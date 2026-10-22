import os
import json
import logging
import subprocess
import shutil
import socket
import urllib.request
import urllib.error
from typing import Tuple, List, Optional

logger = logging.getLogger("bipdesk.tailscale")

TAILSCALE_SOCKET_PATHS = [
    "/var/run/tailscale/tailscaled.sock",
    "/run/tailscale/tailscaled.sock",
]

def query_localapi_socket(client_ip: str) -> Optional[dict]:
    """Consulta a LocalAPI do Tailscale via socket UNIX se disponível"""
    for sock_path in TAILSCALE_SOCKET_PATHS:
        if os.path.exists(sock_path):
            try:
                # Cria socket HTTP customizado para socket UNIX
                class UnixHTTPConnection:
                    pass
                # Se estiver em Linux com socket UNIX disponível
                s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                s.connect(sock_path)
                req = f"GET /localapi/v0/whois?addr={client_ip} HTTP/1.1\r\nHost: local-tailscaled.sock\r\n\r\n"
                s.sendall(req.encode())
                response = b""
                while True:
                    chunk = s.recv(4096)
                    if not chunk:
                        break
                    response += chunk
                s.close()
                header, _, body = response.partition(b"\r\n\r\n")
                if b"200 OK" in header:
                    return json.loads(body.decode("utf-8"))
            except Exception as e:
                logger.debug(f"Falha ao consultar socket Tailscale {sock_path}: {e}")
    return None

def query_tailscale_cli(client_ip: str) -> Optional[dict]:
    """Consulta quem é o nó através do utilitário de linha de comando tailscale"""
    ts_bin = shutil.which("tailscale")
    if not ts_bin:
        # Tenta caminhos padrões do Windows
        win_path = r"C:\Program Files\Tailscale\tailscale.exe"
        if os.path.exists(win_path):
            ts_bin = win_path

    if not ts_bin:
        return None

    try:
        res = subprocess.run(
            [ts_bin, "whois", "--json", client_ip],
            capture_output=True,
            text=True,
            timeout=3
        )
        if res.returncode == 0 and res.stdout.strip():
            return json.loads(res.stdout)
    except Exception as e:
        logger.debug(f"Falha ao executar 'tailscale whois': {e}")
    return None

def verify_tailscale_peer_tag(client_ip: str, required_tag: str = "tag:bipdesk-host") -> Tuple[bool, str]:
    """
    Verifica a identidade do peer via Tailscale LocalAPI (Seção 5 - Defesa em Profundidade).
    Retorna (is_allowed, reason).
    """
    # Em ambiente local / testes unitários de loopback, permite prosseguir com aviso
    if client_ip in ("127.0.0.1", "::1", "localhost", "testclient"):
        return True, "Ambiente local/teste (loopback permitido para verificação Ed25519)"

    # 1. Tenta consulta via socket UNIX
    data = query_localapi_socket(client_ip)

    # 2. Tenta consulta via CLI
    if not data:
        data = query_tailscale_cli(client_ip)

    # 3. Se LocalAPI não estiver disponível, registra a limitação e continua com a assinatura Ed25519
    if not data:
        logger.warning(
            f"[TAILSCALE DEFESA] LocalAPI não disponível para IP {client_ip}. "
            "Prosseguindo com defesa primária via assinatura criptográfica Ed25519."
        )
        return True, "LocalAPI indisponível; prosseguindo com verificação Ed25519 obrigatória"

    # 4. Verifica se a tag requerida está presente no Node
    node = data.get("Node", {})
    tags: List[str] = node.get("Tags") or []
    
    # Se o nó possui tags explícitas
    if required_tag in tags:
        return True, f"Peer verificado com sucesso no Tailscale com {required_tag}"

    # Rejeição estrita se o nó do Tailscale não possuir a tag necessária
    logger.warning(
        f"[SEGURANÇA] Conexão recusada: IP {client_ip} no Tailscale não possui {required_tag}. Tags encontradas: {tags}"
    )
    return False, f"Acesso negado: Nó Tailscale não possui a tag requerida '{required_tag}'"
