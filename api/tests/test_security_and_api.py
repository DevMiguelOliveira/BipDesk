import os
import pytest
import hmac
import hashlib
from pathlib import Path
from cryptography.fernet import Fernet
from pydantic import ValidationError

# Define ambiente antes de carregar os módulos da aplicação
os.environ["ENV"] = "development"
os.environ["SECRET_KEY"] = "test_secret_key_12345678901234567890"

from app.database import Base, engine, auto_migrate, SessionLocal
from app.models import User, Machine, EnrollToken
from app.security import (
    get_secret_key,
    encrypt_password,
    decrypt_password,
    rotate_encrypted_password,
    create_access_token,
    decode_access_token,
    verify_password,
    get_password_hash
)
from app.main import (
    app,
    HeartbeatRequest,
    EnrollRequest,
    RemoteCommandRequest,
    sanitize_rustdesk_id,
    startup_populate
)
from fastapi.testclient import TestClient

@pytest.fixture(autouse=True)
def setup_test_db():
    Base.metadata.create_all(bind=engine)
    auto_migrate(engine)
    startup_populate()
    yield
    # Limpeza básica de dados após cada teste
    db = SessionLocal()
    try:
        db.query(Machine).delete()
        db.commit()
    finally:
        db.close()

@pytest.fixture
def client():
    return TestClient(app)

def test_secret_key_production_fail_fast(monkeypatch, tmp_path):
    monkeypatch.setenv("ENV", "production")
    monkeypatch.delenv("SECRET_KEY", raising=False)
    monkeypatch.setattr("app.security.SECRET_KEY_FILE", tmp_path / "non_existent.txt")
    with pytest.raises(RuntimeError, match="SECRET_KEY"):
        get_secret_key()

def test_fernet_key_rotation(monkeypatch):
    k1 = Fernet.generate_key().decode()
    k2 = Fernet.generate_key().decode()

    # Inicia com k1
    monkeypatch.setenv("FERNET_KEYS", k1)
    original_pass = "HostSuperSecretP@ssw0rd!2026"
    encrypted_k1 = encrypt_password(original_pass)
    assert decrypt_password(encrypted_k1) == original_pass

    # Rotaciona para k2 como chave primária, mantendo k1 para decifração
    monkeypatch.setenv("FERNET_KEYS", f"{k2},{k1}")
    # Decifra com chave antiga
    assert decrypt_password(encrypted_k1) == original_pass

    # Re-encripta (rotaciona) para a chave primária k2
    rotated = rotate_encrypted_password(encrypted_k1)
    assert decrypt_password(rotated) == original_pass

    # Se removermos k1, o token rotacionado ainda funciona com k2
    monkeypatch.setenv("FERNET_KEYS", k2)
    assert decrypt_password(rotated) == original_pass

def test_jwt_strict_hs256():
    payload = {"sub": "technician_1", "role": "technician"}
    token = create_access_token(payload)
    decoded = decode_access_token(token)
    assert decoded["sub"] == "technician_1"
    assert decoded["role"] == "technician"

    # Token adulterado deve falhar
    assert decode_access_token(token + "tampered") is None

def test_rustdesk_id_sanitization_and_validation():
    # Sanitização
    assert sanitize_rustdesk_id("123456789\r\n") == "123456789"
    assert sanitize_rustdesk_id("[WARN] flutter log\r\n987654321") == "987654321"

    # Validação Pydantic
    valid_hb = HeartbeatRequest(rustdesk_id="123456789")
    assert valid_hb.rustdesk_id == "123456789"

    # Menos de 6 caracteres deve falhar
    with pytest.raises(ValidationError):
        HeartbeatRequest(rustdesk_id="12345")

    # Caracteres ilegais devem falhar
    with pytest.raises(ValidationError):
        HeartbeatRequest(rustdesk_id="bad;id;injection")

    # Mais de 32 caracteres deve falhar
    with pytest.raises(ValidationError):
        HeartbeatRequest(rustdesk_id="a" * 33)

def test_remote_command_closed_enum():
    # Comandos permitidos no enum
    for cmd in ["cad", "restart_spooler", "flush_dns", "lock", "reboot"]:
        req = RemoteCommandRequest(command_type=cmd)
        assert req.command_type == cmd

    # Comandos proibidos (fora do enum) devem falhar na validação Pydantic
    for forbidden in ["cmd", "powershell", "bash", "whoami", "custom"]:
        with pytest.raises(ValidationError):
            RemoteCommandRequest(command_type=forbidden)

def test_heartbeat_without_token(client):
    # Heartbeat sem header X-Machine-Token deve retornar 401 ou 404
    resp = client.post("/api/agent/heartbeat", json={"rustdesk_id": "999888777"})
    assert resp.status_code in [401, 404]

def test_agent_enroll_and_heartbeat_flow(client):
    # 1. Enroll
    enroll_payload = {
        "token": "direct",
        "rustdesk_id": "777888999",
        "hostname": "Test-Client-Host",
        "password": "TemporaryRandomPassw0rd!"
    }
    enroll_resp = client.post("/api/agent/enroll", json=enroll_payload)
    assert enroll_resp.status_code == 200
    enroll_data = enroll_resp.json()
    assert enroll_data["status"] == "enrolled"
    machine_token = enroll_data["machine_token"]
    assert len(machine_token) > 20

    # 2. Heartbeat sem token para máquina cadastrada -> 401
    hb_no_token = client.post("/api/agent/heartbeat", json={"rustdesk_id": "777888999"})
    assert hb_no_token.status_code == 401

    # 3. Heartbeat com token inválido -> 403
    hb_bad_token = client.post(
        "/api/agent/heartbeat",
        json={"rustdesk_id": "777888999"},
        headers={"X-Machine-Token": "invalid_wrong_token_12345"}
    )
    assert hb_bad_token.status_code == 403

    # 4. Heartbeat com token válido -> 200
    hb_good = client.post(
        "/api/agent/heartbeat",
        json={"rustdesk_id": "777888999", "cpu_usage": 15, "ram_usage": 45, "disk_free_gb": 80},
        headers={"X-Machine-Token": machine_token}
    )
    assert hb_good.status_code == 200
    assert hb_good.json()["status"] == "alive"

def test_login_rate_limiting(client):
    # Limite é 15 por minuto para /api/login
    last_status = None
    for _ in range(25):
        resp = client.post("/api/login", json={"username": "admin", "password": "wrong_password"})
        last_status = resp.status_code
        if last_status == 429:
            break
    assert last_status == 429
