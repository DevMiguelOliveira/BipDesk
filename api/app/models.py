import datetime
from sqlalchemy import Column, Integer, String, Boolean, DateTime, Text
from .database import Base

class User(Base):
    """Conta do técnico administrador"""
    __tablename__ = "users"

    id = Column(Integer, primary_key=True, index=True)
    username = Column(String(50), unique=True, index=True, nullable=False)
    password_hash = Column(String(255), nullable=False)
    created_at = Column(DateTime, default=datetime.datetime.utcnow)

class Machine(Base):
    """Computadores clientes com BipDesk Host instalado"""
    __tablename__ = "machines"

    id = Column(Integer, primary_key=True, index=True)
    rustdesk_id = Column(String(32), unique=True, index=True, nullable=False)
    hostname = Column(String(100), nullable=False)
    alias = Column(String(100), nullable=True) # Nome amigável dado pelo técnico
    client_group = Column(String(100), default="Geral", index=True) # Empresa ou setor
    notes = Column(Text, nullable=True)
    is_favorite = Column(Boolean, default=False)
    
    # Telemetria & Heartbeat
    logged_in_user = Column(String(100), nullable=True)
    os_version = Column(String(100), nullable=True)
    ip_address = Column(String(45), nullable=True)
    mac_address = Column(String(45), nullable=True)
    last_seen_at = Column(DateTime, default=datetime.datetime.utcnow, index=True)
    
    # Credencial cifrada (para conexão sem intervenção do usuário)
    encrypted_password = Column(Text, nullable=False)
    created_at = Column(DateTime, default=datetime.datetime.utcnow)

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
    """Auditoria de sessões iniciadas pelo técnico"""
    __tablename__ = "connection_audits"

    id = Column(Integer, primary_key=True, index=True)
    technician_username = Column(String(50), nullable=False)
    machine_id = Column(Integer, nullable=False)
    rustdesk_id = Column(String(32), nullable=False)
    connected_at = Column(DateTime, default=datetime.datetime.utcnow)
