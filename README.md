# BipDesk - Solução Própria de Gerenciamento e Suporte Remoto

BipDesk é uma solução completa e moderna de acesso remoto não assistido e suporte técnico corporativo, projetada para substituir ferramentas proprietárias (como TeamViewer e AnyDesk) com custo zero de infraestrutura e controle total dos dados.

---

## 🌟 Visão do Produto: O Modelo Host & Admin

Inspirado no modelo consolidado do **TeamViewer**, o BipDesk é dividido em dois papéis claros e intuitivos:

```
┌──────────────────────────────────────┐          ┌──────────────────────────────────────┐
│        BipDesk Host (Cliente)        │          │        BipDesk Admin (Técnico)       │
├──────────────────────────────────────┤          ├──────────────────────────────────────┤
│ • Instalador silencioso de 1 linha   │          │ • Console nativo moderno (.NET 8)    │
│ • Roda como serviço de sistema (24/7)│          │ • Grid com status Online/Offline     │
│ • Zero janelas confusas para usuário │          │ • Pesquisa global instantânea (Ctrl+K)│
│ • Acesso com senha autogerada segura │          │ • Conexão em 1 clique (Enter / Duplo)│
│ • Servidor embutido no binário       │          │ • Barra de sessões ativas no rodapé  │
└──────────────────────────────────────┘          └──────────────────────────────────────┘
                   ▲                                                 ▲
                   │                                                 │
                   └──────────────────┬──────────────────────────────┘
                                      │
                         ┌─────────────────────────┐
                         │   BipDesk Cloud (VM)    │
                         ├─────────────────────────┤
                         │ • Caddy (HTTPS / TLS)   │
                         │ • FastAPI + SQLite WAL  │
                         │ • hbbs (Sinalização)    │
                         │ • hbbr (Relay P2P)      │
                         │ Consumo: < 300 MB RAM   │
                         └─────────────────────────┘
```

---

## 🚀 Principais Características

- **Zero Configuração para o Usuário Final:** As máquinas clientes recebem apenas o instalador silencioso do **BipDesk Host**. Não solicitam IP, porta ou chave.
- **Console do Técnico de Alta Performance:** Desenvolvido em .NET 8 WPF nativo com tema escuro elegante (*Cyber Emerald & Dark Slate*), pesquisa instantânea e baixa latência.
- **Hospedagem 100% Gratuita na Oracle Cloud:** Roda com folga dentro de uma instância gratuita de 1 GB de RAM.
- **Segurança de Alto Padrão:** Criptografia de ponta a ponta com chaves Ed25519, senhas não assistidas de 24 caracteres aleatórias geradas localmente e tickets de sessão com expiração de 60 segundos.
- **Compatível com AGPL-3.0:** Marca e experiência de usuário 100% personalizadas como BipDesk, mantendo atribuições legais e licenças de código aberto intactas.

---

## 📁 Estrutura do Repositório

```
BipDesk/
├── .github/workflows/          # CI/CD: Compilação automatizada no GitHub Actions
│   ├── build-tech-app.yml      # Compilação do BipDesk Admin (.NET 8 WPF)
│   ├── build-agent.yml         # Compilação do BipDesk Host Agent (Go 1.24)
│   └── build-session.yml       # Compilação do motor de sessão (RustDesk 1.4.9)
├── agent/                      # Código-fonte do BipDesk Host Agent (Go)
│   ├── go.mod
│   └── main.go
├── api/                        # API de Inventário e Heartbeat (FastAPI + SQLite WAL)
│   ├── app/
│   │   ├── database.py
│   │   ├── main.py
│   │   ├── models.py
│   │   └── security.py
│   ├── Dockerfile
│   └── requirements.txt
├── client/patches/             # Patches de rebrand cirúrgicos no motor RustDesk
│   └── 001-rebrand-paths-and-names.patch
├── deploy/                     # Orquestração e Implantação
│   ├── bootstrap.sh            # Script de inicialização da VM Oracle
│   ├── Caddyfile               # Configuração do Proxy Reverso HTTPS
│   ├── docker-compose.yml      # Stack completa com limites de memória
│   └── install-host.ps1        # Script PowerShell de instalação silenciosa
├── docs/                       # Documentação Técnica
│   ├── DEPLOY.md               # Passo a passo de implantação na nuvem
│   └── VERSIONS.md             # Versões pinadas do ecossistema
├── tech-app/                   # BipDesk Admin para o Técnico (WPF .NET 8)
│   ├── BipDesk.Tech.csproj
│   ├── App.xaml / App.xaml.cs
│   ├── Models/
│   ├── Services/
│   ├── Styles/
│   ├── ViewModels/
│   └── Views/
├── LICENSE                     # Licença AGPL-3.0
├── NOTICE                      # Atribuição de direitos autorais originais
└── README.md
```

---

## 🛠️ Como Iniciar

1. Siga o guia [docs/DEPLOY.md](docs/DEPLOY.md) para subir a infraestrutura na sua VM gratuita da Oracle Cloud.
2. Adicione as variáveis `RENDEZVOUS_SERVER` e `RS_PUB_KEY` nos **GitHub Secrets**.
3. Dispare os workflows no GitHub Actions para obter os binários compilados:
   - `BipDesk_Admin.exe` (para o seu PC).
   - `BipDesk_Session.exe` e `bipdesk-agent.exe` (para o servidor distribuir).
4. No **BipDesk Admin**, clique em **"+ Nova Máquina"** para gerar o comando de instalação para seus clientes.

---

## ⚖️ Licença e Conformidade

Este projeto é disponibilizado sob a licença **GNU Affero General Public License v3.0 (AGPL-3.0)**.
Consulte o arquivo [LICENSE](LICENSE) e o arquivo [NOTICE](NOTICE) para mais detalhes sobre os créditos e projetos upstream.
