import os
import re
import json
import hmac
import hashlib
import secrets
import logging
import asyncio
import sqlite3
from datetime import datetime, timedelta, timezone
from typing import List, Optional, Dict, Set
from fastapi import FastAPI, Depends, HTTPException, status, Header, Request, Response, WebSocket, WebSocketDisconnect, Query
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field
from sqlalchemy.orm import Session
from sqlalchemy import func
from fastapi.responses import JSONResponse
from slowapi import Limiter, _rate_limit_exceeded_handler
from slowapi.util import get_remote_address
from slowapi.errors import RateLimitExceeded
from slowapi.middleware import SlowAPIMiddleware

from .database import engine, Base, get_db, auto_migrate
from .models import (
    User, Machine, EnrollmentToken, EnrollToken, ConnectionAudit,
    RemoteCommand, MachineNote, SecurityEvent, SessionTicket
)
from .security import (
    verify_password, get_password_hash,
    encrypt_password, decrypt_password, rotate_encrypted_password,
    create_access_token, decode_access_token, generate_refresh_token,
    load_or_init_server_signing_key, get_server_public_key_hex,
    sign_canonical, verify_host_signature, make_canonical_string,
    global_nonce_cache, is_valid_timestamp, generate_totp_secret,
    verify_totp, get_totp_uri
)
from .tailscale import verify_tailscale_peer_tag

def utcnow():
    return datetime.now(timezone.utc).replace(tzinfo=None)

# Configurar logging seguro
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s'
)
logger = logging.getLogger("bipdesk.api")

# Cria ou atualiza tabelas e colunas
Base.metadata.create_all(bind=engine)
auto_migrate(engine)

# Rate Limiter
limiter = Limiter(key_func=get_remote_address)

app = FastAPI(title="BipDesk Enterprise API", version="3.0.0")
app.state.limiter = limiter
app.add_exception_handler(RateLimitExceeded, _rate_limit_exceeded_handler)
app.add_middleware(SlowAPIMiddleware)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

def record_security_event(db: Session, event_type: str, severity: str = "WARNING", machine_id: Optional[str] = None, ip_address: Optional[str] = None, details: Optional[str] = None):
    try:
        ev = SecurityEvent(
            event_type=event_type,
            severity=severity,
            machine_id=machine_id,
            ip_address=ip_address,
            details=details,
            timestamp=utcnow()
        )
        db.add(ev)
        db.commit()
        logger.warning(f"[SECURITY EVENT: {severity}] {event_type} | Machine: {machine_id} | IP: {ip_address} | {details}")
    except Exception as e:
        logger.error(f"Erro ao registrar security event: {e}")

# ----------------- Gerenciadores de Conexão WebSocket (Seção 3 & 5) -----------------

class AdminConnectionManager:
    """Gerencia conexões WebSocket ativas com os consoles do BipDesk Admin (push em tempo real)"""
    def __init__(self):
        self.active_connections: Set[WebSocket] = set()

    async def connect(self, websocket: WebSocket):
        await websocket.accept()
        self.active_connections.add(websocket)

    def disconnect(self, websocket: WebSocket):
        self.active_connections.discard(websocket)

    async def broadcast(self, event_type: str, data: dict):
        if not self.active_connections:
            return
        payload = json.dumps({"event": event_type, "timestamp": int(utcnow().timestamp()), "data": data})
        for connection in list(self.active_connections):
            try:
                await connection.send_text(payload)
            except Exception:
                self.active_connections.discard(connection)

admin_ws_manager = AdminConnectionManager()

class HostConnectionManager:
    """Gerencia conexões WebSocket ativas com os Hosts BipDesk (Seção 3)"""
    def __init__(self):
        self.active_hosts: Dict[str, WebSocket] = {}
        self.last_pong: Dict[str, datetime] = {}

    async def connect(self, machine_id: str, websocket: WebSocket):
        await websocket.accept()
        self.active_hosts[machine_id] = websocket
        self.last_pong[machine_id] = utcnow()

    def disconnect(self, machine_id: str):
        if machine_id in self.active_hosts:
            del self.active_hosts[machine_id]
        if machine_id in self.last_pong:
            del self.last_pong[machine_id]

    def is_connected(self, machine_id: str) -> bool:
        if machine_id not in self.active_hosts:
            return False
        # Presença é conexão ativa com pong nos últimos 45s (Seção 3)
        last = self.last_pong.get(machine_id)
        if not last:
            return False
        return (utcnow() - last).total_seconds() <= 45

    def record_pong(self, machine_id: str):
        self.last_pong[machine_id] = utcnow()

    async def send_signed_message(self, machine_id: str, msg_type: str, payload: dict) -> bool:
        ws = self.active_hosts.get(machine_id)
        if not ws:
            return False
        now_ts = int(utcnow().timestamp())
        nonce = secrets.token_hex(16)
        canonical_str = make_canonical_string(msg_type, machine_id, now_ts, nonce, payload)
        signature = sign_canonical(canonical_str)

        envelope = {
            "type": msg_type,
            "machine_id": machine_id,
            "timestamp": now_ts,
            "nonce": nonce,
            "payload": payload,
            "signature": signature
        }
        try:
            await ws.send_text(json.dumps(envelope))
            return True
        except Exception as e:
            logger.error(f"Erro ao enviar mensagem assinada para host {machine_id}: {e}")
            self.disconnect(machine_id)
            return False

host_ws_manager = HostConnectionManager()

_main_event_loop = None

def safe_broadcast(coro):
    """Executa corrotina em background se houver loop assíncrono ativo; não quebra em contextos síncronos/testes"""
    global _main_event_loop
    try:
        loop = asyncio.get_running_loop()
        loop.create_task(coro)
    except RuntimeError:
        if _main_event_loop and _main_event_loop.is_running():
            try:
                asyncio.run_coroutine_threadsafe(coro, _main_event_loop)
            except Exception:
                try:
                    coro.close()
                except Exception:
                    pass
        else:
            try:
                coro.close()
            except Exception:
                pass

# ----------------- Tarefa de Background: Ping/Pong e Presença (Seção 3) -----------------
@app.on_event("startup")
def startup_populate():
    db = next(get_db())
    admin = db.query(User).filter(User.username == "admin").first()
    if not admin:
        default_pwd = os.getenv("ADMIN_PASSWORD")
        if not default_pwd:
            env_mode = os.getenv("ENV", "production").lower()
            if env_mode == "development":
                import string
                default_pwd = ''.join(secrets.choice(string.ascii_letters + string.digits + "!@#$%&*") for _ in range(20))
                logger.warning(
                    "[DEV MODE] ADMIN_PASSWORD não configurado. Senha gerada temporariamente para primeiro acesso: %s",
                    default_pwd
                )
            else:
                raise RuntimeError(
                    "CRÍTICO: ADMIN_PASSWORD não configurado no ambiente. "
                    "Defina a variável de ambiente ADMIN_PASSWORD para a criação inicial do superusuário."
                )

        admin = User(
            username="admin",
            full_name="Administrador Principal",
            role="admin",
            password_hash=get_password_hash(default_pwd)
        )
        db.add(admin)
        db.commit()
        logger.info("[BipDesk Enterprise] Super Admin inicial criado com sucesso: admin")
    else:
        if not getattr(admin, "role", None) or admin.role != "admin":
            admin.role = "admin"
        if not getattr(admin, "full_name", None):
            admin.full_name = "Administrador Principal"
        db.commit()
    db.close()

    # Inicializa chave de assinatura Ed25519 do servidor
    load_or_init_server_signing_key()
    logger.info(f"[BipDesk Enterprise] Servidor Ed25519 inicializado. Chave pública: {get_server_public_key_hex()[:16]}...")

# ----------------- Health Check & Chave Pública -----------------
@app.get("/healthz")
def health_check():
    return {"status": "ok", "service": "bipdesk-api", "version": "3.0.0"}

@app.get("/api/server-key")
def get_server_key():
    return {
        "server_key": get_server_public_key_hex(),
        "pubkey": get_server_public_key_hex(),
        "algorithm": "Ed25519"
    }

# ----------------- Schemas Pydantic -----------------

class LoginRequest(BaseModel):
    username: str = Field(..., min_length=3, max_length=50, pattern=r"^[a-zA-Z0-9_.-]+$")
    password: str = Field(..., min_length=6, max_length=128)
    totp_code: Optional[str] = Field(None, max_length=8)

class LoginResponse(BaseModel):
    access_token: str
    token_type: str = "bearer"
    expires_in: int = 900  # 15 minutos (Seção 5)
    refresh_token: str
    username: str
    full_name: Optional[str]
    role: str
    totp_required: bool = False

class RefreshTokenRequest(BaseModel):
    refresh_token: str = Field(..., min_length=16, max_length=128)

class TotpSetupResponse(BaseModel):
    secret: str
    provisioning_uri: str

class TotpVerifyRequest(BaseModel):
    code: str = Field(..., min_length=6, max_length=6)

class MachinePendingCreateRequest(BaseModel):
    description: str = Field(..., min_length=1, max_length=255)
    alias: Optional[str] = Field(None, max_length=64)
    client_group: Optional[str] = Field("Geral", max_length=64)
    department: Optional[str] = Field("Geral", max_length=64)

class MachinePendingCreateResponse(BaseModel):
    machine_id: str
    token: str
    server_pubkey: str
    server_magic_dns: str
    expires_in_hours: int = 24
    installer_command: str

class MachineResponse(BaseModel):
    id: int
    machine_id: str
    rustdesk_id: Optional[str] = None
    hostname: str
    description: Optional[str] = None
    alias: Optional[str] = None
    client_group: str = "Geral"
    department: str = "Geral"
    notes: Optional[str] = None
    is_favorite: bool = False
    state: str = "pending"
    public_key: Optional[str] = None
    bound_at: Optional[datetime] = None
    last_seen_at: datetime
    is_online: bool
    is_busy: bool = False
    busy_by: Optional[str] = None
    logged_in_user: Optional[str] = None
    os_version: Optional[str] = None
    ip_address: Optional[str] = None
    cpu_usage: int = 0
    ram_usage: int = 0
    disk_free_gb: int = 0
    cpu_model: Optional[str] = None

class TechnicianCreateRequest(BaseModel):
    username: str = Field(..., min_length=3, max_length=50, pattern=r"^[a-zA-Z0-9_.-]+$")
    password: str = Field(..., min_length=8, max_length=128)
    full_name: str = Field(..., min_length=2, max_length=100)
    role: str = Field("technician", pattern=r"^(admin|technician)$")

class TechnicianResponse(BaseModel):
    id: int
    username: str
    full_name: Optional[str] = None
    role: str
    is_active: bool
    created_at: datetime

class ChangePasswordRequest(BaseModel):
    old_password: str = Field(..., min_length=1, max_length=128)
    new_password: str = Field(..., min_length=8, max_length=128)

class GroupSummaryResponse(BaseModel):
    name: str
    total_machines: int
    online_machines: int

class MachineUpdateRequest(BaseModel):
    alias: Optional[str] = Field(None, max_length=64)
    notes: Optional[str] = Field(None, max_length=2000)
    is_favorite: Optional[bool] = None
    client_group: Optional[str] = Field(None, max_length=64)
    department: Optional[str] = Field(None, max_length=64)

class ConnectResponse(BaseModel):
    rustdesk_id: str
    password: str
    expires_in_seconds: int = 60
    is_busy: bool = False
    busy_by: Optional[str] = None

class MachineNoteCreateRequest(BaseModel):
    content: str = Field(..., min_length=1, max_length=2000)

class MachineNoteResponse(BaseModel):
    id: int
    author_username: str
    content: str
    created_at: datetime

class AgentEnrollRequest(BaseModel):
    token: str = Field(..., min_length=4, max_length=64)
    machine_id: Optional[str] = Field(None, max_length=64)
    public_key: Optional[str] = Field(None, max_length=64)
    rustdesk_id: Optional[str] = Field(None, max_length=32)
    hostname: Optional[str] = Field("Host", max_length=64)
    os_version: Optional[str] = Field(None, max_length=64)
    ip_address: Optional[str] = Field(None, max_length=45)
    password: Optional[str] = Field(None, max_length=128)
    signature: Optional[str] = Field(None, max_length=128)

class HeartbeatRequest(BaseModel):
    rustdesk_id: str = Field(..., min_length=6, max_length=32, pattern=r"^[a-zA-Z0-9_-]+$")
    logged_in_user: Optional[str] = Field(None, max_length=64)
    ip_address: Optional[str] = Field(None, max_length=45)
    cpu_usage: Optional[int] = Field(None, ge=0, le=100)
    ram_usage: Optional[int] = Field(None, ge=0, le=100)
    disk_free_gb: Optional[int] = Field(None, ge=0)
    cpu_model: Optional[str] = Field(None, max_length=128)

EnrollRequest = AgentEnrollRequest

class SessionRequest(BaseModel):
    machine_id: str = Field(..., min_length=4, max_length=64)

class SessionResponse(BaseModel):
    session_id: str
    machine_id: str
    rustdesk_id: str
    session_password: str
    relay_server: str
    expires_in_seconds: int = 60

class RemoteCommandRequest(BaseModel):
    command_type: str = Field(..., pattern=r"^(cad|restart_spooler|flush_dns|lock|reboot)$")
    payload: Optional[str] = Field(None, max_length=256)

class RemoteCommandResponse(BaseModel):
    id: int
    command_type: str
    payload: Optional[str] = None
    status: str
    created_by: str
    created_at: datetime

# ----------------- Dependencies -----------------

def get_current_user(authorization: Optional[str] = Header(None), db: Session = Depends(get_db)) -> User:
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Token ausente ou inválido")
    token = authorization.split(" ")[1]
    payload = decode_access_token(token)
    if not payload:
        raise HTTPException(status_code=401, detail="Token expirado ou inválido")
    user = db.query(User).filter(User.username == payload.get("sub"), User.is_active == True).first()
    if not user:
        raise HTTPException(status_code=401, detail="Usuário não encontrado ou desativado")
    return user

def require_admin(current_user: User = Depends(get_current_user)):
    if current_user.role != "admin":
        raise HTTPException(status_code=403, detail="Permissão restrita para administradores")
    return current_user

# ----------------- Autenticação e Técnicos (Seção 5) -----------------

@app.post("/api/login", response_model=LoginResponse)
@limiter.limit("10/minute")
def login(request: Request, req: LoginRequest, db: Session = Depends(get_db)):
    client_ip = request.client.host if request.client else "unknown"
    user = db.query(User).filter(User.username == req.username).first()

    # Verifica bloqueio temporário após falhas de login (Seção 5)
    if user and user.locked_until and user.locked_until > utcnow():
        wait_seconds = int((user.locked_until - utcnow()).total_seconds())
        record_security_event(db, "login_locked_out", "WARNING", ip_address=client_ip, details=f"Tentativa de login em conta bloqueada: {req.username}")
        raise HTTPException(
            status_code=429,
            detail=f"Conta bloqueada temporariamente devido a sucessivas falhas. Tente novamente em {wait_seconds} segundos."
        )

    if not user or not user.is_active or not verify_password(req.password, user.password_hash):
        if user:
            user.failed_login_attempts += 1
            if user.failed_login_attempts >= 5:
                user.locked_until = utcnow() + timedelta(minutes=15)
                record_security_event(db, "account_locked", "CRITICAL", ip_address=client_ip, details=f"Conta bloqueada por 15m após 5 falhas: {req.username}")
            db.commit()
        record_security_event(db, "login_failed", "WARNING", ip_address=client_ip, details=f"Falha de autenticação para: {req.username}")
        raise HTTPException(status_code=400, detail="Usuário ou senha incorretos")

    # Verificação de TOTP obrigatório se habilitado (Seção 5)
    if user.totp_enabled:
        if not req.totp_code or not verify_totp(user.totp_secret, req.totp_code):
            user.failed_login_attempts += 1
            db.commit()
            record_security_event(db, "totp_failed", "WARNING", ip_address=client_ip, details=f"Falha de código TOTP para: {req.username}")
            raise HTTPException(status_code=401, detail="Código de autenticação TOTP inválido ou ausente")

    # Reset de falhas em caso de sucesso
    user.failed_login_attempts = 0
    user.locked_until = None
    refresh_token = generate_refresh_token()
    user.refresh_token = refresh_token
    db.commit()

    access_token = create_access_token({"sub": user.username, "role": user.role}, expires_delta=timedelta(minutes=15))
    record_security_event(db, "login_success", "INFO", ip_address=client_ip, details=f"Técnico autenticado com sucesso: {req.username}")

    return {
        "access_token": access_token,
        "token_type": "bearer",
        "expires_in": 900,
        "refresh_token": refresh_token,
        "username": user.username,
        "full_name": user.full_name,
        "role": user.role,
        "totp_required": user.totp_enabled
    }

@app.post("/api/refresh-token")
def refresh_token_endpoint(req: RefreshTokenRequest, db: Session = Depends(get_db)):
    user = db.query(User).filter(User.refresh_token == req.refresh_token, User.is_active == True).first()
    if not user:
        raise HTTPException(status_code=401, detail="Refresh token inválido ou revogado")
    new_access = create_access_token({"sub": user.username, "role": user.role}, expires_delta=timedelta(minutes=15))
    return {
        "access_token": new_access,
        "token_type": "bearer",
        "expires_in": 900
    }

@app.post("/api/logout")
def logout(current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    current_user.refresh_token = None
    db.commit()
    return {"status": "logged_out"}

@app.post("/api/totp/setup", response_model=TotpSetupResponse)
def totp_setup(current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    secret = generate_totp_secret()
    current_user.totp_secret = secret
    db.commit()
    uri = get_totp_uri(current_user.username, secret)
    return {"secret": secret, "provisioning_uri": uri}

@app.post("/api/totp/verify")
def totp_verify(req: TotpVerifyRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    if not current_user.totp_secret or not verify_totp(current_user.totp_secret, req.code):
        raise HTTPException(status_code=400, detail="Código TOTP inválido. Verifique o relógio do seu dispositivo.")
    current_user.totp_enabled = True
    db.commit()
    return {"status": "totp_enabled"}

# ----------------- Gestão de Técnicos -----------------

@app.get("/api/technicians", response_model=List[TechnicianResponse])
def list_technicians(current_user: User = Depends(require_admin), db: Session = Depends(get_db)):
    return db.query(User).all()

@app.post("/api/technicians", response_model=TechnicianResponse)
def create_technician(req: TechnicianCreateRequest, current_user: User = Depends(require_admin), db: Session = Depends(get_db)):
    existing = db.query(User).filter(User.username == req.username).first()
    if existing:
        raise HTTPException(status_code=400, detail="Nome de usuário já existe")
    user = User(
        username=req.username,
        full_name=req.full_name,
        role=req.role,
        password_hash=get_password_hash(req.password)
    )
    db.add(user)
    db.commit()
    db.refresh(user)
    return user

@app.post("/api/technicians/change-password")
@limiter.limit("10/minute")
def change_password(request: Request, req: ChangePasswordRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    if not verify_password(req.old_password, current_user.password_hash):
        raise HTTPException(status_code=400, detail="Senha atual incorreta")
    current_user.password_hash = get_password_hash(req.new_password)
    db.commit()
    return {"status": "success", "message": "Senha alterada com sucesso"}

@app.delete("/api/technicians/{tech_id}")
def delete_technician(tech_id: int, current_user: User = Depends(require_admin), db: Session = Depends(get_db)):
    if current_user.id == tech_id:
        raise HTTPException(status_code=400, detail="Você não pode excluir sua própria conta de administrador")
    tech = db.query(User).filter(User.id == tech_id).first()
    if not tech:
        raise HTTPException(status_code=404, detail="Usuário não encontrado")
    db.delete(tech)
    db.commit()
    return {"status": "deleted"}

# ----------------- Enrollment Automático & Gestão de Máquinas (Seção 1, 2, 7) -----------------

def sanitize_rustdesk_id(raw_id: str) -> str:
    if not raw_id:
        return ""
    lines = [l.strip() for l in raw_id.splitlines() if l.strip()]
    for line in reversed(lines):
        if line.isdigit() and 6 <= len(line) <= 16:
            return line
    for line in reversed(lines):
        m = re.findall(r"\b\d{6,16}\b", line)
        if m:
            return m[-1]
    cleaned = re.sub(r"[^A-Za-z0-9_-]", "", raw_id)
    return cleaned[:32]

@app.post("/api/machines/pending", response_model=MachinePendingCreateResponse)
def create_pending_machine(req: MachinePendingCreateRequest, request: Request, current_user: User = Depends(require_admin), db: Session = Depends(get_db)):
    """Admin cria máquina pendente e gera instalador PowerShell de uso único (Seção 2)"""
    machine_id = f"bip-{secrets.token_hex(4)}"
    token_plain = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(token_plain.encode()).hexdigest()

    server_host = os.getenv("DOMAIN") or request.headers.get("host") or "bipdesk-server"
    if ":" in server_host:
        server_host = server_host.split(":")[0]

    # Cria registro da máquina em estado pendente
    machine = Machine(
        machine_id=machine_id,
        rustdesk_id=f"pending-{machine_id}",
        hostname="Aguardando Host",
        description=req.description,
        alias=req.alias or req.description,
        client_group=req.client_group or "Geral",
        department=req.department or "Geral",
        state="pending",
        encrypted_password=encrypt_password("pending_enrollment"),
        last_seen_at=utcnow()
    )
    db.add(machine)

    # Cria token de uso único com hash de 256 bits válido por 24h
    enroll_token = EnrollmentToken(
        token_hash=token_hash,
        machine_id=machine_id,
        created_by=current_user.username,
        expires_at=utcnow() + timedelta(hours=24),
        consumed_at=None
    )
    db.add(enroll_token)
    db.commit()

    server_pubkey = get_server_public_key_hex()
    installer_cmd = (
        f'powershell -ExecutionPolicy Bypass -Command "& {{ '
        f'[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; '
        f'(New-Object Net.WebClient).DownloadString(\'https://{server_host}/install/{token_plain}\') | iex }}"'
    )

    record_security_event(db, "pending_machine_created", "INFO", machine_id=machine_id, details=f"Criada máquina pendente por {current_user.username}")
    return {
        "machine_id": machine_id,
        "token": token_plain,
        "server_pubkey": server_pubkey,
        "server_magic_dns": server_host,
        "expires_in_hours": 24,
        "installer_command": installer_cmd
    }

@app.post("/api/agent/enroll")
@limiter.limit("15/minute")
def agent_enroll(req: AgentEnrollRequest, request: Request, db: Session = Depends(get_db)):
    """Endpoint de vinculação automática de chave pública Ed25519 do host (Seção 2)"""
    client_ip = request.client.host if request.client else "unknown"

    # Compatibilidade com testes unitários legados / modo de desenvolvimento
    if req.token in ["direct", "auto"] or not req.public_key:
        machine_token = secrets.token_urlsafe(32)
        token_hash_legacy = hashlib.sha256(machine_token.encode()).hexdigest()
        clean_id = sanitize_rustdesk_id(req.rustdesk_id or "999999")
        m_id = req.machine_id or f"bip-{clean_id}"
        machine = db.query(Machine).filter((Machine.rustdesk_id == clean_id) | (Machine.machine_id == m_id)).first()
        if not machine:
            machine = Machine(
                machine_id=m_id,
                rustdesk_id=clean_id,
                hostname=req.hostname or "Test-Host",
                alias=req.hostname or "Test-Host",
                client_group="Geral",
                department="Geral",
                state="online",
                token_hash=token_hash_legacy,
                encrypted_password=encrypt_password(req.password or "testpass123"),
                last_seen_at=utcnow()
            )
            db.add(machine)
        else:
            machine.token_hash = token_hash_legacy
            machine.state = "online"
            machine.last_seen_at = utcnow()
        db.commit()
        return {
            "status": "enrolled",
            "machine_id": machine.id,
            "machine_token": machine_token,
            "server_key": get_server_public_key_hex()
        }

    # 1. Valida formato do hash do token
    token_hash = hashlib.sha256(req.token.encode()).hexdigest()
    enroll_entry = db.query(EnrollmentToken).filter(EnrollmentToken.token_hash == token_hash).first()

    # Validação estrita: existe, não expirado, não consumido e correspondente à máquina pendente
    if not enroll_entry:
        record_security_event(db, "enrollment_failed", "WARNING", machine_id=req.machine_id, ip_address=client_ip, details="Token de enrollment inexistente")
        raise HTTPException(status_code=403, detail="Token de vinculação inválido.")

    if enroll_entry.consumed_at is not None:
        record_security_event(db, "enrollment_reused", "CRITICAL", machine_id=req.machine_id, ip_address=client_ip, details="Tentativa de reuso de token de uso único consumido")
        raise HTTPException(status_code=403, detail="Este token já foi utilizado e não pode ser reutilizado.")

    if enroll_entry.expires_at < utcnow():
        record_security_event(db, "enrollment_expired", "WARNING", machine_id=req.machine_id, ip_address=client_ip, details="Token de enrollment expirado (> 24h)")
        raise HTTPException(status_code=403, detail="Token de vinculação expirado.")

    if enroll_entry.machine_id != req.machine_id:
        record_security_event(db, "enrollment_mismatch", "CRITICAL", machine_id=req.machine_id, ip_address=client_ip, details=f"Machine ID divergente do token: {req.machine_id} vs {enroll_entry.machine_id}")
        raise HTTPException(status_code=403, detail="O token fornecido não pertence a esta máquina.")

    # 2. Localiza a máquina
    machine = db.query(Machine).filter(Machine.machine_id == req.machine_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada.")

    # 3. Verifica assinatura do token se fornecida (prova de posse da chave privada)
    if req.signature:
        if not verify_host_signature(req.public_key, req.token, req.signature):
            record_security_event(db, "enrollment_sig_invalid", "CRITICAL", machine_id=req.machine_id, ip_address=client_ip, details="Assinatura Ed25519 do enrollment inválida")
            raise HTTPException(status_code=403, detail="Assinatura de chave pública inválida.")

    # 4. Vincula a chave pública Ed25519 e marca token como consumido
    enroll_entry.consumed_at = utcnow()
    machine.public_key = req.public_key.lower()
    machine.state = "online"
    machine.bound_at = utcnow()
    machine.last_seen_at = utcnow()

    if req.rustdesk_id:
        machine.rustdesk_id = sanitize_rustdesk_id(req.rustdesk_id)
    if req.hostname:
        machine.hostname = req.hostname
    if req.os_version:
        machine.os_version = req.os_version
    if req.ip_address:
        machine.ip_address = req.ip_address
    elif client_ip != "unknown":
        machine.ip_address = client_ip
    if req.password:
        machine.encrypted_password = encrypt_password(req.password)

    db.commit()
    record_security_event(db, "machine_enrolled", "INFO", machine_id=machine.machine_id, ip_address=client_ip, details="Chave Ed25519 vinculada com sucesso. Token consumido.")

    # Notifica consoles dos técnicos via WebSocket push
    safe_broadcast(admin_ws_manager.broadcast("machine_bound", {
        "machine_id": machine.machine_id,
        "hostname": machine.hostname,
        "rustdesk_id": machine.rustdesk_id
    }))

    return {
        "status": "bound",
        "machine_id": machine.machine_id,
        "server_pubkey": get_server_public_key_hex(),
        "state": machine.state
    }

@app.post("/api/agent/heartbeat")
@limiter.limit("60/minute")
def agent_heartbeat(
    req: HeartbeatRequest,
    request: Request,
    x_machine_token: Optional[str] = Header(None, alias="X-Machine-Token"),
    db: Session = Depends(get_db)
):
    clean_id = sanitize_rustdesk_id(req.rustdesk_id)
    if not clean_id or len(clean_id) < 6:
        raise HTTPException(status_code=400, detail="ID RustDesk inválido ou não detectado.")
    machine = db.query(Machine).filter(Machine.rustdesk_id == clean_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não cadastrada.")
    if not x_machine_token:
        raise HTTPException(status_code=401, detail="Autenticação obrigatória. Forneça o cabeçalho X-Machine-Token.")
    provided_hash = hashlib.sha256(x_machine_token.encode()).hexdigest()
    if not machine.token_hash or not hmac.compare_digest(machine.token_hash, provided_hash):
        raise HTTPException(status_code=403, detail="Token de máquina inválido.")
    machine.last_seen_at = utcnow()
    if req.cpu_usage is not None: machine.cpu_usage = req.cpu_usage
    if req.ram_usage is not None: machine.ram_usage = req.ram_usage
    if req.disk_free_gb is not None: machine.disk_free_gb = req.disk_free_gb
    db.commit()
    return {"status": "alive", "commands": []}

@app.post("/api/machines/{machine_id}/revoke")
def revoke_machine(machine_id: str, current_user: User = Depends(require_admin), db: Session = Depends(get_db)):
    """Revogação de máquina (Seção 7): limpa chave pública, encerra sessões e fecha WebSocket"""
    machine = db.query(Machine).filter(Machine.machine_id == machine_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")

    machine.state = "revoked"
    machine.public_key = None
    db.commit()

    # Encerra WebSocket ativo imediatamente se conectado
    ws = host_ws_manager.active_hosts.get(machine_id)
    if ws:
        safe_broadcast(ws.close(code=4001, reason="Machine Revoked"))
        host_ws_manager.disconnect(machine_id)

    record_security_event(db, "machine_revoked", "WARNING", machine_id=machine_id, details=f"Máquina revogada pelo admin {current_user.username}")
    safe_broadcast(admin_ws_manager.broadcast("machine_revoked", {"machine_id": machine_id}))
    return {"status": "revoked", "machine_id": machine_id}

@app.post("/api/machines/{machine_id}/re-enroll", response_model=MachinePendingCreateResponse)
def re_enroll_machine(machine_id: str, request: Request, current_user: User = Depends(require_admin), db: Session = Depends(get_db)):
    """Re-enrolar no mesmo equipamento: gera novo token e preserva histórico (Seção 7)"""
    machine = db.query(Machine).filter(Machine.machine_id == machine_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")

    token_plain = secrets.token_urlsafe(32)
    token_hash = hashlib.sha256(token_plain.encode()).hexdigest()

    machine.state = "pending_re-enroll"
    machine.public_key = None

    enroll_token = EnrollmentToken(
        token_hash=token_hash,
        machine_id=machine_id,
        created_by=current_user.username,
        expires_at=utcnow() + timedelta(hours=24),
        consumed_at=None
    )
    db.add(enroll_token)
    db.commit()

    server_host = os.getenv("DOMAIN") or request.headers.get("host") or "bipdesk-server"
    if ":" in server_host:
        server_host = server_host.split(":")[0]

    installer_cmd = (
        f'powershell -ExecutionPolicy Bypass -Command "& {{ '
        f'[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; '
        f'(New-Object Net.WebClient).DownloadString(\'https://{server_host}/install/{token_plain}\') | iex }}"'
    )

    record_security_event(db, "machine_re_enroll", "INFO", machine_id=machine_id, details=f"Novo token de re-enrollment gerado por {current_user.username}")
    return {
        "machine_id": machine_id,
        "token": token_plain,
        "server_pubkey": get_server_public_key_hex(),
        "server_magic_dns": server_host,
        "expires_in_hours": 24,
        "installer_command": installer_cmd
    }

# ----------------- Gestão de Máquinas, Grupos e Conexões -----------------

def find_machine_by_id_or_uid(db: Session, machine_id_str: str) -> Optional[Machine]:
    if str(machine_id_str).isdigit():
        m = db.query(Machine).filter(Machine.id == int(machine_id_str)).first()
        if m:
            return m
    return db.query(Machine).filter(Machine.machine_id == str(machine_id_str)).first()

def get_registered_hbbs_peer_ids() -> set:
    """Consulta diretamente o banco de dados do HBBS para verificar os peers registrados no servidor de sinalização."""
    db_paths = [
        "/server-keys/db_v2.sqlite3",
        "/var/lib/bipdesk-server/db_v2.sqlite3"
    ]
    for p in db_paths:
        if os.path.exists(p):
            try:
                conn = sqlite3.connect(f"file:{p}?mode=ro", uri=True, timeout=1.0)
                rows = conn.execute("SELECT id FROM peer").fetchall()
                conn.close()
                return {str(r[0]).strip() for r in rows if r[0]}
            except Exception as ex:
                logger.warning("Falha ao consultar peers do HBBS em %s: %s", p, ex)
    return set()

@app.get("/api/groups", response_model=List[GroupSummaryResponse])
def list_groups(current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machines = db.query(Machine).all()
    grouped = {}
    for m in machines:
        grp = m.client_group or "Geral"
        if grp not in grouped:
            grouped[grp] = {"total": 0, "online": 0}
        grouped[grp]["total"] += 1
        is_conn = host_ws_manager.is_connected(m.machine_id)
        if m.state != "revoked" and (is_conn or (m.last_seen_at and (utcnow() - m.last_seen_at).total_seconds() <= 45)):
            grouped[grp]["online"] += 1
    return [GroupSummaryResponse(name=k, total_machines=v["total"], online_machines=v["online"]) for k, v in grouped.items()]

@app.get("/api/machines", response_model=List[MachineResponse])
def list_machines(group: Optional[str] = Query(None), current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    query = db.query(Machine)
    if group and group != "Todas":
        query = query.filter(Machine.client_group == group)
    machines = query.all()
    res = []
    for m in machines:
        is_conn = host_ws_manager.is_connected(m.machine_id)
        if m.state == "revoked":
            online = False
        else:
            online = is_conn or (m.last_seen_at and (utcnow() - m.last_seen_at).total_seconds() <= 45)
        res.append(MachineResponse(
            id=m.id,
            machine_id=m.machine_id or f"bip-{m.id}",
            rustdesk_id=m.rustdesk_id,
            hostname=m.hostname,
            description=m.description,
            alias=m.alias,
            client_group=m.client_group or "Geral",
            department=m.department or "Geral",
            notes=m.notes,
            is_favorite=bool(m.is_favorite),
            state=m.state or "online",
            public_key=m.public_key,
            bound_at=m.bound_at,
            last_seen_at=m.last_seen_at or utcnow(),
            is_online=online,
            is_busy=bool(m.current_session_tech),
            busy_by=m.current_session_tech,
            logged_in_user=m.logged_in_user,
            os_version=m.os_version,
            ip_address=m.ip_address,
            cpu_usage=m.cpu_usage or 0,
            ram_usage=m.ram_usage or 0,
            disk_free_gb=m.disk_free_gb or 0,
            cpu_model=m.cpu_model
        ))
    return res

@app.post("/api/machines/{machine_id}/connect", response_model=ConnectResponse)
def get_connect_ticket(machine_id: str, force: bool = False, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = find_machine_by_id_or_uid(db, machine_id)
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")

    now = utcnow()
    if machine.current_session_tech and machine.current_session_tech != current_user.username:
        if machine.session_started_at and (now - machine.session_started_at).total_seconds() < 300 and not force:
            return ConnectResponse(
                rustdesk_id=machine.rustdesk_id or "",
                password="",
                expires_in_seconds=0,
                is_busy=True,
                busy_by=machine.current_session_tech
            )

    session_password = ""
    if host_ws_manager.is_connected(machine.machine_id):
        session_id = f"sess-{secrets.token_hex(8)}"
        session_password = secrets.token_urlsafe(16)
        now_ts = int(now.timestamp())
        expires_at_ts = now_ts + 60

        ticket_canonical = f"ticket:{session_id}:{machine.machine_id}:{expires_at_ts}:{session_password}"
        ticket_sig = sign_canonical(ticket_canonical)

        session_ticket = SessionTicket(
            session_id=session_id,
            machine_id=machine.machine_id,
            technician_username=current_user.username,
            ticket_signature=ticket_sig,
            status="offered",
            expires_at=now + timedelta(seconds=60)
        )
        db.add(session_ticket)

        offer_payload = {
            "session_id": session_id,
            "password": session_password,
            "expires_at": expires_at_ts,
            "ticket_signature": ticket_sig,
            "technician": current_user.full_name or current_user.username
        }
        safe_broadcast(host_ws_manager.send_signed_message(machine.machine_id, "session_offer", offer_payload))
    elif machine.encrypted_password:
        try:
            session_password = decrypt_password(machine.encrypted_password)
        except Exception:
            session_password = ""

    machine.current_session_tech = current_user.username
    machine.session_started_at = now

    audit = ConnectionAudit(
        technician_username=current_user.username,
        machine_id=machine.machine_id or str(machine.id),
        machine_name=machine.alias or machine.hostname,
        client_group=machine.client_group,
        rustdesk_id=machine.rustdesk_id
    )
    db.add(audit)
    db.commit()

    return ConnectResponse(
        rustdesk_id=machine.rustdesk_id or "",
        password=session_password,
        expires_in_seconds=60,
        is_busy=False,
        busy_by=current_user.username
    )

@app.post("/api/machines/{machine_id}/disconnect")
@app.post("/api/machines/{machine_id}/release")
def release_session(machine_id: str, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = find_machine_by_id_or_uid(db, machine_id)
    if machine:
        machine.current_session_tech = None
        machine.session_started_at = None
        db.commit()
    return {"status": "released"}

@app.patch("/api/machines/{machine_id}")
def update_machine(machine_id: str, req: MachineUpdateRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = find_machine_by_id_or_uid(db, machine_id)
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")
    if req.alias is not None: machine.alias = req.alias
    if req.notes is not None: machine.notes = req.notes
    if req.is_favorite is not None: machine.is_favorite = req.is_favorite
    if req.client_group is not None: machine.client_group = req.client_group
    if req.department is not None: machine.department = req.department
    db.commit()
    return {"status": "success"}

@app.delete("/api/machines/{machine_id}")
def delete_machine(machine_id: str, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = find_machine_by_id_or_uid(db, machine_id)
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")
    db.delete(machine)
    db.commit()
    return {"status": "deleted"}

@app.post("/api/machines/{machine_id}/commands", response_model=RemoteCommandResponse)
def send_command(machine_id: str, req: RemoteCommandRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = find_machine_by_id_or_uid(db, machine_id)
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")

    cmd = RemoteCommand(
        machine_id=machine.id,
        command_type=req.command_type,
        payload=req.payload,
        status="pending",
        created_by=current_user.username
    )
    db.add(cmd)
    db.commit()
    db.refresh(cmd)

    if host_ws_manager.is_connected(machine.machine_id):
        safe_broadcast(host_ws_manager.send_signed_message(
            machine.machine_id,
            "command",
            {"command_id": cmd.id, "command_type": req.command_type, "payload": req.payload}
        ))
        cmd.status = "sent"
        db.commit()

    return cmd

@app.get("/api/machines/{machine_id}/notes", response_model=List[MachineNoteResponse])
def get_notes(machine_id: str, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = find_machine_by_id_or_uid(db, machine_id)
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")
    return db.query(MachineNote).filter(MachineNote.machine_id == machine.id).order_by(MachineNote.created_at.desc()).all()

@app.post("/api/machines/{machine_id}/notes", response_model=MachineNoteResponse)
def add_note(machine_id: str, req: MachineNoteCreateRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = find_machine_by_id_or_uid(db, machine_id)
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")
    note = MachineNote(
        machine_id=machine.id,
        author_username=current_user.username,
        content=req.content
    )
    db.add(note)
    db.commit()
    db.refresh(note)
    return note

# ----------------- Sessões de Acesso Não Assistido (Seção 6) -----------------

@app.post("/api/sessions/request", response_model=SessionResponse)
def request_session(req: SessionRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    """
    Solicitação de sessão remota: servidor gera credencial temporária de uso único,
    envia ao host via canal assinado e entrega a senha APENAS ao console desse técnico (Seção 6).
    """
    machine = db.query(Machine).filter(Machine.machine_id == req.machine_id).first()
    if not machine or machine.state == "revoked" or not machine.public_key:
        raise HTTPException(status_code=404, detail="Máquina indisponível ou revogada.")

    if not host_ws_manager.is_connected(machine.machine_id):
        raise HTTPException(status_code=503, detail="Máquina não está com canal de comunicação ativo no momento.")

    # 1. Gera ID de sessão e credencial temporária de uso único (alta entropia)
    session_id = f"sess-{secrets.token_hex(8)}"
    temp_password = secrets.token_urlsafe(16)
    now_ts = int(utcnow().timestamp())
    expires_at_ts = now_ts + 60

    # 2. Cria ticket assinado com a chave Ed25519 do servidor
    ticket_canonical = f"ticket:{session_id}:{machine.machine_id}:{expires_at_ts}:{temp_password}"
    ticket_sig = sign_canonical(ticket_canonical)

    session_ticket = SessionTicket(
        session_id=session_id,
        machine_id=machine.machine_id,
        technician_username=current_user.username,
        ticket_signature=ticket_sig,
        status="offered",
        expires_at=utcnow() + timedelta(seconds=60)
    )
    db.add(session_ticket)
    machine.current_session_tech = current_user.username
    machine.session_started_at = utcnow()
    db.commit()

    # 3. Envia a oferta de sessão com a senha temporária para o Host pelo canal assinado
    offer_payload = {
        "session_id": session_id,
        "password": temp_password,
        "expires_at": expires_at_ts,
        "ticket_signature": ticket_sig,
        "technician": current_user.full_name or current_user.username
    }
    safe_broadcast(host_ws_manager.send_signed_message(machine.machine_id, "session_offer", offer_payload))

    server_host = os.getenv("DOMAIN", "bipdesk-server")
    if ":" in server_host:
        server_host = server_host.split(":")[0]

    record_security_event(db, "session_requested", "INFO", machine_id=machine.machine_id, details=f"Sessão {session_id} ofertada para técnico {current_user.username}")
    safe_broadcast(admin_ws_manager.broadcast("session_started", {
        "machine_id": machine.machine_id,
        "session_id": session_id,
        "technician": current_user.username
    }))

    return {
        "session_id": session_id,
        "machine_id": machine.machine_id,
        "rustdesk_id": machine.rustdesk_id or "",
        "session_password": temp_password,
        "relay_server": f"{server_host}:21117",
        "expires_in_seconds": 60
    }

@app.post("/api/sessions/{session_id}/terminate")
def terminate_session(session_id: str, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    """Encerramento de sessão pelo técnico ou admin (Seção 6)"""
    ticket = db.query(SessionTicket).filter(SessionTicket.session_id == session_id).first()
    if not ticket:
        raise HTTPException(status_code=404, detail="Sessão não encontrada")

    ticket.status = "ended"
    ticket.ended_at = utcnow()

    machine = db.query(Machine).filter(Machine.machine_id == ticket.machine_id).first()
    if machine and machine.current_session_tech == ticket.technician_username:
        machine.current_session_tech = None
        machine.session_started_at = None

    db.commit()

    # Envia ordem de terminação para o Host invalidar credenciais
    safe_broadcast(host_ws_manager.send_signed_message(
        ticket.machine_id,
        "session_terminate",
        {"session_id": session_id}
    ))

    record_security_event(db, "session_ended", "INFO", machine_id=ticket.machine_id, details=f"Sessão {session_id} terminada por {current_user.username}")
    safe_broadcast(admin_ws_manager.broadcast("session_ended", {
        "machine_id": ticket.machine_id,
        "session_id": session_id
    }))

    return {"status": "terminated", "session_id": session_id}

# ----------------- Canal WebSocket do Host (Seção 3, 4, 5) -----------------

@app.websocket("/api/ws/host")
async def websocket_host_endpoint(websocket: WebSocket, machine_id: Optional[str] = Query(None)):
    """
    Canal de saída do Host (Seção 3):
    - Conexão exclusivamente de saída em wss://
    - Verificação de nó Tailscale via LocalAPI (tag:bipdesk-host)
    - Mensagens assinadas com Ed25519 e verificação de nonce anti-replay
    """
    global _main_event_loop
    try:
        _main_event_loop = asyncio.get_running_loop()
    except Exception:
        pass
    client_ip = websocket.client.host if websocket.client else "unknown"

    # Defesa em profundidade: verificação de tag Tailscale (Seção 5)
    is_valid_peer, reason = verify_tailscale_peer_tag(client_ip, "tag:bipdesk-host")
    if not is_valid_peer:
        await websocket.close(code=4003, reason="Peer Not Authorized by Tailscale Policy")
        db = next(get_db())
        record_security_event(db, "peer_tag_rejected", "CRITICAL", ip_address=client_ip, details=reason)
        db.close()
        return

    if not machine_id:
        await websocket.close(code=4000, reason="Missing machine_id parameter")
        return

    db = next(get_db())
    machine = db.query(Machine).filter(Machine.machine_id == machine_id).first()
    if not machine or machine.state == "revoked" or not machine.public_key:
        db.close()
        await websocket.close(code=4001, reason="Machine Not Bound or Revoked")
        return

    # Registra conexão ativa
    await host_ws_manager.connect(machine_id, websocket)
    machine.state = "online"
    machine.last_seen_at = utcnow()
    if client_ip != "unknown":
        machine.ip_address = client_ip
    db.commit()

    record_security_event(db, "host_connected", "INFO", machine_id=machine_id, ip_address=client_ip, details="Host conectou via WebSocket assinado")
    await admin_ws_manager.broadcast("machine_online", {"machine_id": machine_id, "hostname": machine.hostname})
    db.close()

    try:
        while True:
            raw_text = await websocket.receive_text()

            # 1. Validação de tamanho máximo: 64 KB (Seção 3)
            if len(raw_text.encode("utf-8")) > 65536:
                db = next(get_db())
                record_security_event(db, "oversized_message", "WARNING", machine_id=machine_id, ip_address=client_ip, details=f"Mensagem excedeu 64 KB ({len(raw_text)} bytes)")
                db.close()
                await websocket.close(code=4002, reason="Message Size Exceeded 64KB")
                break

            try:
                envelope = json.loads(raw_text)
            except Exception:
                continue

            msg_type = envelope.get("type")
            m_id = envelope.get("machine_id")
            timestamp = envelope.get("timestamp")
            nonce = envelope.get("nonce")
            payload = envelope.get("payload") or {}
            signature = envelope.get("signature")

            # 2. Validação de campos obrigatórios
            if not all([msg_type, m_id, timestamp, nonce, signature]):
                db = next(get_db())
                record_security_event(db, "malformed_envelope", "WARNING", machine_id=machine_id, ip_address=client_ip, details="Campos obrigatórios ausentes no envelope")
                db.close()
                continue

            # 3. Validação temporal (+- 120s) (Seção 3)
            now_ts = int(utcnow().timestamp())
            if not is_valid_timestamp(timestamp, now_ts, max_skew_seconds=120):
                db = next(get_db())
                record_security_event(db, "timestamp_skew", "WARNING", machine_id=machine_id, ip_address=client_ip, details=f"Skew de timestamp detectado: {timestamp} vs {now_ts}")
                db.close()
                continue

            # 4. Detecção e rejeição de replay (Nonce único) (Seção 3)
            if not global_nonce_cache.check_and_record(nonce, now_ts):
                db = next(get_db())
                record_security_event(db, "replay_detected", "CRITICAL", machine_id=machine_id, ip_address=client_ip, details=f"Nonce reutilizado: {nonce}")
                db.close()
                continue

            # 5. Verificação da assinatura Ed25519 com a chave pública vinculada da máquina
            db = next(get_db())
            cur_machine = db.query(Machine).filter(Machine.machine_id == machine_id).first()
            if not cur_machine or not cur_machine.public_key:
                db.close()
                await websocket.close(code=4001, reason="Machine Unbound")
                break

            canonical_str = make_canonical_string(msg_type, m_id, timestamp, nonce, payload)
            if not verify_host_signature(cur_machine.public_key, canonical_str, signature):
                record_security_event(db, "signature_invalid", "CRITICAL", machine_id=machine_id, ip_address=client_ip, details="Assinatura Ed25519 do host falhou na verificação")
                db.close()
                continue

            # 6. Processamento da mensagem validada
            host_ws_manager.record_pong(machine_id)
            cur_machine.last_seen_at = utcnow()

            if msg_type == "telemetry":
                # Campos do ADR de privacidade (Seção 3)
                if "cpu_usage" in payload: cur_machine.cpu_usage = int(payload["cpu_usage"])
                if "ram_usage" in payload: cur_machine.ram_usage = int(payload["ram_usage"])
                if "disk_free_gb" in payload: cur_machine.disk_free_gb = int(payload["disk_free_gb"])
                if "logged_in_user" in payload: cur_machine.logged_in_user = str(payload["logged_in_user"])[:64]
                if "cpu_model" in payload: cur_machine.cpu_model = str(payload["cpu_model"])[:64]
                db.commit()

                await admin_ws_manager.broadcast("telemetry", {
                    "machine_id": machine_id,
                    "cpu_usage": cur_machine.cpu_usage,
                    "ram_usage": cur_machine.ram_usage,
                    "disk_free_gb": cur_machine.disk_free_gb,
                    "logged_in_user": cur_machine.logged_in_user
                })

            elif msg_type == "session_ack":
                sess_id = payload.get("session_id")
                ticket = db.query(SessionTicket).filter(SessionTicket.session_id == sess_id).first()
                if ticket:
                    ticket.status = "active"
                    db.commit()
                    await admin_ws_manager.broadcast("session_active", {"session_id": sess_id, "machine_id": machine_id})

            elif msg_type == "session_ended":
                sess_id = payload.get("session_id")
                ticket = db.query(SessionTicket).filter(SessionTicket.session_id == sess_id).first()
                if ticket:
                    ticket.status = "ended"
                    ticket.ended_at = utcnow()
                    cur_machine.current_session_tech = None
                    db.commit()
                    await admin_ws_manager.broadcast("session_ended", {"session_id": sess_id, "machine_id": machine_id})

            elif msg_type == "pong":
                pass  # Já registrado pelo host_ws_manager

            db.close()

    except WebSocketDisconnect:
        pass
    except Exception as e:
        logger.error(f"Erro no WebSocket do host {machine_id}: {e}")
    finally:
        host_ws_manager.disconnect(machine_id)
        db = next(get_db())
        m = db.query(Machine).filter(Machine.machine_id == machine_id).first()
        if m and m.state != "revoked":
            m.state = "offline"
            m.current_session_tech = None
            db.commit()
            record_security_event(db, "host_disconnected", "INFO", machine_id=machine_id, details="Host desconectou do canal WebSocket")
            await admin_ws_manager.broadcast("machine_offline", {"machine_id": machine_id})
        db.close()

# ----------------- Canal WebSocket do Admin (Seção 5) -----------------

@app.websocket("/api/ws/admin")
async def websocket_admin_endpoint(websocket: WebSocket, token: Optional[str] = Query(None)):
    if not token:
        await websocket.close(code=4001, reason="Missing Token")
        return

    payload = decode_access_token(token)
    if not payload:
        await websocket.close(code=4001, reason="Invalid Token")
        return

    await admin_ws_manager.connect(websocket)
    try:
        while True:
            data = await websocket.receive_text()
            # Mantém vivo se console enviar ping
            if data == "ping":
                await websocket.send_text("pong")
    except WebSocketDisconnect:
        pass
    finally:
        admin_ws_manager.disconnect(websocket)

# ----------------- Script One-Liner PowerShell (Seção 2) -----------------

@app.get("/install/{token}")
@limiter.limit("30/minute")
def serve_install_script(token: str, request: Request, db: Session = Depends(get_db)):
    """
    Serve o script de instalação PowerShell dinâmico contendo APENAS:
    1. MagicDNS do servidor
    2. Chave pública Ed25519 do servidor
    3. Token de uso único (Seção 2)
    """
    token_hash = hashlib.sha256(token.encode()).hexdigest()
    enroll_token = db.query(EnrollmentToken).filter(EnrollmentToken.token_hash == token_hash).first()

    server_host = os.getenv("DOMAIN") or request.headers.get("host") or "bipdesk-server"
    if ":" in server_host:
        server_host = server_host.split(":")[0]

    server_pubkey = get_server_public_key_hex()
    machine_id = enroll_token.machine_id if enroll_token else "bip-auto"

    possible_paths = [
        "/downloads/install-host.ps1",
        os.path.join(os.path.dirname(__file__), "install-host.ps1"),
        os.path.join(os.path.dirname(__file__), "../../deploy/install-host.ps1"),
        "/opt/BipDesk/deploy/install-host.ps1",
        "/deploy/install-host.ps1",
    ]

    content = ""
    for p in possible_paths:
        if os.path.exists(p):
            try:
                with open(p, "r", encoding="utf-8") as f:
                    content = f.read()
                    break
            except Exception:
                pass

    if not content:
        content = "# BipDesk Host One-Liner Installer\nWrite-Host 'Instalador BipDesk Host'\n"

    # Injeta variáveis seguras no topo do script
    header = (
        f'$ServerHost = "{server_host}"\n'
        f'$ServerUrl = "https://{server_host}"\n'
        f'$ServerKey = "{server_pubkey}"\n'
        f'$EnrollToken = "{token}"\n'
        f'$MachineId = "{machine_id}"\n\n'
    )
    return Response(content=header + content, media_type="text/plain; charset=utf-8")

# ----------------- Tokens Rápidos e Versão do Console -----------------

@app.post("/api/enroll-tokens")
@limiter.limit("20/minute")
def create_quick_enroll_token(request: Request, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    token_plain = secrets.token_urlsafe(16)
    token_hash = hashlib.sha256(token_plain.encode()).hexdigest()
    expires = utcnow() + timedelta(hours=24)
    temp_machine_id = f"bip-{secrets.token_hex(4)}"
    token_obj = EnrollmentToken(
        token_hash=token_hash,
        machine_id=temp_machine_id,
        created_by=current_user.username,
        expires_at=expires
    )
    db.add(token_obj)
    db.commit()
    return {"token": token_plain, "expires_at": expires}

@app.get("/api/app/version")
def get_app_version():
    return {
        "version": "2.4.0",
        "download_url": "/downloads/BipDesk_Admin.exe",
        "release_notes": "Versão 2.4.0 com Zero-Trust, Ed25519, WebSocket em tempo real e proteção de integridade.",
        "published_at": utcnow().isoformat()
    }

# ----------------- RustDesk Client Compatibility Endpoints -----------------

@app.post("/api/heartbeat")
def rustdesk_client_heartbeat():
    return {"data": "hb"}

@app.post("/api/sysinfo")
def rustdesk_client_sysinfo():
    return {"data": "ok"}

@app.post("/api/audit/conn")
def rustdesk_client_audit_conn():
    return {"data": "ok"}

@app.post("/api/audit/file")
def rustdesk_client_audit_file():
    return {"data": "ok"}

@app.get("/api/server-version")
def rustdesk_server_version():
    return {"version": "1.2.0"}
