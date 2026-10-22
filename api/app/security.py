import os
import json
import base64
import hashlib
import secrets
import logging
import threading
from pathlib import Path
from datetime import datetime, timedelta, timezone
from typing import Optional, List, Dict
import bcrypt
from jose import jwt
from cryptography.fernet import Fernet, MultiFernet
from cryptography.hazmat.primitives.asymmetric import ed25519
from cryptography.hazmat.primitives import serialization
from argon2 import PasswordHasher
from argon2.exceptions import VerifyMismatchError, InvalidHashError
import pyotp

logger = logging.getLogger("bipdesk.security")

def utcnow():
    return datetime.now(timezone.utc).replace(tzinfo=None)

# Diretório para armazenar segredos persistentes (volume Docker)
SECRETS_DIR = Path(os.getenv("SECRETS_DIR", "/data/secrets"))
try:
    SECRETS_DIR.mkdir(parents=True, exist_ok=True)
except Exception:
    pass

SECRET_KEY_FILE = SECRETS_DIR / "secret_key.txt"
FERNET_KEY_FILE = SECRETS_DIR / "fernet_key.txt"
SERVER_ED25519_KEY_FILE = SECRETS_DIR / "server_ed25519.key"
SERVER_ED25519_PUB_FILE = SECRETS_DIR / "server_ed25519.pub"

# ----------------- Secret Key (JWT HS256) -----------------

def load_or_init_secret_key() -> str:
    env_secret = os.getenv("SECRET_KEY", "").strip()
    if env_secret:
        return env_secret

    if SECRET_KEY_FILE.exists():
        with open(SECRET_KEY_FILE, "r", encoding="utf-8") as f:
            content = f.read().strip()
            if content:
                return content

    env_mode = os.getenv("ENV", "production").lower()
    if env_mode == "development":
        key = secrets.token_urlsafe(32)
        try:
            with open(SECRET_KEY_FILE, "w", encoding="utf-8") as f:
                f.write(key)
            os.chmod(SECRET_KEY_FILE, 0o600)
        except Exception:
            pass
        logger.warning("[SEGURANÇA] Modo de desenvolvimento ativo: gerada SECRET_KEY temporária.")
        return key

    raise RuntimeError(
        "CRÍTICO: SECRET_KEY não configurada. Defina a variável de ambiente SECRET_KEY "
        "ou crie o arquivo /data/secrets/secret_key.txt com uma chave de 32 bytes de alta entropia. "
        "A aplicação recusa inicializar em produção sem chave definida."
    )

get_secret_key = load_or_init_secret_key
ALGORITHM = "HS256"
ACCESS_TOKEN_EXPIRE_MINUTES = 15  # 15 minutos conforme Seção 5

# ----------------- MultiFernet -----------------

def load_or_init_fernet_keys() -> List[bytes]:
    keys_env = os.getenv("FERNET_KEYS", "").strip()
    if keys_env:
        return [k.strip().encode() for k in keys_env.split(",") if k.strip()]

    single_key = os.getenv("FERNET_KEY", "").strip()
    if single_key:
        return [single_key.encode()]

    if FERNET_KEY_FILE.exists():
        with open(FERNET_KEY_FILE, "rb") as f:
            lines = [line.strip() for line in f.readlines() if line.strip()]
            if lines:
                return lines

    env_mode = os.getenv("ENV", "production").lower()
    if env_mode == "development":
        key = Fernet.generate_key()
        try:
            with open(FERNET_KEY_FILE, "wb") as f:
                f.write(key + b"\n")
            os.chmod(FERNET_KEY_FILE, 0o600)
        except Exception:
            pass
        logger.warning("[SEGURANÇA] Modo de desenvolvimento: gerada chave Fernet temporária.")
        return [key]

    raise RuntimeError(
        "CRÍTICO: Nenhuma chave Fernet configurada. Defina FERNET_KEYS na variável de ambiente "
        "ou crie /data/secrets/fernet_key.txt com pelo menos uma chave válida gerada por Fernet.generate_key()."
    )

def get_cipher() -> MultiFernet:
    keys = load_or_init_fernet_keys()
    fernet_instances = [Fernet(k) for k in keys]
    return MultiFernet(fernet_instances)

def encrypt_password(plain_password: str) -> str:
    cipher = get_cipher()
    return cipher.encrypt(plain_password.encode()).decode()

def decrypt_password(cipher_text: str) -> str:
    cipher = get_cipher()
    return cipher.decrypt(cipher_text.encode()).decode()

def rotate_encrypted_password(cipher_text: str) -> str:
    cipher = get_cipher()
    decrypted = cipher.decrypt(cipher_text.encode())
    return cipher.encrypt(decrypted).decode()

# ----------------- Argon2id & Senhas de Usuário (Seção 5) -----------------

_argon2_hasher = PasswordHasher(time_cost=3, memory_cost=65536, parallelism=4, hash_len=32)

def get_password_hash(password: str) -> str:
    """Gera hash seguro com Argon2id"""
    return _argon2_hasher.hash(password)

def verify_password(plain_password: str, hashed_password: str) -> bool:
    """Verifica senha com Argon2id, com fallback transparente para bcrypt legado"""
    if not hashed_password or not plain_password:
        return False
    # Formato Argon2id: $argon2id$...
    if hashed_password.startswith("$argon2"):
        try:
            return _argon2_hasher.verify(hashed_password, plain_password)
        except (VerifyMismatchError, InvalidHashError):
            return False
        except Exception:
            return False
    # Formato bcrypt: $2b$ ou $2a$
    if hashed_password.startswith("$2"):
        try:
            return bcrypt.checkpw(plain_password.encode('utf-8'), hashed_password.encode('utf-8'))
        except Exception:
            return False
    return False

# ----------------- TOTP (RFC 6238) (Seção 5) -----------------

def generate_totp_secret() -> str:
    return pyotp.random_base32()

def verify_totp(secret: str, code: str) -> bool:
    if not secret or not code:
        return False
    try:
        clean_code = str(code).strip().replace(" ", "")
        totp = pyotp.TOTP(secret)
        return totp.verify(clean_code, valid_window=1)  # Aceita skew de +- 30s
    except Exception:
        return False

def get_totp_uri(username: str, secret: str, issuer: str = "BipDesk") -> str:
    totp = pyotp.TOTP(secret)
    return totp.provisioning_uri(name=username, issuer_name=issuer)

# ----------------- JWT Access & Refresh Token (Seção 5) -----------------

def create_access_token(data: dict, expires_delta: Optional[timedelta] = None) -> str:
    to_encode = data.copy()
    if expires_delta:
        expire = utcnow() + expires_delta
    else:
        expire = utcnow() + timedelta(minutes=ACCESS_TOKEN_EXPIRE_MINUTES)
    to_encode.update({"exp": expire})
    secret = load_or_init_secret_key()
    return jwt.encode(to_encode, secret, algorithm=ALGORITHM)

def decode_access_token(token: str) -> Optional[dict]:
    try:
        secret = load_or_init_secret_key()
        payload = jwt.decode(token, secret, algorithms=[ALGORITHM], options={"verify_aud": False})
        return payload
    except Exception:
        return None

def generate_refresh_token() -> str:
    return secrets.token_urlsafe(48)

# ----------------- Ed25519 do Servidor (Assinatura e Pinning) -----------------

_server_privkey: Optional[ed25519.Ed25519PrivateKey] = None
_server_pubkey_hex: Optional[str] = None
_key_lock = threading.Lock()

def load_or_init_server_signing_key() -> ed25519.Ed25519PrivateKey:
    global _server_privkey, _server_pubkey_hex
    with _key_lock:
        if _server_privkey is not None:
            return _server_privkey

        # Verifica arquivo de chave
        if SERVER_ED25519_KEY_FILE.exists():
            try:
                with open(SERVER_ED25519_KEY_FILE, "rb") as f:
                    _server_privkey = serialization.load_pem_private_key(f.read(), password=None)
                pub_bytes = _server_privkey.public_key().public_bytes(
                    encoding=serialization.Encoding.Raw,
                    format=serialization.PublicFormat.Raw
                )
                _server_pubkey_hex = pub_bytes.hex()
                return _server_privkey
            except Exception as e:
                logger.warning(f"Erro ao ler chave Ed25519 existente: {e}")

        # Se não existe, gera nova chave
        _server_privkey = ed25519.Ed25519PrivateKey.generate()
        pem = _server_privkey.private_bytes(
            encoding=serialization.Encoding.PEM,
            format=serialization.PrivateFormat.PKCS8,
            encryption_algorithm=serialization.NoEncryption()
        )
        try:
            with open(SERVER_ED25519_KEY_FILE, "wb") as f:
                f.write(pem)
            os.chmod(SERVER_ED25519_KEY_FILE, 0o600)
            
            pub_bytes = _server_privkey.public_key().public_bytes(
                encoding=serialization.Encoding.Raw,
                format=serialization.PublicFormat.Raw
            )
            _server_pubkey_hex = pub_bytes.hex()
            with open(SERVER_ED25519_PUB_FILE, "w", encoding="utf-8") as f:
                f.write(_server_pubkey_hex)
        except Exception as e:
            logger.warning(f"Não foi possível salvar chave Ed25519 em disco: {e}")
            pub_bytes = _server_privkey.public_key().public_bytes(
                encoding=serialization.Encoding.Raw,
                format=serialization.PublicFormat.Raw
            )
            _server_pubkey_hex = pub_bytes.hex()

        return _server_privkey

def get_server_public_key_hex() -> str:
    global _server_pubkey_hex
    if _server_pubkey_hex is None:
        load_or_init_server_signing_key()
    return _server_pubkey_hex

def sign_canonical(canonical_str: str) -> str:
    """Assina uma string canônica usando a chave privada Ed25519 do servidor. Retorna hex."""
    priv = load_or_init_server_signing_key()
    sig = priv.sign(canonical_str.encode("utf-8"))
    return sig.hex()

def verify_host_signature(public_key_hex: str, canonical_str: str, signature_hex: str) -> bool:
    """Verifica uma assinatura Ed25519 gerada pelo host. Retorna True se válida."""
    if not public_key_hex or not canonical_str or not signature_hex:
        return False
    try:
        pub_bytes = bytes.fromhex(public_key_hex)
        sig_bytes = bytes.fromhex(signature_hex)
        if len(pub_bytes) != 32 or len(sig_bytes) != 64:
            return False
        pub = ed25519.Ed25519PublicKey.from_public_bytes(pub_bytes)
        pub.verify(sig_bytes, canonical_str.encode("utf-8"))
        return True
    except Exception:
        return False

# ----------------- Formato de Envelope Canônico (Seção 3) -----------------

def make_canonical_string(msg_type: str, machine_id: str, timestamp: int, nonce: str, payload: dict) -> str:
    """
    Constrói a representação canônica rigorosa para assinatura Ed25519:
    type:machine_id:timestamp:nonce:canonical_json_payload
    """
    canonical_payload = json.dumps(payload, sort_keys=True, separators=(',', ':'))
    return f"{msg_type}:{machine_id}:{timestamp}:{nonce}:{canonical_payload}"

# ----------------- Anti-Replay Nonce Cache (Seção 3) -----------------

class NonceCache:
    """Cache thread-safe para detecção e rejeição de nonces repetidos com janela de expiração"""
    def __init__(self, ttl_seconds: int = 300):
        self.ttl = ttl_seconds
        self._nonces: Dict[str, int] = {}
        self._lock = threading.Lock()

    def check_and_record(self, nonce: str, now: int) -> bool:
        if not nonce or len(nonce) < 8:
            return False
        with self._lock:
            # Purga nonces expirados
            expired = [k for k, ts in self._nonces.items() if now - ts > self.ttl]
            for k in expired:
                del self._nonces[k]

            if nonce in self._nonces:
                return False  # Replay detectado!

            self._nonces[nonce] = now
            return True

    def clear(self):
        with self._lock:
            self._nonces.clear()

global_nonce_cache = NonceCache(ttl_seconds=300)

def is_valid_timestamp(ts: int, now: int, max_skew_seconds: int = 120) -> bool:
    """Verifica se o timestamp está dentro da janela permitida (+- 120s)"""
    return abs(now - ts) <= max_skew_seconds