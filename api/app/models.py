import datetime
from sqlalchemy import Column, Integer, String, Boolean, DateTime, Text, ForeignKey
from sqlalchemy.orm import relationship
from .database import Base

class User(Base):
    """Conta do técnico administrador"""
    __tablename__ = "users"

    id = Column(Integer, primary_key=True, index=True)
    username = Column(String(50), unique=True, index=True, nullable=False)
    full_name = Column(String(100), nullable=True)
    role = Column(String(20), default="technician") # "admin" ou "technician"
    is_active = Column(Boolean, default=True)
    password_hash = Column(String(255), nullable=False)
    created_at = Column(DateTime, default=datetime.datetime.utcnow)

class Machine(Base):
    """Computadores clientes com BipDesk Host instalado"""
    __tablename__ = "machines"

    id = Column(Integer, primary_key=True, index=True)
    rustdesk_id = Column(String(32), unique=True, index=True, nullable=False)
    hostname = Column(String(100), nullable=False)
    alias = Column(String(100), nullable=True) # Nome amigável dado pelo técnico
    client_group = Column(String(100), default="Geral", index=True) # Empresa / Cliente
    department = Column(String(100), default="Geral", index=True) # Setor / Filial
    notes = Column(Text, nullable=True)
    is_favorite = Column(Boolean, default=False)
    
    # Telemetria & Heartbeat
    logged_in_user = Column(String(100), nullable=True)
    os_version = Column(String(100), nullable=True)
    ip_address = Column(String(45), nullable=True)
    mac_address = Column(String(45), nullable=True)
    last_seen_at = Column(DateTime, default=datetime.datetime.utcnow, index=True)
    
    # Métricas de Hardware em Tempo Real
    cpu_usage = Column(Integer, default=0) # 0-100%
    ram_usage = Column(Integer, default=0) # 0-100%
    disk_free_gb = Column(Integer, default=0)
    cpu_model = Column(String(100), nullable=True)
    
    # Controle de Concorrência Multi-Técnico
    current_session_tech = Column(String(50), nullable=True) # Técnico conectado no momento
    session_started_at = Column(DateTime, nullable=True)

    # Autenticação Criptográfica do Agente (ADR-001)
    token_hash = Column(String(64), nullable=True, index=True)
    token_created_at = Column(DateTime, nullable=True)
    token_revoked = Column(Boolean, default=False)

    # Credencial cifrada (para conexão sem intervenção do usuário)
    encrypted_password = Column(Text, nullable=False)
    created_at = Column(DateTime, default=datetime.datetime.utcnow)

    # Relacionamentos
    commands = relationship("RemoteCommand", back_populates="machine", cascade="all, delete-orphan")
    history_notes = relationship("MachineNote", back_populates="machine", cascade="all, delete-orphan")

class RemoteCommand(Base):
    """Fila de comandos silenciosos enviados pelo técnico para a máquina"""
    __tablename__ = "remote_commands"

    id = Column(Integer, primary_key=True, index=True)
    machine_id = Column(Integer, ForeignKey("machines.id"), nullable=False, index=True)
    command_type = Column(String(30), nullable=False) # "reboot", "message", "restart_spooler"
    payload = Column(Text, nullable=True) # Mensagem ou parâmetro
    status = Column(String(20), default="pending", index=True) # "pending", "executed", "failed"
    created_by = Column(String(50), nullable=False)
    created_at = Column(DateTime, default=datetime.datetime.utcnow)
    executed_at = Column(DateTime, nullable=True)

    machine = relationship("Machine", back_populates="commands")

class MachineNote(Base):
    """Linha do tempo de ocorrências e notas técnicas compartilhadas"""
    __tablename__ = "machine_notes"

    id = Column(Integer, primary_key=True, index=True)
    machine_id = Column(Integer, ForeignKey("machines.id"), nullable=False, index=True)
    author_username = Column(String(50), nullable=False)
    content = Column(Text, nullable=False)
    created_at = Column(DateTime, default=datetime.datetime.utcnow)

    machine = relationship("Machine", back_populates="history_notes")

class EnrollToken(Base):
    """Token temporário para adicionar novas máquinas"""
    __tablename__ = "enroll_tokens"

    id = Column(Integer, primary_key=True, index=True)
    token = Column(String(64), unique=True, index=True, nullable=False)
    created_by = Column(String(50), default="admin")
    expires_at = Column(DateTime, nullable=False)
    is_used = Column(Boolean, default=False)
    created_at = Column(DateTime, default=datetime.datetime.utcnow)

class ConnectionAudit(Base):
    """Auditoria detalhada de sessões para compliance e faturamento"""
    __tablename__ = "connection_audits"

    id = Column(Integer, primary_key=True, index=True)
    technician_username = Column(String(50), nullable=False, index=True)
    machine_id = Column(Integer, nullable=False, index=True)
    machine_name = Column(String(100), nullable=True)
    client_group = Column(String(100), nullable=True)
    rustdesk_id = Column(String(32), nullable=False)
    connected_at = Column(DateTime, default=datetime.datetime.utcnow)
    duration_seconds = Column(Integer, default=0)
