import os
from sqlalchemy import create_engine, event
from sqlalchemy.orm import declarative_base, sessionmaker

DATABASE_URL = os.getenv("DATABASE_URL", "sqlite:///./bipdesk.db")

engine = create_engine(
    DATABASE_URL,
    connect_args={"check_same_thread": False} if "sqlite" in DATABASE_URL else {},
    pool_pre_ping=True
)

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
    from sqlalchemy import text
    migrations = [
        # Tabela users
        "ALTER TABLE users ADD COLUMN full_name VARCHAR(100)",
        "ALTER TABLE users ADD COLUMN role VARCHAR(20) DEFAULT 'technician'",
        "ALTER TABLE users ADD COLUMN is_active BOOLEAN DEFAULT 1",
        # Tabela machines
        "ALTER TABLE machines ADD COLUMN department VARCHAR(100) DEFAULT 'Geral'",
        "ALTER TABLE machines ADD COLUMN current_session_tech VARCHAR(50)",
        "ALTER TABLE machines ADD COLUMN session_started_at DATETIME",
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
