# 🛡️ Validação e Status de Correção dos Achados da Auditoria — BipDesk

> **Data da Validação Inicial:** 20 de Outubro de 2026  
> **Data de Conclusão das Correções:** 20 de Outubro de 2026  
> **Versão Inicial Auditada:** 2.1.0  
> **Versão Endurecida e Validada:** 2.4.0  
> **Branch de Trabalho:** `security/hardening`  

---

## 1. Matriz Consolidada dos Achados (A1 a A15)

| ID | Status Inicial | Status Final | Componente Afetado | Correção Aplicada e Evidência |
|:---|:---:|:---:|:---|:---|
| **A1** | `DIVERGENTE` | **CORRIGIDO** | `agent/main.go`<br/>`api/app/main.py` | Ação `cmd` completamente eliminada. Enum fechado estrito (`cad`, `restart_spooler`, `flush_dns`, `lock`, `reboot`) assinado com HMAC-SHA256, nonce anti-replay e expiração curta (<60s). Validado em `agent/agent_test.go` e `api/tests/test_security_and_api.py`. |
| **A2** | `CONFIRMADO` | **CORRIGIDO** | `agent/main.go`<br/>`deploy/install-host.ps1`<br/>`deploy/.env.example`<br/>`.gitignore` | Chave pública do RustDesk hardcoded removida de `agent/main.go` e `install-host.ps1`. Chave agora obtida dinamicamente via `/api/server-key` ou enrollment. `SECRET_KEY` removida do repositório; `.gitignore` e `.env.example` criados sem segredos reais. |
| **A3** | `CONFIRMADO` | **CORRIGIDO** | `api/app/main.py`<br/>`api/app/security.py` | Senhas padrão fixas (`ADMIN_DEFAULT_PASSWORD` e `HOST_DEFAULT_PASSWORD`) eliminadas. API recusa iniciar em produção sem `ADMIN_PASSWORD` e `SECRET_KEY`. Senhas de máquinas geradas com 24 caracteres de alta entropia. |
| **A4** | `DIVERGENTE` | **CORRIGIDO** | `agent/main.go`<br/>`tech-app/Services/ApiClient.cs` | Identificador `InsecureSkipVerify` removido do agente e versão mínima TLS 1.2 imposta. Callback de bypass (`ServerCertificateCustomValidationCallback = (...) => true`) removido do console WPF; validação estrita da cadeia do SO ativada (ADR-005). |
| **A5** | `CONFIRMADO` | **CORRIGIDO** | `api/app/main.py` | Auto-cadastro inseguro no heartbeat desativado. Máquinas desconhecidas retornam HTTP 404. O registro exige token de enrollment temporário de uso único via `/api/agent/enroll`. |
| **A6** | `CONFIRMADO` | **CORRIGIDO** | `api/app/main.py`<br/>`agent/main.go` | Autenticação mútua obrigatória: cada máquina recebe um `machine_token` no enrollment, validado com `hmac.compare_digest` contra `token_hash` SHA-256 no banco. Heartbeat sem token retorna 401; token inválido retorna 403. |
| **A7** | `DIVERGENTE` | **CORRIGIDO** | `api/app/main.py` | `SlowAPI` estendido para `/api/login` (15/min), `/api/agent/enroll` (15/min) e `/api/agent/heartbeat` (60/min), retornando HTTP 429 em caso de abuso. Validado em teste unitário. |
| **A8** | `CONFIRMADO` | **CORRIGIDO** | `agent/main.go`<br/>`tech-app/Services/SessionLauncher.cs` | Downloads de executáveis agora validam a integridade via hash criptográfico SHA-256 e gravam em arquivo `.part` temporário antes de promover para execução (ADR-007). |
| **A9** | `CONFIRMADO` | **CORRIGIDO** | `agent/main.go`<br/>`deploy/install-host.ps1` | Auto-elevação silenciosa via PowerShell `Start-Process -Verb RunAs` removida do agente. Se executado sem privilégios de Administrador, o binário notifica o usuário via MessageBox e encerra com erro explícito (ADR-008). |
| **A10** | `CONFIRMADO` | **CORRIGIDO** | `api/app/security.py` | Implementada criptografia simétrica com `MultiFernet` e rotação transparente via `rotate_encrypted_password()`. Chaves carregadas de `FERNET_KEYS` permitindo chave primária nova e chaves secundárias de leitura. Testado em pytest. |
| **A11** | `DIVERGENTE` | **CORRIGIDO** | `deploy/docker-compose.yml` | Rede interna isolada `bipdesk-net` criada. Porta 8080 da API desexposta da rede host (comunicação restrita ao Caddy). Adicionados healthchecks e limites de memória (< 650 MB total). |
| **A12** | `CONFIRMADO` | **CORRIGIDO** | `api/app/main.py` | Modelos Pydantic endurecidos com `Field`: limites estritos de tamanho (`min_length`, `max_length`), regex para identificadores numéricos e enums fechados para comandos e papéis. |
| **A13** | `CONFIRMADO` | **CORRIGIDO** | `api/app/main.py`<br/>`tech-app/Services/ApiClient.cs`<br/>`tech-app/App.xaml.cs` | Removido log de senhas no servidor. Removida a gravação do arquivo `autologin_debug.txt` no cliente do técnico. |
| **A14** | `CONFIRMADO` | **CORRIGIDO** | `agent/main.go`<br/>`tech-app/Services/ApiClient.cs` | Timeouts explícitos configurados em todos os clientes HTTP (10s para heartbeat, 15s para autenticação, 3 minutos para download de binários). |
| **A15** | `CONFIRMADO` | **CORRIGIDO** | Múltiplos arquivos | URLs e chaves desacopladas de código-fonte. Configuração centralizada em variáveis de ambiente (`BIPDESK_SERVER_HOST`, `BIPDESK_SERVER_KEY`, etc.) e suporte a rede privada Tailscale (ADR-003). |

---

## 2. Correções de Inconsistências do Relatório de Auditoria Anterior

1. **Correção do Erro na Solução 4 (FastAPI)**: O relatório original propunha um middleware/dependência inválido consumindo o corpo de requisição json de forma incompatível. A implementação corrigida utiliza o cabeçalho HTTP padrão `X-Machine-Token` e validação por hash criptográfico SHA-256.
2. **Métricas Falsas Rejeitadas**: Tabelas teóricas de "antes/depois" do relatório anterior foram expurgadas. Todos os dados desta entrega baseiam-se em testes reais executados no ambiente.
3. **Caddy e Let's Encrypt**: O mito de que ACME Let's Encrypt funciona em qualquer ambiente residencial foi corrigido no ADR-003 e ADR-005, adotando a malha privada Tailscale como recomendação primária para evitar bloqueios de operadoras.

---

## 3. Endurecimento Avançado e Arquitetura Zero-Trust (Fase 2 — ADRs 010 a 015)

| ADR | Domínio | Decisão Arquitetural e Implementação | Status e Evidência |
|:---|:---|:---|:---:|
| [ADR-010](file:///d:/PROJETOS/BipDesk/docs/adr/ADR-010-autenticacao-argon2id-e-totp-mfa.md) | Autenticação de Operadores | Hash **Argon2id** (time=3, mem=64MB, p=4) com fallback seguro para bcrypt legado; MFA via **TOTP (RFC 6238)**; tokens JWT de 15 minutos com rotação via Refresh Tokens. | **CONCLUÍDO**<br/>`test_argon2id_and_totp` |
| [ADR-011](file:///d:/PROJETOS/BipDesk/docs/adr/ADR-011-identidade-criptografica-ed25519-e-dpapi.md) | Identidade do Host | Par de chaves assimétricas **Ed25519** gerado localmente pelo agente Go; chave privada cifrada via **Windows DPAPI** (`CryptProtectData`) com ACL restrita a `SYSTEM` e `Admins`; enrollment via token único de 256 bits com expiração de 24h. | **CONCLUÍDO**<br/>`TestHostIdentity_DPAPIAndKeyGeneration`<br/>`test_one_time_token_enrollment_lifecycle` |
| [ADR-012](file:///d:/PROJETOS/BipDesk/docs/adr/ADR-012-canal-websocket-bidirecional-e-envelopes-assinados.md) | Canal em Tempo Real | Conexão WebSocket exclusivamente de saída (`/api/ws/host`); envelopes canônicos assinados (`type:machine_id:timestamp:nonce:json`); cache anti-replay (TTL 300s); tolerância máxima de skew temporal (+-120s); limite estrito de 64 KB por mensagem. | **CONCLUÍDO**<br/>`TestEd25519_ForeignSignatureRejected`<br/>`TestAntiReplay_NonceDuplicateRejected`<br/>`TestTimestamp_SkewWindowRejected`<br/>`TestOversizedMessage_Rejected` |
| [ADR-013](file:///d:/PROJETOS/BipDesk/docs/adr/ADR-013-pinning-de-chave-do-servidor-e-protecao-anti-tampering.md) | Integridade do Servidor | Chave pública do servidor fixada no instalador (`-server-key`); host descarta mensagens ou ofertas de servidores com chaves divergentes. | **CONCLUÍDO**<br/>`TestPinnedServerKey_MismatchRejected` |
| [ADR-014](file:///d:/PROJETOS/BipDesk/docs/adr/ADR-014-sessoes-efemeras-e-tickets-temporarios.md) | Sessões Efêmeras | Erradicação de senhas fixas de computadores clientes. Geração de `SessionTicket` de uso único assinado com Ed25519; credencial efêmera válida por 60 segundos transmitida diretamente ao host; purga imediata das credenciais no encerramento. | **CONCLUÍDO**<br/>`TestTicketValidation_InvalidExpiredOrReused`<br/>`test_session_ticket_lifecycle_and_termination` |
| [ADR-015](file:///d:/PROJETOS/BipDesk/docs/adr/ADR-015-defesa-em-profundidade-com-tailscale-localapi.md) | Defesa em Profundidade | Validação de nós Tailscale via **LocalAPI** (`/localapi/v0/whois` e CLI `tailscale whois`); exigência da tag `tag:bipdesk-host`; rejeição de peers não autorizados com código 4003 e auditoria. | **CONCLUÍDO**<br/>`test_tailscale_peer_tag_defense` |

---

## 4. Homologação Completa e Pipeline de Integração Contínua (CI/CD)

- **Suíte de Testes Python (`api/tests/`)**:
  - `tests/test_ed25519_and_channel.py`: 9 testes (Argon2id, TOTP, envelopes canônicos, anti-replay, tolerância de skew temporal, enrollment de 256 bits, canal WebSocket autenticado, proteção contra payloads acima de 64 KB, defesa por tags Tailscale e ciclo de vida de tickets de sessão).
  - `tests/test_security_and_api.py`: 11 testes (fail-fast de SECRET_KEY em produção, rotação de chaves MultiFernet, JWT HS256 estrito, sanitização e regex de IDs RustDesk, enum estrito de comandos remotos, obrigatoriedade de X-Machine-Token, rate limiting SlowAPI, CRUD de técnicos, listagem de grupos, tickets de conexão, notas e endpoints de compatibilidade RustDesk).
  - **Resultado**: **20 testes executados — 20 aprovados (100% de sucesso)**.
- **Suíte de Testes Go (`agent/agent_test.go`)**:
  - 9 testes unitários: proteção de chave via Windows DPAPI, descarte de assinaturas estrangeiras, descarte de nonces repetidos, descarte de mensagens fora da janela temporal, integridade e pinning de chave do servidor, recusa de mensagens > 64 KB, ciclo de vida e expiração de tickets temporários, rejeição estrita fora do enum fechado de comandos e extração de ID limpo.
  - **Resultado**: **9 testes executados — 9 aprovados (100% de sucesso)**.
- **Compilação de Binários**:
  - Binário `agent/bipdesk-agent.exe` compilado com `-ldflags "-s -w -H=windowsgui"` sem avisos.
- **Automação de CI/CD GitHub Actions**:
  - `.github/workflows/build-agent.yml`: Compilação e hash SHA-256 do agente Go com pinned actions.
  - `.github/workflows/build-tech-app.yml`: Build do console WPF com pinned actions.
  - `.github/workflows/build-session.yml`: Empacotamento do motor RustDesk com pinned actions.
  - `.github/workflows/test-api.yml`: Execução contínua da suíte Pytest com pinned actions no GitHub Actions.


