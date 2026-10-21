# Guia de Implantação e Operação BipDesk Server

Este guia detalha o processo completo para provisionar e operar a infraestrutura de sinalização, relay e API do **BipDesk** em um servidor Linux (VPS, servidor dedicado ou nó de malha Tailscale / Headscale), com consumo inferior a 650 MB de RAM e suporte a mais de 1.000 máquinas clientes.

---

## 1. Topologia de Rede e Hospedagem (ADR-003)

O BipDesk oferece suporte a duas opções de exposição de rede:

1. **Rede Privada em Malha (Tailscale / Headscale Mesh - Recomendado):**
   - Zero portas de entrada expostas para a internet pública aberta.
   - Tráfego direto P2P criptografado entre técnicos e clientes.
   - Ideal para servidores locais ou sem IP público estático.
2. **Servidor Linux com IP Público / VPS:**
   - Exposição via Caddy HTTPS (portas 80 e 443).
   - Portas RustDesk: `21115/TCP`, `21116/TCP`, `21116/UDP`, `21117/TCP`.

---

## 2. Requisitos de Portas e Firewall

Se optar por servidor Linux com IP público:

| Protocolo | Porta de Destino | Finalidade |
| :--- | :--- | :--- |
| **TCP** | `80` | Desafio ACME Let's Encrypt / Redirecionamento HTTPS |
| **TCP** | `443` | Caddy HTTPS (API REST, Heartbeat e Downloads) |
| **TCP** | `21115` | hbbs (Teste de tipo NAT) |
| **TCP** | `21116` | hbbs (Sinalização e Registro de IDs) |
| **UDP** | `21116` | hbbs (Perfuração NAT STUN / P2P) |
| **TCP** | `21117` | hbbr (Relay P2P Criptografado) |

> ⚠️ **Atenção:** A porta `8080` da API FastAPI **não** deve ser exposta publicamente. Ela roda exclusivamente na rede interna Docker `bipdesk-net`, protegida pelo Caddy.

---

## 3. Preparação do Servidor (Bootstrap)

Conecte-se ao servidor Linux via SSH utilizando sua chave configurada:
```bash
ssh -i ~/.ssh/sua_chave_privada usuario@<IP_OU_HOST_DO_SERVIDOR>
```

Clone o repositório e execute o script de inicialização:
```bash
cd /opt
sudo git clone https://github.com/DevMiguelOliveira/BipDesk.git
cd BipDesk/deploy
sudo chmod +x bootstrap.sh
sudo ./bootstrap.sh
```

O `bootstrap.sh` realiza automaticamente:
- Criação de **2 GB de Swap** (garante estabilidade sem OOM em instâncias com 1 GB de RAM).
- Ajustes de buffers de rede via `sysctl`.
- Liberação das portas requeridas no firewall.
- Inicialização do Docker Engine.
- Criação dos volumes persistentes do sistema.

---

## 4. Configuração de Variáveis de Ambiente (.env)

Crie o arquivo `deploy/.env` a partir do modelo seguro:
```bash
cp .env.example .env
nano .env
```

Preencha os valores obrigatórios (ADR-004):
- `DOMAIN`: Seu domínio ou IP no Tailscale (ex: `bipdesk.duckdns.org` ou `bipdesk.sua-rede.ts.net`).
- `SECRET_KEY`: Chave aleatória de alta entropia para JWT (mínimo 32 caracteres).
- `ADMIN_PASSWORD`: Senha inicial do usuário administrador.
- `FERNET_KEYS`: Chave Fernet (ou lista separada por vírgulas para rotação).

---

## 5. Subir os Serviços via Docker Compose

Inicie os contêineres:
```bash
sudo docker compose up -d
```

Verifique o status e os healthchecks:
```bash
sudo docker compose ps
```
Você verá os 4 serviços saudáveis:
- `bipdesk-hbbs` (Sinalização - healthy)
- `bipdesk-hbbr` (Relay P2P)
- `bipdesk-api` (API FastAPI + SQLite WAL - healthy)
- `bipdesk-caddy` (Proxy Reverso HTTPS - healthy)

---

## 6. Backup Seguro do Banco de Dados

Para realizar o backup consistente do banco de dados SQLite em modo WAL (sem risco de corrupção):
```bash
sudo ./backup-db.sh
```
Configure uma entrada no crontab para execução diária:
```bash
0 3 * * * /opt/BipDesk/deploy/backup-db.sh >> /var/log/bipdesk-backup.log 2>&1
```
