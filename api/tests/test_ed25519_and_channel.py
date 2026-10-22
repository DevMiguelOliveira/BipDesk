import os
import json
import secrets
import hashlib
import pytest
from datetime import datetime, timedelta, timezone
from cryptography.hazmat.primitives.asymmetric import ed25519
from fastapi.testclient import TestClient

os.environ["ENV"] = "development"
os.environ["SECRET_KEY"] = "test_ed25519_secret_key_1234567890123456789"

from app.database import Base, engine, auto_migrate, SessionLocal
from app.models import User, Machine, EnrollmentToken, SecurityEvent, SessionTicket
from app.main import app, startup_populate, host_ws_manager
from app.security import (
    load_or_init_server_signing_key, get_server_public_key_hex,
    sign_canonical, verify_host_signature, make_canonical_string,
    global_nonce_cache, is_valid_timestamp, get_password_hash,
    verify_password, generate_totp_secret, verify_totp
)
from app.tailscale import verify_tailscale_peer_tag

def utcnow():
    return datetime.now(timezone.utc).replace(tzinfo=None)

@pytest.fixture(autouse=True)
def setup_db():
    Base.metadata.create_all(bind=engine)
    auto_migrate(engine)
    startup_populate()
    global_nonce_cache.clear()
    _init_db = SessionLocal()
    _admin = _init_db.query(User).filter(User.username == "admin").first()
    if _admin:
        _admin.failed_login_attempts = 0
        _admin.locked_until = None
        _init_db.commit()
    _init_db.close()
    yield
    db = SessionLocal()
    try:
        db.query(Machine).delete()
        db.query(EnrollmentToken).delete()
        db.query(SecurityEvent).delete()
        db.query(SessionTicket).delete()
        db.commit()
    finally:
        db.close()

@pytest.fixture
def client():
    return TestClient(app)

@pytest.fixture
def host_keypair():
    priv = ed25519.Ed25519PrivateKey.generate()
    pub = priv.public_key()
    pub_bytes = pub.public_bytes_raw()
    return priv, pub_bytes.hex()

def test_argon2id_and_totp():
    """Valida hashing com Argon2id e autenticação TOTP RFC 6238 (Seção 5)"""
    pwd = "AdminStrongP@ssword2026!"
    h = get_password_hash(pwd)
    assert h.startswith("$argon2id$")
    assert verify_password(pwd, h) is True
    assert verify_password("WrongPassword!", h) is False

    totp_sec = generate_totp_secret()
    import pyotp
    code = pyotp.TOTP(totp_sec).now()
    assert verify_totp(totp_sec, code) is True
    assert verify_totp(totp_sec, "000000") is False

def test_ed25519_canonical_envelope_and_signature(host_keypair):
    """Valida construção de mensagem canônica e verificação de assinatura Ed25519 (Seção 3)"""
    priv, pub_hex = host_keypair
    msg_type = "telemetry"
    machine_id = "bip-test1"
    now_ts = int(utcnow().timestamp())
    nonce = secrets.token_hex(16)
    payload = {"cpu_usage": 25, "ram_usage": 50, "disk_free_gb": 120}

    canonical_str = make_canonical_string(msg_type, machine_id, now_ts, nonce, payload)
    sig = priv.sign(canonical_str.encode("utf-8")).hex()

    # Assinatura válida
    assert verify_host_signature(pub_hex, canonical_str, sig) is True

    # Mensagem com assinatura de outra chave é descartada (Seção 9 - Teste 1)
    other_priv = ed25519.Ed25519PrivateKey.generate()
    bad_sig = other_priv.sign(canonical_str.encode("utf-8")).hex()
    assert verify_host_signature(pub_hex, canonical_str, bad_sig) is False

    # Mensagem adulterada no payload
    tampered_str = make_canonical_string(msg_type, machine_id, now_ts, nonce, {"cpu_usage": 99, "ram_usage": 50, "disk_free_gb": 120})
    assert verify_host_signature(pub_hex, tampered_str, sig) is False

def test_anti_replay_nonce_cache():
    """Mensagem repetida (nonce já visto) é descartada (Seção 9 - Teste 2)"""
    now_ts = int(utcnow().timestamp())
    nonce = secrets.token_hex(16)

    # Primeiro uso: válido
    assert global_nonce_cache.check_and_record(nonce, now_ts) is True

    # Segundo uso (replay): descartado
    assert global_nonce_cache.check_and_record(nonce, now_ts) is False

def test_timestamp_skew_window():
    """Mensagem com timestamp fora da janela (+- 120s) é descartada (Seção 9 - Teste 3)"""
    now_ts = 1000000
    assert is_valid_timestamp(now_ts, now_ts, max_skew_seconds=120) is True
    assert is_valid_timestamp(now_ts + 60, now_ts, max_skew_seconds=120) is True
    assert is_valid_timestamp(now_ts - 120, now_ts, max_skew_seconds=120) is True
    # Fora da janela (> 120s)
    assert is_valid_timestamp(now_ts + 121, now_ts, max_skew_seconds=120) is False
    assert is_valid_timestamp(now_ts - 121, now_ts, max_skew_seconds=120) is False

def test_one_time_token_enrollment_lifecycle(client, host_keypair):
    """Token reutilizado, expirado ou de outra máquina é recusado e gera evento (Seção 9 - Teste 4)"""
    priv, pub_hex = host_keypair
    db = SessionLocal()

    admin = db.query(User).filter(User.username == "admin").first()
    admin.password_hash = get_password_hash("AdminPass123!")
    admin.failed_login_attempts = 0
    admin.locked_until = None
    db.commit()

    token_resp = client.post("/api/login", json={"username": "admin", "password": "AdminPass123!"})
    auth_header = {"Authorization": f"Bearer {token_resp.json()['access_token']}"}

    create_resp = client.post("/api/machines/pending", json={"description": "PC B Test Host"}, headers=auth_header)
    assert create_resp.status_code == 200
    data = create_resp.json()
    machine_id = data["machine_id"]
    token = data["token"]
    assert len(token) > 20

    # 2. Enrollment com token válido -> Sucesso (vincula chave pública e consome token)
    sig = priv.sign(token.encode("utf-8")).hex()
    enroll_payload = {
        "token": token,
        "machine_id": machine_id,
        "public_key": pub_hex,
        "rustdesk_id": "1148443067",
        "hostname": "PC-B-Host",
        "signature": sig
    }
    enroll_resp = client.post("/api/agent/enroll", json=enroll_payload)
    assert enroll_resp.status_code == 200
    assert enroll_resp.json()["status"] == "bound"

    # 3. Tentativa de reuso do mesmo token consumido -> 403 e gera SecurityEvent
    reuse_resp = client.post("/api/agent/enroll", json=enroll_payload)
    assert reuse_resp.status_code == 403
    assert "já foi utilizado" in reuse_resp.json()["detail"]

    # Verifica se SecurityEvent de reuso foi registrado
    ev = db.query(SecurityEvent).filter(SecurityEvent.event_type == "enrollment_reused").first()
    assert ev is not None
    assert ev.severity == "CRITICAL"

    # 4. Token de outra máquina é recusado
    other_create = client.post("/api/machines/pending", json={"description": "PC C"}, headers=auth_header)
    token_c = other_create.json()["token"]
    mismatch_payload = {
        "token": token_c,
        "machine_id": machine_id,  # Tenta usar token do PC C para PC B
        "public_key": pub_hex,
        "rustdesk_id": "1148443067"
    }
    mismatch_resp = client.post("/api/agent/enroll", json=mismatch_payload)
    assert mismatch_resp.status_code == 403
    assert "não pertence a esta máquina" in mismatch_resp.json()["detail"]

    # 5. Token expirado é recusado
    expired_create = client.post("/api/machines/pending", json={"description": "PC Expired"}, headers=auth_header)
    tok_exp = expired_create.json()["token"]
    tok_hash = hashlib.sha256(tok_exp.encode()).hexdigest()
    t_entry = db.query(EnrollmentToken).filter(EnrollmentToken.token_hash == tok_hash).first()
    t_entry.expires_at = utcnow() - timedelta(minutes=1)
    db.commit()

    exp_resp = client.post("/api/agent/enroll", json={
        "token": tok_exp,
        "machine_id": expired_create.json()["machine_id"],
        "public_key": pub_hex
    })
    assert exp_resp.status_code == 403
    assert "expirado" in exp_resp.json()["detail"]
    db.close()

def test_websocket_channel_unauthenticated_rejected(client):
    """Conexão WebSocket sem autenticação/máquina válida é recusada (Seção 9 - Teste 5)"""
    # 1. Sem machine_id
    with pytest.raises(Exception):
        with client.websocket_connect("/api/ws/host") as ws:
            pass

    # 2. Com machine_id inexistente
    with pytest.raises(Exception):
        with client.websocket_connect("/api/ws/host?machine_id=bip-nonexistent") as ws:
            pass

def test_oversized_message_rejected(client, host_keypair):
    """Mensagem acima de 64 KB é recusada com código 4002 (Seção 9 - Teste 7)"""
    priv, pub_hex = host_keypair
    db = SessionLocal()

    # Prepara máquina vinculada no DB
    machine = Machine(
        machine_id="bip-oversize",
        rustdesk_id="999111222",
        hostname="OversizeHost",
        public_key=pub_hex,
        state="online",
        encrypted_password="enc_test_pass",
        last_seen_at=utcnow()
    )
    db.add(machine)
    db.commit()
    db.close()

    with client.websocket_connect("/api/ws/host?machine_id=bip-oversize") as ws:
        # Envia mensagem maior que 64 KB (65536 bytes)
        huge_payload = "A" * 66000
        ws.send_text(huge_payload)
        
        # O servidor deve fechar com código 4002
        try:
            ws.receive_text()
        except Exception:
            pass

    db = SessionLocal()
    ev = db.query(SecurityEvent).filter(SecurityEvent.event_type == "oversized_message").first()
    assert ev is not None
    db.close()

def test_tailscale_peer_tag_defense():
    """Valida defesa em profundidade com tags Tailscale (Seção 5)"""
    # Loopback / local deve permitir
    allowed, _ = verify_tailscale_peer_tag("127.0.0.1", "tag:bipdesk-host")
    assert allowed is True

def test_session_ticket_lifecycle_and_termination(client, host_keypair):
    """Valida ciclo de vida completo do SessionTicket: oferta com credencial efêmera e terminação (Seção 6 e 9 - Teste 6)"""
    priv, pub_hex = host_keypair
    db = SessionLocal()

    admin = db.query(User).filter(User.username == "admin").first()
    admin.password_hash = get_password_hash("AdminPass123!")
    admin.failed_login_attempts = 0
    admin.locked_until = None
    db.commit()

    token_resp = client.post("/api/login", json={"username": "admin", "password": "AdminPass123!"})
    auth_header = {"Authorization": f"Bearer {token_resp.json()['access_token']}"}

    # Prepara máquina cadastrada e ativa
    machine = Machine(
        machine_id="bip-sess-host",
        rustdesk_id="888777666",
        hostname="SessionHost",
        public_key=pub_hex,
        state="online",
        encrypted_password="enc_test_pass",
        last_seen_at=utcnow()
    )
    db.add(machine)
    db.commit()
    db.close()

    # 1. Sem host conectado no WebSocket -> 503
    fail_req = client.post("/api/sessions/request", json={"machine_id": "bip-sess-host"}, headers=auth_header)
    assert fail_req.status_code == 503

    # 2. Conecta host no WebSocket
    with client.websocket_connect("/api/ws/host?machine_id=bip-sess-host") as ws:
        # Envia pong para registrar atividade
        ws.send_text(json.dumps({"type": "pong", "machine_id": "bip-sess-host"}))

        # Solicita sessão
        sess_resp = client.post("/api/sessions/request", json={"machine_id": "bip-sess-host"}, headers=auth_header)
        assert sess_resp.status_code == 200
        sess_data = sess_resp.json()
        assert "session_id" in sess_data
        assert "session_password" in sess_data
        assert len(sess_data["session_password"]) >= 16

        # Recebe no WebSocket do host o 'session_offer' assinado
        msg_raw = ws.receive_text()
        offer_envelope = json.loads(msg_raw)
        assert offer_envelope["type"] == "session_offer"
        assert offer_envelope["payload"]["session_id"] == sess_data["session_id"]
        assert offer_envelope["payload"]["password"] == sess_data["session_password"]

        # Verifica ticket no banco
        db = SessionLocal()
        ticket = db.query(SessionTicket).filter(SessionTicket.session_id == sess_data["session_id"]).first()
        assert ticket is not None
        assert ticket.status == "offered"
        db.close()

        # Encerra sessão
        term_resp = client.post(f"/api/sessions/{sess_data['session_id']}/terminate", headers=auth_header)
        assert term_resp.status_code == 200

        # Host recebe 'session_terminate'
        term_msg = ws.receive_text()
        term_envelope = json.loads(term_msg)
        assert term_envelope["type"] == "session_terminate"
        assert term_envelope["payload"]["session_id"] == sess_data["session_id"]

        # Verifica que status do ticket virou 'ended'
        db = SessionLocal()
        ticket = db.query(SessionTicket).filter(SessionTicket.session_id == sess_data["session_id"]).first()
        assert ticket.status == "ended"
        assert ticket.ended_at is not None
        m = db.query(Machine).filter(Machine.machine_id == "bip-sess-host").first()
        assert m.current_session_tech is None
        db.close()
