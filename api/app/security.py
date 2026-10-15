import os
import base64
import hashlib
import secrets
from pathlib import Path
from datetime import datetime, timedelta
from typing import Optional
import bcrypt
from jose import jwt
from cryptography.fernet import Fernet

# Diretório para armazenar segredos
SECRETS_DIR = Path(os.getenv("SECRETS_DIR", "/data/secrets"))
SECRETS_DIR.mkdir(parents=True, exist_ok=True)

# Caminhos para os arquivos de segredos
SECRET_KEY_FILE = SECRETS_DIR / "secret_key.txt"
FERNET_KEY_FILE = SECRETS_DIR / "fernet_key.txt"


def get_or_create_secret_key() -> str:
    """Carrega ou gera uma SECRET_KEY segura (32 bytes para HS256)"""
    if SECRET_KEY_FILE.exists():
        with open(SECRET_KEY_FILE, "r") as f:
            return f.read().strip()
    
    # Gerar nova chave (32 bytes = 256 bits para HS256)
    key = secrets.token_urlsafe(32)
    with open(SECRET_KEY_FILE, "w") as f:
        f.write(key)
    # Proteger arquivo (só leitura para owner)
    os.chmod(SECRET_KEY_FILE, 0o600)
    return key


def get_or_create_fernet_key() -> bytes:
    """Carrega ou gera uma chave Fernet segura (32 bytes para AES-256)"""
    if FERNET_KEY_FILE.exists():
        with open(FERNET_KEY_FILE, "rb") as f:
            return f.read()
    
    # Gerar nova chave Fernet (32 bytes)
    key = Fernet.generate_key()
    with open(FERNET_KEY_FILE, "wb") as f:
        f.write(key)
    os.chmod(FERNET_KEY_FILE, 0o600)
    return key


# Carregar chaves uma vez
SECRET_KEY = get_or_create_secret_key()
FERNET_KEY = get_or_create_fernet_key()

ALGORITHM = "HS256"
ACCESS_TOKEN_EXPIRE_MINUTES = 60 * 24 * 7  # 7 dias

# Usar chave Fernet direta (32 bytes já é o formato certo)
def get_cipher():
    return Fernet(FERNET_KEY)

def verify_password(plain_password: str, hashed_password: str) -> bool:
    try:
        return bcrypt.checkpw(plain_password.encode('utf-8'), hashed_password.encode('utf-8'))
    except Exception:
        return False

def get_password_hash(password: str) -> str:
    pwd_bytes = password.encode('utf-8')[:72]
    salt = bcrypt.gensalt()
    return bcrypt.hashpw(pwd_bytes, salt).decode('utf-8')

def encrypt_password(plain_password: str) -> str:
    cipher = get_cipher()
    return cipher.encrypt(plain_password.encode()).decode()

def decrypt_password(cipher_text: str) -> str:
    cipher = get_cipher()
    return cipher.decrypt(cipher_text.encode()).decode()

def create_access_token(data: dict, expires_delta: Optional[timedelta] = None):
    to_encode = data.copy()
    if expires_delta:
        expire = datetime.utcnow() + expires_delta
    else:
        expire = datetime.utcnow() + timedelta(minutes=ACCESS_TOKEN_EXPIRE_MINUTES)
    to_encode.update({"exp": expire})
    return jwt.encode(to_encode, SECRET_KEY, algorithm=ALGORITHM)

def decode_access_token(token: str) -> Optional[dict]:
    try:
        payload = jwt.decode(token, SECRET_KEY, algorithms=[ALGORITHM])
        return payload
    except Exception:
        return None
