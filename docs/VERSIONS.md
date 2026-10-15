# BipDesk - Versões Pinadas do Sistema

Para garantir reprodutibilidade, segurança e evitar quebras de compatibilidade, todas as dependências críticas do BipDesk são estritamente pinadas:

| Componente | Versão Pinada | Origem / Repositório |
| :--- | :--- | :--- |
| **BipDesk Session (Motor)** | `1.4.9` | Fork baseado na tag `1.4.9` do RustDesk |
| **Servidor de Sinalização (hbbs)**| `1.1.16` | Imagem Docker `rustdesk/rustdesk-server:1.1.16` (fallback: `1.1.15`) |
| **Servidor de Relay (hbbr)** | `1.1.16` | Imagem Docker `rustdesk/rustdesk-server:1.1.16` (fallback: `1.1.15`) |
| **Proxy Reverso & TLS** | `2.10.2` | Imagem Docker `caddy:2.10.2` (nunca `latest`) |
| **API Backend** | `Python 3.12` | FastAPI 0.115+, Uvicorn, SQLite com WAL |
| **BipDesk Admin (Tech App)** | `.NET 8.0 WPF` | Windows 10/11 x64, C# 12 |
| **BipDesk Agent (Host)** | `Go 1.24` | Windows x64 Service & Heartbeat Daemon |
| **Ambiente de Build CI** | `windows-latest` | GitHub Actions (Flutter / Rust / vcpkg / .NET SDK) |

> **Nota:** Não atualizar para tags flutuantes como `latest` em ambientes de produção.
