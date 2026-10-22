import os
from sqlalchemy import create_engine, event
from sqlalchemy.orm import declarative_base, sessionmaker

DATABASE_URL = os.getenv("DATABASE_URL", "sqlite:///./bipdesk.db")

# Normalizar URLs do PostgreSQL / Supabase
if DATABASE_URL.startswith("postgres://"):
    DATABASE_URL = DATABASE_URL.replace("postgres://", "postgresql://", 1)

engine_kwargs = {"pool_pre_ping": True}
if "sqlite" in DATABASE_URL:
    engine_kwargs["connect_args"] = {"check_same_thread": False}
else:
    # Configurações otimizadas para Supabase / PostgreSQL (Pooler ou Direto)
    engine_kwargs["pool_recycle"] = 300
    engine_kwargs["pool_size"] = int(os.getenv("DB_POOL_SIZE", "5"))
    engine_kwargs["max_overflow"] = int(os.getenv("DB_MAX_OVERFLOW", "10"))

engine = create_engine(DATABASE_URL, **engine_kwargs)

# Configurações de alta performance para SQLite WAL
if "sqlite" in DATABASE_URL:
    @event.listens_for(engine, "connect")
    def set_sqlite_pragma(dbapi_connection, connection_record):
        cursor = dbapi_connection.cursor()
        cursor.execute("PRAGMA journal_mode=WAL")
        cursor.execute("PRAGMA synchronous=NORMAL")
        cursor.execute("PRAGMA busy_timeout=5000")
        cursor.execute("PRAGMA cache_size=-64000")
        cursor.close()

SessionLocal = sessionmaker(autocommit=False, autoflush=False, bind=engine)
Base = declarative_base()

def auto_migrate(engine_instance):
    """Aplica migrações incrementais no SQLite de forma não destrutiva para colunas adicionadas"""
    if "sqlite" not in DATABASE_URL:
        return

    from sqlalchemy import text
    migrations = [
        # Tabela users
        "ALTER TABLE users ADD COLUMN full_name VARCHAR(100)",
        "ALTER TABLE users ADD COLUMN role VARCHAR(20) DEFAULT 'technician'",
        "ALTER TABLE users ADD COLUMN is_active BOOLEAN DEFAULT 1",
        "ALTER TABLE users ADD COLUMN totp_secret VARCHAR(64)",
        "ALTER TABLE users ADD COLUMN totp_enabled BOOLEAN DEFAULT 0",
        "ALTER TABLE users ADD COLUMN failed_login_attempts INTEGER DEFAULT 0",
        "ALTER TABLE users ADD COLUMN locked_until DATETIME",
        "ALTER TABLE users ADD COLUMN refresh_token VARCHAR(255)",
        # Tabela machines
        "ALTER TABLE machines ADD COLUMN machine_id VARCHAR(64)",
        "ALTER TABLE machines ADD COLUMN description VARCHAR(255)",
        "ALTER TABLE machines ADD COLUMN public_key VARCHAR(64)",
        "ALTER TABLE machines ADD COLUMN state VARCHAR(30) DEFAULT 'pending'",
        "ALTER TABLE machines ADD COLUMN bound_at DATETIME",
        "ALTER TABLE machines ADD COLUMN department VARCHAR(100) DEFAULT 'Geral'",
        "ALTER TABLE machines ADD COLUMN current_session_tech VARCHAR(50)",
        "ALTER TABLE machines ADD COLUMN session_started_at DATETIME",
        "ALTER TABLE machines ADD COLUMN cpu_usage INTEGER DEFAULT 0",
        "ALTER TABLE machines ADD COLUMN ram_usage INTEGER DEFAULT 0",
        "ALTER TABLE machines ADD COLUMN disk_free_gb INTEGER DEFAULT 0",
        "ALTER TABLE machines ADD COLUMN cpu_model VARCHAR(100)",
        "ALTER TABLE machines ADD COLUMN token_hash VARCHAR(64)",
        "ALTER TABLE machines ADD COLUMN token_created_at DATETIME",
        "ALTER TABLE machines ADD COLUMN token_revoked BOOLEAN DEFAULT 0",
        # Tabela connection_audits
        "ALTER TABLE connection_audits ADD COLUMN machine_name VARCHAR(100)",
        "ALTER TABLE connection_audits ADD COLUMN client_group VARCHAR(100)",
        "ALTER TABLE connection_audits ADD COLUMN duration_seconds INTEGER DEFAULT 0",
    ]
    with engine_instance.connect() as conn:
        for sql in migrations:
            try:
                conn.execute(text(sql))
                conn.commit()
            except Exception:
                pass

def get_db():
    db = SessionLocal()
    try:
        yield db
    finally:
        db.close()
