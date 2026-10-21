import os
import base64
import hashlib
import secrets
import logging
from pathlib import Path
from datetime import datetime, timedelta
from typing import Optional, List
import bcrypt
from jose import jwt
from cryptography.fernet import Fernet, MultiFernet

logger = logging.getLogger("bipdesk.security")

# Diretório para armazenar segredos persistentes (volume Docker)
SECRETS_DIR = Path(os.getenv("SECRETS_DIR", "/data/secrets"))
try:
    SECRETS_DIR.mkdir(parents=True, exist_ok=True)
except Exception:
    pass

SECRET_KEY_FILE = SECRETS_DIR / "secret_key.txt"
FERNET_KEY_FILE = SECRETS_DIR / "fernet_key.txt"

def load_or_init_secret_key() -> str:
    """
    Carrega a SECRET_KEY de ambiente ou de arquivo seguro.
    Em produção, se ausente, levanta RuntimeError imediato impedindo inicialização insegura.
    Em desenvolvimento ('ENV=development'), gera uma chave temporária e registra aviso.
    """
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


def load_or_init_fernet_keys() -> List[bytes]:
    """
    Carrega as chaves Fernet para uso com MultiFernet (suporte a rotação transparente).
    A primeira chave da lista é utilizada para encriptar novos dados.
    Chaves subsequentes continuam aptas a decodificar dados legados.
    """
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


get_secret_key = load_or_init_secret_key

ALGORITHM = "HS256"
ACCESS_TOKEN_EXPIRE_MINUTES = 60 * 12  # 12 horas conforme especificação e restrição de tokens efêmeros

def get_cipher() -> MultiFernet:
    """Retorna instância de MultiFernet para suporte a rotação de chaves sem perda de dados."""
    keys = load_or_init_fernet_keys()
    fernet_instances = [Fernet(k) for k in keys]
    return MultiFernet(fernet_instances)

def verify_password(plain_password: str, hashed_password: str) -> bool:
    try:
        return bcrypt.checkpw(plain_password.encode('utf-8'), hashed_password.encode('utf-8'))
    except Exception:
        return False

def get_password_hash(password: str) -> str:
    # Trunca em 72 bytes por especificação estrita do bcrypt
    pwd_bytes = password.encode('utf-8')[:72]
    salt = bcrypt.gensalt(rounds=12)
    return bcrypt.hashpw(pwd_bytes, salt).decode('utf-8')

def encrypt_password(plain_password: str) -> str:
    cipher = get_cipher()
    return cipher.encrypt(plain_password.encode()).decode()

def decrypt_password(cipher_text: str) -> str:
    cipher = get_cipher()
    return cipher.decrypt(cipher_text.encode()).decode()

def rotate_encrypted_password(cipher_text: str) -> str:
    """Re-encripta a credencial usando a chave primária mais recente do MultiFernet."""
    cipher = get_cipher()
    decrypted = cipher.decrypt(cipher_text.encode())
    return cipher.encrypt(decrypted).decode()

def create_access_token(data: dict, expires_delta: Optional[timedelta] = None) -> str:
    to_encode = data.copy()
    if expires_delta:
        expire = datetime.utcnow() + expires_delta
    else:
        expire = datetime.utcnow() + timedelta(minutes=ACCESS_TOKEN_EXPIRE_MINUTES)
    to_encode.update({"exp": expire})
    secret = load_or_init_secret_key()
    return jwt.encode(to_encode, secret, algorithm=ALGORITHM)

def decode_access_token(token: str) -> Optional[dict]:
    try:
        secret = load_or_init_secret_key()
        # Força estritamente HS256 e impede algoritmo 'none' ou chaves públicas assimétricas
        payload = jwt.decode(token, secret, algorithms=[ALGORITHM], options={"verify_aud": False})
        return payload
    except Exception:
        return None