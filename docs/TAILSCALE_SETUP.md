# Guia de Configuração BipDesk com Tailscale Mesh (ADR-003)

Este guia orienta a implantação do servidor BipDesk utilizando a malha privada **Tailscale**, eliminando completamente a necessidade de IP público fixo, abertura de portas em roteadores domésticos ou exposição a varreduras de portas na internet pública.

---

## 1. Vantagens da Arquitetura com Tailscale

1. **Zero Portas Abertas na Internet Pública:** Nenhum invasor na internet consegue alcançar a porta 80, 443, 21116 ou 8080.
2. **NAT Traversal Nativo (WireGuard P2P):** O protocolo DERP e WireGuard do Tailscale resolve automaticamente CGNAT de operadoras de internet residencial.
3. **Desempenho UDP Nativo:** O motor RustDesk obtém baixa latência de vídeo streaming mesmo entre redes diferentes.
4. **Criptografia de Ponta a Ponta:** Todo tráfego entre console do técnico, servidor e agente trafega encapsulado em túneis WireGuard autenticados por chave pública.

---

## 2. Passo a Passo de Implantação

### Passo 1: Obter a Chave de Autenticação Tailscale
1. Acesse o console administrativo do Tailscale: [login.tailscale.com/admin/settings/keys](https://login.tailscale.com/admin/settings/keys).
2. Clique em **Generate auth key**.
3. Selecione:
   - **Reusable:** Marcado (permite recriar o container sem gerar nova chave).
   - **Expiration:** 90 dias (ou conforme sua política).
   - **Tags:** Atribua uma tag de servidor (ex: `tag:server`).
4. Copie a chave gerada (`tskey-auth-...`).

---

### Passo 2: Configurar o Arquivo `.env`
Na pasta `deploy/`:
```bash
cp tailscale.env.example .env
nano .env
```
Preencha:
- `TS_AUTHKEY`: A chave copiada no passo 1.
- `TS_HOSTNAME`: `bipdesk-server`
- `DOMAIN`: O nome MagicDNS atribuído pelo Tailscale (ex: `bipdesk-server.sua-tailnet.ts.net`) ou o IP Tailscale da máquina (`100.x.y.z`).
- `SECRET_KEY`, `ADMIN_PASSWORD` e `FERNET_KEYS`.

---

### Passo 3: Iniciar a Stack com Docker Compose
Execute o compose específico do Tailscale:
```bash
docker compose -f docker-compose.tailscale.yml up -d
```

Verifique se todos os contêineres subiram saudáveis:
```bash
docker compose -f docker-compose.tailscale.yml ps
```

---

### Passo 4: Conectar o Console do Técnico e as Máquinas Clientes
Tanto o computador do técnico quanto os clientes precisam estar conectados na mesma tailnet:
1. Instale o aplicativo oficial do Tailscale nas máquinas.
2. No console do técnico (`BipDesk Admin`), configure a URL da API para:
   `https://bipdesk-server.sua-tailnet.ts.net` (ou `http://100.x.y.z`).
3. No comando de instalação do agente cliente (`install-host.ps1`):
   `$ServerUrl = "https://bipdesk-server.sua-tailnet.ts.net"`
