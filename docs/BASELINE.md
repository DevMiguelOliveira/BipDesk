# 📊 Linha de Base do Repositório (Baseline) — BipDesk

> **Data:** 20 de Outubro de 2026  
> **Branch de Trabalho:** `security/hardening`  
> **Responsável:** Engenheiro de Segurança e Desenvolvimento Sênior  
> **Objetivo:** Registro do estado inicial antes de qualquer alteração de código (Fase 0).

---

## 1. Mapeamento da Árvore do Repositório

O repositório está organizado nos seguintes módulos principais:

```
BipDesk/
├── agent/                  # Agente cliente em Go (Windows Service BipDeskHost)
├── api/                    # API central em FastAPI (Python 3.12)
├── client/                 # Patches e recursos estáticos do cliente RustDesk
├── deploy/                 # Docker Compose, Caddyfile e scripts PowerShell de operação
├── dist/                   # Binários e pacotes gerados previamente
├── docs/                   # Documentações de arquitetura, versões e deploy
├── tech-app/               # Console do operador técnico em C# .NET 8 WPF
└── .github/workflows/      # Pipelines de CI/CD para build de artefatos
```

---

## 2. Inventário de Dependências e Componentes

### 2.1. Agente (`agent/go.mod`)
- **Versão Go declarada**: `1.24`
- **Módulo**: `bipdesk/agent`
- **Dependências externas**: Nenhuma dependência externa declarada em `go.mod`. O agente utiliza bibliotecas padrão de Go e chamadas Win32 diretas via DLLs do sistema operacional (`kernel32.dll`, `user32.dll`, `advapi32.dll`).

### 2.2. Backend API (`api/requirements.txt` e `api/Dockerfile`)
- **Base Docker**: `python:3.12-slim`
- **Pacotes fixados**:
  - `fastapi==0.115.6`
  - `uvicorn[standard]==0.34.0`
  - `pydantic==2.10.4`
  - `sqlalchemy==2.0.36`
  - `cryptography==44.0.0`
  - `python-jose[cryptography]==3.3.0`
  - `bcrypt==4.2.1`
  - `python-multipart==0.0.20`
  - `slowapi==0.1.8`
  - `python-dotenv==1.0.0`
  - `psycopg2-binary>=2.9.9` (driver para PostgreSQL caso configurado)

### 2.3. Console do Técnico (`tech-app/BipDesk.Tech.csproj`)
- **Framework alvo**: `net8.0-windows`
- **Tipo de Projeto**: WinExe com `UseWPF=true`
- **Nullable**: `enable`
- **Dependências NuGet**: `System.Net.Http.Json` (8.0.1)

### 2.4. Infraestrutura (`deploy/docker-compose.yml`)
- **hbbs**: `rustdesk/rustdesk-server:1.1.16` (limite: 120M)
- **hbbr**: `rustdesk/rustdesk-server:1.1.16` (limite: 120M)
- **api**: imagem local `bipdesk-api:1.0.0` baseada em `../api/Dockerfile` (limite: 256M)
- **caddy**: `caddy:2.10.2` (limite: 128M)
- **Portas publicadas**: 21115:21115 (TCP), 21116:21116 (TCP/UDP), 21117:21117 (TCP), 8080:8080 (TCP), 80:80 (TCP), 443:443 (TCP).

### 2.5. Workflows de CI/CD (`.github/workflows/`)
- `build-agent.yml`: Roda em `windows-latest`, instala Go 1.24, compila `bipdesk-agent.exe` com flags `-s -w -H=windowsgui` e `go-winres`.
- `build-tech-app.yml`: Roda em `windows-latest`, instala .NET 8 SDK, publica single-file executável com auto-extração nativa e compacta bundle.
- `build-session.yml`: Roda em `windows-latest`, baixa release upstream `1.4.9` do RustDesk e injeta ícone e branding usando Resource Hacker.

---

## 3. Execução de Builds e Testes Existentes

| Componente | Comando Executado | Resultado Real | Observações |
|---|---|---|---|
| **Go Agent** (Testes) | `go test ./...` | `? bipdesk/agent [no test files]` (Código 0) | Nenhum arquivo de teste unitário existia no repositório. |
| **Go Agent** (Build) | `go build -o test_build.exe main.go` | **Sucesso** (Código 0) | Compilação estática bem-sucedida em Windows x64. |
| **API Python** (Testes) | `pytest` | **Falha de execução** (Código 1) | `pytest` não está instalado/configurado no ambiente local. Nenhum teste unitário `test_*.py` existia no repositório. |
| **Console .NET** (Build) | `dotnet build` | **Não executável localmente** | .NET 8 SDK não instalado no path global desta máquina (apenas runtime). Compilação requer SDK ou execução via CI / Visual Studio. |

---

## 4. Estado Inicial do Git

- **Branch criada**: `security/hardening`
- **Restrição**: Nenhum commit realizado na branch `main`. Todo o trabalho de refatoração, testes e hardening ocorrerá nesta branch dedicada.
