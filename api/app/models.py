import datetime
from sqlalchemy import Column, Integer, String, Boolean, DateTime, Text, ForeignKey
from sqlalchemy.orm import relationship
from .database import Base

def utcnow():
    return datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None)

class User(Base):
    """Conta do técnico administrador com suporte a Argon2id, TOTP e bloqueio temporário"""
    __tablename__ = "users"

    id = Column(Integer, primary_key=True, index=True)
    username = Column(String(50), unique=True, index=True, nullable=False)
    full_name = Column(String(100), nullable=True)
    role = Column(String(20), default="technician")  # "admin" ou "technician"
    is_active = Column(Boolean, default=True)
    password_hash = Column(String(255), nullable=False)  # Argon2id (compatível com bcrypt)
    
    # 2FA e Segurança
    totp_secret = Column(String(64), nullable=True)  # Segredo Base32 para RFC 6238
    totp_enabled = Column(Boolean, default=False)
    failed_login_attempts = Column(Integer, default=0)
    locked_until = Column(DateTime, nullable=True)
    refresh_token = Column(String(255), nullable=True)
    created_at = Column(DateTime, default=utcnow)

class Machine(Base):
    """Computadores clientes com BipDesk Host e vinculação Ed25519 (ADR-011)"""
    __tablename__ = "machines"

    id = Column(Integer, primary_key=True, index=True)
    machine_id = Column(String(64), unique=True, index=True, nullable=False)  # Identificador imutável (ex: bip-xxxx)
    rustdesk_id = Column(String(32), unique=True, index=True, nullable=True)  # ID do motor RustDesk
    hostname = Column(String(100), nullable=False, default="Pendente")
    description = Column(String(255), nullable=True)  # Descrição cadastrada pelo técnico
    alias = Column(String(100), nullable=True)  # Nome amigável dado pelo técnico
    client_group = Column(String(100), default="Geral", index=True)  # Empresa / Cliente
    department = Column(String(100), default="Geral", index=True)  # Setor / Filial
    notes = Column(Text, nullable=True)
    is_favorite = Column(Boolean, default=False)

    # Identidade Criptográfica Ed25519 (ADR-011)
    public_key = Column(String(64), unique=True, index=True, nullable=True)  # Chave pública Ed25519 (32 bytes hex)
    state = Column(String(30), default="pending", index=True)  # pending, online, offline, revoked, pending_re-enroll
    bound_at = Column(DateTime, nullable=True)  # Data de vinculação do enrollment
    
    # Telemetria & Presença em Tempo Real (ADR-012)
    logged_in_user = Column(String(100), nullable=True)
    os_version = Column(String(100), nullable=True)
    ip_address = Column(String(45), nullable=True)
    mac_address = Column(String(45), nullable=True)
    last_seen_at = Column(DateTime, default=utcnow, index=True)
    
    # Métricas de Hardware
    cpu_usage = Column(Integer, default=0)  # 0-100%
    ram_usage = Column(Integer, default=0)  # 0-100%
    disk_free_gb = Column(Integer, default=0)
    cpu_model = Column(String(100), nullable=True)
    
    # Controle de Concorrência e Sessão
    current_session_tech = Column(String(50), nullable=True)
    session_started_at = Column(DateTime, nullable=True)

    # Token legado para compatibilidade de testes
    token_hash = Column(String(64), nullable=True, index=True)

    # Credencial cifrada legada (opcional para conexão sem intervenção)
    encrypted_password = Column(Text, nullable=True)
    created_at = Column(DateTime, default=utcnow)

    # Relacionamentos
    commands = relationship("RemoteCommand", back_populates="machine", cascade="all, delete-orphan")
    history_notes = relationship("MachineNote", back_populates="machine", cascade="all, delete-orphan")
    tokens = relationship("EnrollmentToken", back_populates="machine", cascade="all, delete-orphan")

class EnrollmentToken(Base):
    """Token de uso único de 256 bits para enrollment automático (ADR-011)"""
    __tablename__ = "enrollment_tokens"

    id = Column(Integer, primary_key=True, index=True)
    token_hash = Column(String(64), unique=True, index=True, nullable=False)  # SHA-256 do token
    machine_id = Column(String(64), ForeignKey("machines.machine_id"), nullable=False, index=True)
    created_by = Column(String(50), default="admin")
    expires_at = Column(DateTime, nullable=False)  # Validade estrita de 24h
    consumed_at = Column(DateTime, nullable=True)  # Preenchido ao vincular
    created_at = Column(DateTime, default=utcnow)

    machine = relationship("Machine", back_populates="tokens")

# Alias de compatibilidade com testes anteriores
EnrollToken = EnrollmentToken

class RemoteCommand(Base):
    """Fila de comandos silenciosos enviados pelo técnico para a máquina (Enum Fechado)"""
    __tablename__ = "remote_commands"

    id = Column(Integer, primary_key=True, index=True)
    machine_id = Column(Integer, ForeignKey("machines.id"), nullable=False, index=True)
    command_type = Column(String(30), nullable=False)  # cad, restart_spooler, flush_dns, lock, reboot
    payload = Column(Text, nullable=True)
    status = Column(String(20), default="pending", index=True)  # pending, executed, failed
    created_by = Column(String(50), nullable=False)
    created_at = Column(DateTime, default=utcnow)
    executed_at = Column(DateTime, nullable=True)

    machine = relationship("Machine", back_populates="commands")

class MachineNote(Base):
    """Linha do tempo de ocorrências e notas técnicas compartilhadas"""
    __tablename__ = "machine_notes"

    id = Column(Integer, primary_key=True, index=True)
    machine_id = Column(Integer, ForeignKey("machines.id"), nullable=False, index=True)
    author_username = Column(String(50), nullable=False)
    content = Column(Text, nullable=False)
    created_at = Column(DateTime, default=utcnow)

    machine = relationship("Machine", back_populates="history_notes")

class ConnectionAudit(Base):
    """Auditoria detalhada de sessões para compliance e faturamento"""
    __tablename__ = "connection_audits"

    id = Column(Integer, primary_key=True, index=True)
    technician_username = Column(String(50), nullable=False, index=True)
    machine_id = Column(String(64), nullable=False, index=True)
    machine_name = Column(String(100), nullable=True)
    client_group = Column(String(100), nullable=True)
    rustdesk_id = Column(String(32), nullable=True)
    connected_at = Column(DateTime, default=utcnow)
    duration_seconds = Column(Integer, default=0)

class SecurityEvent(Base):
    """Trilha de auditoria e eventos de segurança (STRIDE / Compliance)"""
    __tablename__ = "security_events"

    id = Column(Integer, primary_key=True, index=True)
    timestamp = Column(DateTime, default=utcnow, index=True)
    event_type = Column(String(50), nullable=False, index=True)  # enrollment_failed, signature_invalid, replay_detected, etc.
    severity = Column(String(20), default="WARNING", index=True)  # INFO, WARNING, CRITICAL
    machine_id = Column(String(64), nullable=True, index=True)
    ip_address = Column(String(45), nullable=True)
    details = Column(Text, nullable=True)

class SessionTicket(Base):
    """Ticket de sessão temporária de uso único assinado pelo servidor (Seção 6)"""
    __tablename__ = "session_tickets"

    id = Column(Integer, primary_key=True, index=True)
    session_id = Column(String(64), unique=True, index=True, nullable=False)
    machine_id = Column(String(64), nullable=False, index=True)
    technician_username = Column(String(50), nullable=False, index=True)
    status = Column(String(20), default="offered", index=True)  # offered, active, ended, revoked
    ticket_signature = Column(String(128), nullable=False)  # Assinatura Ed25519 do servidor
    created_at = Column(DateTime, default=utcnow)
    expires_at = Column(DateTime, nullable=False)  # Expira em 60s se não aceita
    ended_at = Column(DateTime, nullable=True)
