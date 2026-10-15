import os
import secrets
from datetime import datetime, timedelta
from typing import List, Optional
from fastapi import FastAPI, Depends, HTTPException, status, Header, Request, Response
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel
from sqlalchemy.orm import Session

from .database import engine, Base, get_db
from .models import User, Machine, EnrollToken, ConnectionAudit
from .security import (
    verify_password, get_password_hash,
    encrypt_password, decrypt_password,
    create_access_token, decode_access_token
)

# Cria tabelas automaticamente
Base.metadata.create_all(bind=engine)

app = FastAPI(title="BipDesk API", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Cria usuário admin padrão se banco estiver vazio
@app.on_event("startup")
def startup_populate():
    db = next(get_db())
    admin = db.query(User).filter(User.username == "admin").first()
    if not admin:
        default_pwd = os.getenv("ADMIN_DEFAULT_PASSWORD", "BipDesk@2026")
        admin = User(
            username="admin",
            password_hash=get_password_hash(default_pwd)
        )
        db.add(admin)
        db.commit()
        print(f"[BipDesk] Usuário admin criado com senha inicial: {default_pwd}")
    db.close()

# ----------------- Schemas -----------------
class LoginRequest(BaseModel):
    username: str
    password: str

class LoginResponse(BaseModel):
    access_token: str
    token_type: str = "bearer"

class MachineResponse(BaseModel):
    id: int
    rustdesk_id: str
    hostname: str
    alias: Optional[str]
    client_group: str
    notes: Optional[str]
    is_favorite: bool
    is_online: bool
    logged_in_user: Optional[str]
    os_version: Optional[str]
    ip_address: Optional[str]
    last_seen_at: datetime

class MachineUpdateRequest(BaseModel):
    alias: Optional[str] = None
    notes: Optional[str] = None
    is_favorite: Optional[bool] = None
    client_group: Optional[str] = None

class EnrollRequest(BaseModel):
    token: str
    rustdesk_id: str
    hostname: str
    username: Optional[str] = None
    os_version: Optional[str] = None
    password: str

class HeartbeatRequest(BaseModel):
    rustdesk_id: str
    logged_in_user: Optional[str] = None
    ip_address: Optional[str] = None

class ConnectResponse(BaseModel):
    rustdesk_id: str
    password: str
    expires_in_seconds: int = 60

# ----------------- Dependency -----------------
def get_current_user(authorization: Optional[str] = Header(None), db: Session = Depends(get_db)):
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="Token ausente ou inválido")
    token = authorization.split(" ")[1]
    payload = decode_access_token(token)
    if not payload:
        raise HTTPException(status_code=401, detail="Token expirado ou inválido")
    user = db.query(User).filter(User.username == payload.get("sub")).first()
    if not user:
        raise HTTPException(status_code=401, detail="Usuário não encontrado")
    return user

# ----------------- Rotas do Técnico -----------------
@app.post("/api/login", response_model=LoginResponse)
def login(req: LoginRequest, db: Session = Depends(get_db)):
    user = db.query(User).filter(User.username == req.username).first()
    if not user or not verify_password(req.password, user.password_hash):
        raise HTTPException(status_code=400, detail="Usuário ou senha incorretos")
    token = create_access_token({"sub": user.username})
    return {"access_token": token, "token_type": "bearer"}

@app.get("/api/machines", response_model=List[MachineResponse])
def list_machines(current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    now = datetime.utcnow()
    machines = db.query(Machine).order_by(Machine.is_favorite.desc(), Machine.last_seen_at.desc()).all()
    result = []
    for m in machines:
        diff_seconds = (now - m.last_seen_at).total_seconds()
        is_online = diff_seconds < 90
        result.append(MachineResponse(
            id=m.id,
            rustdesk_id=m.rustdesk_id,
            hostname=m.hostname,
            alias=m.alias,
            client_group=m.client_group,
            notes=m.notes,
            is_favorite=m.is_favorite,
            is_online=is_online,
            logged_in_user=m.logged_in_user,
            os_version=m.os_version,
            ip_address=m.ip_address,
            last_seen_at=m.last_seen_at
        ))
    return result

@app.patch("/api/machines/{machine_id}")
def update_machine(machine_id: int, req: MachineUpdateRequest, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.id == machine_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")
    if req.alias is not None:
        machine.alias = req.alias
    if req.notes is not None:
        machine.notes = req.notes
    if req.is_favorite is not None:
        machine.is_favorite = req.is_favorite
    if req.client_group is not None:
        machine.client_group = req.client_group
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

@app.post("/api/enroll-tokens")
def create_enroll_token(current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    token_str = secrets.token_urlsafe(16)
    expires = datetime.utcnow() + timedelta(hours=24)
    token = EnrollToken(token=token_str, created_by=current_user.username, expires_at=expires)
    db.add(token)
    db.commit()
    return {"token": token_str, "expires_at": expires}

@app.post("/api/machines/{machine_id}/connect", response_model=ConnectResponse)
def get_connect_ticket(machine_id: int, current_user: User = Depends(get_current_user), db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.id == machine_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não encontrada")
    
    # Registra auditoria da sessão
    audit = ConnectionAudit(
        technician_username=current_user.username,
        machine_id=machine.id,
        rustdesk_id=machine.rustdesk_id
    )
    db.add(audit)
    db.commit()

    decrypted_pwd = decrypt_password(machine.encrypted_password)
    return ConnectResponse(
        rustdesk_id=machine.rustdesk_id,
        password=decrypted_pwd,
        expires_in_seconds=60
    )

# ----------------- Rotas do Agent / Host -----------------
@app.post("/api/agent/enroll")
def agent_enroll(req: EnrollRequest, request: Request, db: Session = Depends(get_db)):
    token_entry = db.query(EnrollToken).filter(
        EnrollToken.token == req.token,
        EnrollToken.is_used == False,
        EnrollToken.expires_at > datetime.utcnow()
    ).first()
    if not token_entry:
        raise HTTPException(status_code=400, detail="Token de instalação inválido ou expirado")
    
    client_ip = request.client.host if request.client else None

    # Verifica se a máquina já existe
    machine = db.query(Machine).filter(Machine.rustdesk_id == req.rustdesk_id).first()
    if not machine:
        machine = Machine(
            rustdesk_id=req.rustdesk_id,
            hostname=req.hostname,
            alias=req.hostname,
            logged_in_user=req.username,
            os_version=req.os_version,
            ip_address=client_ip,
            encrypted_password=encrypt_password(req.password),
            last_seen_at=datetime.utcnow()
        )
        db.add(machine)
    else:
        machine.hostname = req.hostname
        machine.logged_in_user = req.username
        machine.os_version = req.os_version
        machine.ip_address = client_ip
        machine.encrypted_password = encrypt_password(req.password)
        machine.last_seen_at = datetime.utcnow()

    token_entry.is_used = True
    db.commit()
    return {"status": "enrolled", "machine_id": machine.id}

@app.post("/api/agent/heartbeat")
def agent_heartbeat(req: HeartbeatRequest, request: Request, db: Session = Depends(get_db)):
    machine = db.query(Machine).filter(Machine.rustdesk_id == req.rustdesk_id).first()
    if not machine:
        raise HTTPException(status_code=404, detail="Máquina não registrada")
    
    machine.last_seen_at = datetime.utcnow()
    if req.logged_in_user:
        machine.logged_in_user = req.logged_in_user
    if req.ip_address:
        machine.ip_address = req.ip_address
    elif request.client:
        machine.ip_address = request.client.host

    db.commit()
    return {"status": "alive"}

# ----------------- Script One-Liner PowerShell -----------------
@app.get("/install/{token}")
def serve_install_script(token: str, request: Request):
    base_url = f"{request.url.scheme}://{request.url.netloc}"
    ps_script_path = os.path.join(os.path.dirname(__file__), "../../deploy/install-host.ps1")
    if os.path.exists(ps_script_path):
        with open(ps_script_path, "r", encoding="utf-8") as f:
            content = f.read()
    else:
        content = "# BipDesk Install Script\n"
    
    # Injeta a URL e o token dinamicamente
    content = content.replace('param(', f'# Injetado automaticamente\n$ServerUrl = "{base_url}"\n$EnrollToken = "{token}"\nparam(')
    return Response(content=content, media_type="text/plain; charset=utf-8")

@app.get("/healthz")
def healthz():
    return {"status": "ok", "timestamp": datetime.utcnow().isoformat()}
