# Guia de Implantação BipDesk na Oracle Cloud Free Tier

Este guia detalha o processo completo para provisionar a infraestrutura de sinalização e relay do BipDesk em uma instância gratuita da Oracle Cloud (1 GB de RAM), com suporte automático a HTTPS e custo zero.

---

## 1. Pré-Requisitos na Console Oracle Cloud

### 1.1 Criar a Instância Gratuita
- **Tipo de Instância:** `VM.Standard.E2.1.Micro` (Always Free, 1 OCPU, 1 GB RAM) ou `VM.Standard.A1.Flex` (Ampere).
- **Sistema Operacional:** Ubuntu 22.04 LTS ou Oracle Linux 8/9.
- **Armazenamento:** 47 GB (Always Free).
- **Chave SSH:** Utilize a chave SSH gerada localmente (`ssh-key-2026-10-15.key`).

### 1.2 Configurar o Firewall da VCN (Security List)
No painel da Oracle: **Networking > Virtual Cloud Networks > VCN > Security Lists > Default Security List**.
Adicione as seguintes **Ingress Rules** (CIDR de Origem: `0.0.0.0/0`):

| Protocolo | Porta de Destino | Finalidade |
| :--- | :--- | :--- |
| **TCP** | `80` | Validação Let's Encrypt / HTTP |
| **TCP** | `443` | Caddy HTTPS (API + Downloads) |
| **TCP** | `21115` | hbbs (Teste de tipo NAT) |
| **TCP** | `21116` | hbbs (Sinalização e Heartbeat) |
| **UDP** | `21116` | hbbs (Perfuração NAT / Registro P2P) |
| **TCP** | `21117` | hbbr (Relay Criptografado de Vídeo) |

> ⚠️ **Atenção:** Restrinja a porta `22 (SSH)` apenas ao seu IP público para evitar ataques de força bruta.

---

## 2. Configurar o DuckDNS (Domínio Gratuito)
1. Acesse [DuckDNS.org](https://www.duckdns.org) e faça login.
2. Crie um subdomínio (exemplo: `bipdesk.duckdns.org`).
3. Aponte o subdomínio para o **IP Público da sua VM Oracle**.

---

## 3. Preparação da VM (Execução do Bootstrap)

Conecte-se na VM via SSH:
```bash
ssh -i ssh-key-2026-10-15.key ubuntu@<SEU-IP-ORACLE>
```

Clone ou transfira a pasta `deploy` para a VM e execute o bootstrap:
```bash
cd /opt
sudo git clone https://github.com/SEU-USUARIO/BipDesk.git
cd BipDesk/deploy
sudo chmod +x bootstrap.sh
sudo ./bootstrap.sh
```

O `bootstrap.sh` realiza automaticamente:
- Criação de **2 GB de Swap** (garante estabilidade sem OOM na VM de 1 GB).
- Liberação do tráfego das portas do BipDesk no `iptables` interno da Oracle.
- Instalação e inicialização do Docker Engine.
- Criação dos volumes persistentes em `/var/lib/bipdesk-*`.

---

## 4. Subir os Serviços via Docker Compose

1. Defina a variável de ambiente do seu domínio:
```bash
export DOMAIN=bipdesk.duckdns.org
```

2. Inicie a stack do BipDesk:
```bash
sudo -E docker compose up -d
```

3. Verifique se os 4 containers estão em execução:
```bash
sudo docker compose ps
```
Você verá:
- `bipdesk-hbbs` (Sinalização)
- `bipdesk-hbbr` (Relay)
- `bipdesk-api` (API FastAPI + SQLite WAL)
- `bipdesk-caddy` (Proxy Reverso + Certificado SSL)

---

## 5. Extração da Chave Pública de Sinalização (RS_PUB_KEY)

O `hbbs` gera automaticamente um par de chaves Ed25519 na primeira inicialização.
Para obter a chave pública:
```bash
sudo cat /var/lib/bipdesk-server/id_ed25519.pub
```
*Copie a string impressa (exemplo: `ABcD1234...`).*

### Adicionar nos Segredos do GitHub (GitHub Secrets)
No seu repositório no GitHub: **Settings > Secrets and variables > Actions > New repository secret**:
1. `RENDEZVOUS_SERVER`: `bipdesk.duckdns.org`
2. `RS_PUB_KEY`: `<conteúdo do id_ed25519.pub>`

---

## 6. Distribuição dos Binários para Clientes

Quando os workflows do GitHub Actions compilarem `BipDesk_Session.exe` e `bipdesk-agent.exe`, envie esses dois arquivos para a pasta `/var/www/bipdesk-downloads/` na VM:
```bash
# Na sua máquina local:
scp -i ssh-key-2026-10-15.key dist/session/BipDesk_Session.exe ubuntu@<IP>:/tmp/
scp -i ssh-key-2026-10-15.key dist/agent/bipdesk-agent.exe ubuntu@<IP>:/tmp/

# Na VM:
sudo mv /tmp/BipDesk_Session.exe /var/www/bipdesk-downloads/
sudo mv /tmp/bipdesk-agent.exe /var/www/bipdesk-downloads/
sudo chmod 644 /var/www/bipdesk-downloads/*
```

Agora, o comando de 1 linha de instalação do **BipDesk Host** estará 100% operacional para qualquer máquina cliente!
