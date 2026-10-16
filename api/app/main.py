import os
import re
import secrets
import logging
from datetime import datetime, timedelta
from typing import List, Optional
from fastapi import FastAPI, Depends, HTTPException, status, Header, Request, Response
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from sqlalchemy.orm import Session
from sqlalchemy import func
from fastapi.responses import JSONResponse
from slowapi import Limiter, _rate_limit_exceeded_handler
from slowapi.util import get_remote_address
from slowapi.errors import RateLimitExceeded
from slowapi.middleware import SlowAPIMiddleware

from .database import engine, Base, get_db, auto_migrate
from .models import User, Machine, EnrollToken, ConnectionAudit, RemoteCommand, MachineNote
from .security import (
    verify_password, get_password_hash,
    encrypt_password, decrypt_password,
    create_access_token, decode_access_token
)

# Configurar logging
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

app = FastAPI(title="BipDesk Enterprise API", version="2.1.2")
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

@app.on_event("startup")
def startup_populate():
    db = next(get_db())
    admin = db.query(User).filter(User.username == "admin").first()
    if not admin:
        # NUNCA usar senha default hardcoded em produção
        default_pwd = os.getenv("ADMIN_PASSWORD")
        if not default_pwd:
            # Gerar senha aleatória se não fornecida
            import string
            default_pwd = ''.join(secrets.choice(string.ascii_letters + string.digits + "!@#$%&*") for _ in range(20))
            logger.warning(
                "ADMIN_PASSWORD não configurado. Senha gerada automaticamente: %s. "
                "Defina a variável de ambiente para usar uma senha específica.",
                default_pwd
            )

        admin = User(
            username="admin",
            full_name="Administrador Principal",
            role="admin",
            password_hash=get_password_hash(default_pwd)
        )
        db.add(admin)
        db.commit()
        logger.info("[BipDesk Enterprise] Super Admin criado: admin")
    else:
        if not getattr(admin, "role", None) or admin.role != "admin":
            admin.role = "admin"
        if not getattr(admin, "full_name", None):
            admin.full_name = "Administrador Principal"
        db.commit()
    db.close()

# ----------------- Schemas -----------------
class LoginRequest(BaseModel):
    username: str
    password: str

class LoginResponse(BaseModel):
    access_token: str
    token_type: str = "bearer"
    username: str
    full_name: Optional[str]
    role: str

class TechnicianCreateRequest(BaseModel):
    username: str
    password: str
    full_name: str
    role: str = "technician"

class TechnicianResponse(BaseModel):
    id: int
    username: str
    full_name: Optional[str]
    role: str
    is_active: bool
    created_at: datetime

class ChangePasswordRequest(BaseModel):
    old_password: str
    new_password: str

class MachineResponse(BaseModel):
    id: int
    rustdesk_id: str
    hostname: str
    alias: Optional[str]
    client_group: str
    department: str
    notes: Optional[str]
    is_favorite: bool
    is_online: bool
    is_busy: bool
    busy_by: Optional[str]
    logged_in_user: Optional[str]
    os_version: Optional[str]
    ip_address: Optional[str]
    last_seen_at: datetime

class GroupSummaryResponse(BaseModel):
    name: str
    total_machines: int
    online_machines: int

class MachineUpdateRequest(BaseModel):
    alias: Optional[str] = None
    notes: Optional[str] = None
    is_favorite: Optional[bool] = None
    client_group: Optional[str] = None
    department: Optional[str] = None

class EnrollRequest(BaseModel):
    token: Optional[str] = "direct"
    rustdesk_id: str
    hostname: str
    username: Optional[str] = None
    os_version: Optional[str] = None
    ip_address: Optional[str] = None
    password: str

class HeartbeatRequest(BaseModel):
    rustdesk_id: str
    logged_in_user: Optional[str] = None
    ip_address: Optional[str] = None

class ConnectResponse(BaseModel):
    rustdesk_id: str
    password: str
    expires_in_seconds: int = 60
    is_busy: bool = False
    busy_by: Optional[str] = None

class RemoteCommandRequest(BaseModel):
    command_type: str # "reboot", "message", "restart_spooler"
    payload: Optional[str] = None

class RemoteCommandResponse(BaseModel):
    id: int
    command_type: str
    payload: Optional[str]
    status: str
    created_by: str
    created_at: datetime

class MachineNoteCreateRequest(BaseModel):
    content: str

class MachineNoteResponse(BaseModel):
    id: int
    author_username: str
    content: str
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
        raise HTTPException(status_code=401, detail="Usuário não encontrado ou inativo")
    return user

def require_admin(current_user: User = Depends(get_current_user)):
    if current_user.role != "admin":
        raise HTTPException(status_code=403, detail="Permissão restrita para administradores")
    return current_user

# ----------------- Autenticação e Técnicos -----------------
@app.post("/api/login", response_model=LoginResponse)
@limiter.limit("15/minute")
def login(request: Request, req: LoginRequest, db: Session = Depends(get_db)):
    user = db.query(User).filter(User.username == req.username).first()
    if not user or not user.is_active or not verify_password(req.password, user.password_hash):
        raise HTTPException(status_code=400, detail="Usuário ou senha incorretos")
    token = create_access_token({"sub": user.username, "role": user.role})
    return {
        "access_token": token,
        "token_type": "bearer",
        "username": user.username,
        "full_name": user.full_name,
        "role": user.role
    }

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

# ----------------- Gestão de Máquinas & Grupos -----------------
@app.get("/api/groups", response_model=List[GroupSummaryResponse])
def list_groups(current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    now = datetime.utcnow()
    machines = db.query(Machine).all()
    grouped = {}
    for m in machines:
        grp = m.client_group or "Geral"
        if grp not in grouped:
            grouped[grp] = {"total": 0, "online": 0}
        grouped[grp]["total"] += 1
        if (now - m.last_seen_at).total_seconds() < 90:
            grouped[grp]["online"] += 1

    return [
        GroupSummaryResponse(
            name=name,
            total_machines=data["total"],
            online_machines=data["online"]
        )
        for name, data in sorted(grouped.items())
    ]

@app.get("/api/machines", response_model=List[MachineResponse])
def list_machines(group: Optional[str] = None, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    query = db.query(Machine)
    if group and group != "Todas":
        query = query.filter(Machine.client_group == group)
    
    machines = query.order_by(Machine.is_favorite.desc(), Machine.last_seen_at.desc()).all()
    result = []
    now_utc = datetime.utcnow()
    for m in machines:
        diff_seconds = (now_utc - m.last_seen_at).total_seconds()
        is_online = diff_seconds < 90
        
        # Concorrência: se sessão foi iniciada há mais de 2h, limpa
        is_busy = False
        busy_by = None
        if m.current_session_tech and m.session_started_at:
            if (now_utc - m.session_started_at).total_seconds() < 7200: # 2 horas
                is_busy = True
                busy_by = m.current_session_tech
            else:
                m.current_session_tech = None
                m.session_started_at = None

        result.append(MachineResponse(
            id=m.id,
            rustdesk_id=m.rustdesk_id,
            hostname=m.hostname,
            alias=m.alias,
            client_group=m.client_group or "Geral",
            department=m.department or "Geral",
            notes=m.notes,
            is_favorite=m.is_favorite,
            is_online=is_online,
            is_busy=is_busy,
            busy_by=busy_by,
            logged_in_user=m.logged_in_user,
            os_version=m.os_version,
            ip_address=m.ip_address,
            last_seen_at=m.last_seen_at
        ))
    # Commit once after processing all machines
    db.commit()
    return result

@app.patch("/api/machines/{machine_id}")
def update_machine(machine_id: int, req: MachineUpdateRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.id == machine_id).first()
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
def delete_machine(machine_id: int, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.id == machine_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")
    db.delete(machine)
    db.commit()
    return {"status": "deleted"}

# ----------------- Conexão e Controle de Concorrência -----------------
@app.post("/api/machines/{machine_id}/connect", response_model=ConnectResponse)
def get_connect_ticket(machine_id: int, force: bool = False, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.id == machine_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")

    now = datetime.utcnow()
    # Verifica se outro técnico já está atendendo
    if machine.current_session_tech and machine.current_session_tech != current_user.username:
        if machine.session_started_at and (now - machine.session_started_at).total_seconds() < 7200 and not force:
            return ConnectResponse(
                rustdesk_id=machine.rustdesk_id,
                password="",
                expires_in_seconds=0,
                is_busy=True,
                busy_by=machine.current_session_tech
            )

    # Bloqueia a máquina para este técnico
    machine.current_session_tech = current_user.username
    machine.session_started_at = now

    audit = ConnectionAudit(
        technician_username=current_user.username,
        machine_id=machine.id,
        machine_name=machine.alias or machine.hostname,
        client_group=machine.client_group,
        rustdesk_id=machine.rustdesk_id
    )
    db.add(audit)
    db.commit()

    decrypted_pwd = decrypt_password(machine.encrypted_password)
    return ConnectResponse(
        rustdesk_id=machine.rustdesk_id,
        password=decrypted_pwd,
        expires_in_seconds=60,
        is_busy=False,
        busy_by=current_user.username
    )

@app.post("/api/machines/{machine_id}/disconnect")
def release_session(machine_id: int, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.id == machine_id).first()
    if machine and machine.current_session_tech == current_user.username:
        machine.current_session_tech = None
        machine.session_started_at = None
        db.commit()
    return {"status": "released"}

# ----------------- Comandos Rápidos Silenciosos -----------------
@app.post("/api/machines/{machine_id}/commands", response_model=RemoteCommandResponse)
def send_command(machine_id: int, req: RemoteCommandRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.id == machine_id).first()
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
    return cmd

# ----------------- Linha do Tempo e Notas Compartilhadas -----------------
@app.get("/api/machines/{machine_id}/notes", response_model=List[MachineNoteResponse])
def get_notes(machine_id: int, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    return db.query(MachineNote).filter(MachineNote.machine_id == machine_id).order_by(MachineNote.created_at.desc()).all()

@app.post("/api/machines/{machine_id}/notes", response_model=MachineNoteResponse)
def add_note(machine_id: int, req: MachineNoteCreateRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    note = MachineNote(
        machine_id=machine_id,
        author_username=current_user.username,
        content=req.content
    )
    db.add(note)
    db.commit()
    db.refresh(note)
    return note

# ----------------- Rotas do Agent / Host -----------------
def sanitize_rustdesk_id(raw_id: Optional[str]) -> str:
    """
    Sanitiza o rustdesk_id garantindo que apenas o identificador numérico/limpo seja aceito.
    Filtra logs de console, mensagens de debug do Flutter/RustDesk e quebras de linha.
    """
    if not raw_id:
        return ""
    
    lines = [line.strip() for line in raw_id.strip().splitlines() if line.strip()]
    if not lines:
        return ""
        
    # 1. Procura de baixo para cima linha contendo exclusivamente dígitos (6 a 16 dígitos)
    for line in reversed(lines):
        if re.fullmatch(r"\d{6,16}", line):
            return line
            
    # 2. Procura padrão de dígitos isolados de baixo para cima
    for line in reversed(lines):
        match = re.search(r"\b(\d{6,16})\b", line)
        if match:
            return match.group(1)
            
    # 3. Fallback: linha curta alfanumérica limpa sem palavras de log
    for line in reversed(lines):
        lower = line.lower()
        if not any(k in lower for k in ("skip", "version", "error", "warn", "rustdesk", ".dll")):
            if re.fullmatch(r"[A-Za-z0-9_-]{6,32}", line):
                return line
                
    # 4. Fallback final: limpa caracteres não-alfanuméricos
    cleaned = re.sub(r"[^A-Za-z0-9_-]", "", raw_id)
    return cleaned[:32]

@app.post("/api/agent/enroll")
def agent_enroll(req: EnrollRequest, request: Request, db: Session = Depends(get_db)):
    clean_id = sanitize_rustdesk_id(req.rustdesk_id)
    if not clean_id or len(clean_id) < 6:
        raise HTTPException(status_code=400, detail="ID RustDesk inválido ou não detectado.")
    req.rustdesk_id = clean_id

    # Higieniza hostname se for multilinhas ou longo
    if req.hostname:
        h_lines = [l.strip() for l in req.hostname.strip().splitlines() if l.strip()]
        req.hostname = h_lines[-1][:64] if h_lines else f"Host-{clean_id}"
    else:
        req.hostname = f"Host-{clean_id}"

    if req.token and req.token not in ["direct", "auto"]:
        token_entry = db.query(EnrollToken).filter(
            EnrollToken.token == req.token,
            EnrollToken.is_used == False,
            EnrollToken.expires_at > datetime.utcnow()
        ).first()
        if not token_entry:
            raise HTTPException(status_code=400, detail="Token de instalação inválido ou expirado")
        token_entry.is_used = True
    
    # Prioriza o IP local real da máquina (ipconfig) reportado pelo agent.
    # Fallback para o IP da conexão HTTP apenas se não foi fornecido.
    reported_ip = (req.ip_address or "").strip()
    client_ip = reported_ip if (reported_ip and not reported_ip.startswith("127.")) else (request.client.host if request.client else None)

    # Verifica se já existe por ID limpo ou legado corrompido que termina com esse ID
    machine = db.query(Machine).filter(Machine.rustdesk_id == req.rustdesk_id).first()
    if not machine:
        machine = db.query(Machine).filter(Machine.rustdesk_id.like(f"%{req.rustdesk_id}")).first()
        if machine:
            machine.rustdesk_id = req.rustdesk_id

    if not machine:
        machine = Machine(
            rustdesk_id=req.rustdesk_id,
            hostname=req.hostname,
            alias=req.hostname,
            client_group="Geral",
            department="Geral",
            logged_in_user=req.username,
            os_version=req.os_version,
            ip_address=client_ip,
            encrypted_password=encrypt_password(req.password),
            last_seen_at=datetime.utcnow()
        )
        db.add(machine)
    else:
        machine.hostname = req.hostname
        if not machine.alias or "skip" in machine.alias or len(machine.alias) > 64:
            machine.alias = req.hostname
        if req.username: machine.logged_in_user = req.username
        if req.os_version: machine.os_version = req.os_version
        if client_ip: machine.ip_address = client_ip
        machine.encrypted_password = encrypt_password(req.password)
        machine.last_seen_at = datetime.utcnow()

    db.commit()
    return {"status": "enrolled", "machine_id": machine.id}

@app.post("/api/agent/heartbeat")
def agent_heartbeat(req: HeartbeatRequest, request: Request, db: Session = Depends(get_db)):
    clean_id = sanitize_rustdesk_id(req.rustdesk_id)
    if not clean_id or len(clean_id) < 6:
        raise HTTPException(status_code=400, detail="ID RustDesk inválido ou não detectado.")
    req.rustdesk_id = clean_id

    machine = db.query(Machine).filter(Machine.rustdesk_id == req.rustdesk_id).first()
    # Auto-healing: se não encontrou por ID limpo, verifica se há registro antigo com o ID no final
    if not machine:
        machine = db.query(Machine).filter(Machine.rustdesk_id.like(f"%{req.rustdesk_id}")).first()
        if machine:
            logger.info("Auto-reparando máquina %s para ID limpo %s", machine.id, req.rustdesk_id)
            machine.rustdesk_id = req.rustdesk_id
            if not machine.alias or "skip" in machine.alias or len(machine.alias) > 64:
                machine.alias = f"Host-{req.rustdesk_id}"
            if not machine.hostname or "skip" in machine.hostname or len(machine.hostname) > 64:
                machine.hostname = f"Host-{req.rustdesk_id}"
            db.commit()

    reported_ip = (req.ip_address or "").strip()
    has_reported_lan_ip = bool(reported_ip and not reported_ip.startswith("127."))
    
    if not machine:
        # Auto-registro em caso de primeiro contato via heartbeat
        default_pwd = os.getenv("HOST_PASSWORD")
        if not default_pwd:
            default_pwd = secrets.token_urlsafe(12)
            logger.info("[BipDesk Enterprise] Senha do host gerada para %s: %s", req.rustdesk_id, default_pwd)
        machine = Machine(
            rustdesk_id=req.rustdesk_id,
            hostname=f"Host-{req.rustdesk_id}",
            alias=f"Host-{req.rustdesk_id}",
            client_group="Geral",
            department="Geral",
            logged_in_user=req.logged_in_user,
            ip_address=reported_ip if has_reported_lan_ip else (request.client.host if request.client else None),
            encrypted_password=encrypt_password(default_pwd),
            last_seen_at=datetime.utcnow()
        )
        db.add(machine)
        db.commit()
        db.refresh(machine)
    
    machine.last_seen_at = datetime.utcnow()
    if req.logged_in_user: machine.logged_in_user = req.logged_in_user
    # Se o agente enviou o IP real da LAN (ipconfig), atualiza.
    # Se não foi fornecido, apenas preenche com o IP de conexão se estiver vazio.
    if has_reported_lan_ip:
        machine.ip_address = reported_ip
    elif not machine.ip_address:
        machine.ip_address = request.client.host if request.client else None

    # Busca comandos pendentes para o agente executar
    pending_cmds = db.query(RemoteCommand).filter(
        RemoteCommand.machine_id == machine.id,
        RemoteCommand.status == "pending"
    ).all()

    cmds_to_return = []
    for c in pending_cmds:
        cmds_to_return.append({
            "id": c.id,
            "type": c.command_type,
            "payload": c.payload
        })
        c.status = "executed"
        c.executed_at = datetime.utcnow()

    db.commit()
    return {"status": "alive", "commands": cmds_to_return}

# ----------------- Script One-Liner PowerShell -----------------
@app.get("/install/{token}")
@limiter.limit("30/minute")
def serve_install_script(token: str, request: Request):
    base_url = f"{request.url.scheme}://{request.url.netloc}"
    possible_paths = [
        os.path.join(os.path.dirname(__file__), "install-host.ps1"),
        os.path.join(os.path.dirname(__file__), "../../deploy/install-host.ps1"),
        "/opt/BipDesk/deploy/install-host.ps1",
        "/deploy/install-host.ps1",
        "/downloads/install-host.ps1",
    ]
    content = ""
    for p in possible_paths:
        if os.path.exists(p):
            try:
                with open(p, "r", encoding="utf-8") as f:
                    c = f.read()
                    if len(c) > 200:
                        content = c
                        break
            except Exception:
                pass

    if not content:
        raise HTTPException(status_code=500, detail="Script de instalação não localizado no servidor.")

    content = content.replace('param(', f'# Injetado automaticamente\n$ServerUrl = "{base_url}"\n$EnrollToken = "{token}"\nparam(')
    return Response(content=content, media_type="text/plain; charset=utf-8")

@app.post("/api/enroll-tokens")
@limiter.limit("20/minute")
def create_enroll_token(request: Request, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    token_str = secrets.token_urlsafe(16)
    expires = datetime.utcnow() + timedelta(hours=24)
    token = EnrollToken(token=token_str, created_by=current_user.username, expires_at=expires)
    db.add(token)
    db.commit()
    return {"token": token_str, "expires_at": expires}

@app.get("/api/app/version")
def get_app_version():
    return {
        "version": "2.1.0",
        "download_url": "https://bipdesk.duckdns.org/downloads/BipDesk_Admin.exe",
        "release_notes": "Atualização com novo layout, suporte a 1.000+ máquinas, auto-download de motor e concorrência multi-técnico.",
        "published_at": datetime.utcnow().isoformat()
    }

@app.get("/healthz")
def healthz():
    return {"status": "ok", "version": "2.1.0", "timestamp": datetime.utcnow().isoformat()}

# --- RUSTDESK CLIENT COMPATIBILITY ENDPOINTS (ELIMINA ERROS 404 DE AUDITORIA E TELEMETRIA) ---
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
