# Política de Segurança do BipDesk (Security Policy)

## 1. Visão Geral e Princípios Fundamentais
A segurança do BipDesk é orientada por princípios rígidos para garantir a integridade dos ambientes dos clientes e a proteção das operações de suporte técnico remoto:

1. **Zero Segredos em Código**: Chaves criptográficas, senhas e tokens nunca são embutidos em arquivos versionados, logs ou relatórios.
2. **Enum Fechado de Comandos**: Não há suporte para execução arbitrária de scripts de terminal (`cmd.exe`, PowerShell livre ou shell). Toda ação permitida é restrita a um enum assinado criptograficamente com HMAC-SHA256, nonce único e expiração curta (< 60 segundos).
3. **Comunicação Segura e Verificação TLS Estrita**: Todas as chamadas entre cliente, console e servidor operam sob TLS 1.2+ com verificação estrita de certificados da cadeia de confiança do sistema operacional.
4. **Transparência Total e Zero Modo Furtivo (LGPD)**: O BipDesk não implementa mecanismos de ocultação ou vigilância oculta. O usuário final é notificado de forma visual quando uma sessão de suporte está em andamento.
5. **Autenticação por Máquina e Token Único**: Cada estação de trabalho recebe um token criptográfico exclusivo no momento do cadastramento (Enrollment), protegido pelo subsistema DPAPI do Windows.

---

## 2. Modelo de Ameaças Resumido (Threat Model)

| Vetor de Ameaça | Mitigação Implementada no BipDesk |
|---|---|
| **Execução Remota de Código (RCE)** | Remoção total da ação `cmd`. Uso de comandos com lista estrita de argumentos fixos sem concatenação de strings. Assinatura HMAC com token da máquina. |
| **Ataques de Repetição (Replay Attacks)** | Nonce criptográfico de uso único obrigatório e validação de relógio com janela estrita (< 60 segundos). |
| **Interceptação e Adulteração de Binários** | Publicação e verificação obrigatória de hashes criptográficos SHA-256 antes da execução de binários baixados. |
| **Força Bruta em Autenticação** | Rate limiting na camada de borda e nos endpoints sensíveis (`/api/login`, `/api/agent/enroll`, `/api/agent/heartbeat`) via SlowAPI e Caddy. |
| **Vazamento de Senhas em Repouso** | Criptografia das senhas de acesso não assistido com Fernet/MultiFernet com versionamento de chaves e bcrypt (custo >= 12) para credenciais de técnicos. |
| **Invasão via Exposição de Portas** | Modelo de rede privada em malha (Tailscale/Headscale Mesh) sem necessidade de portas públicas de entrada abertas para a internet pública (ADR-003). |

---

## 3. Como Reportar uma Vulnerabilidade
Se você identificou uma vulnerabilidade de segurança ou falha potencial no BipDesk:

1. **NÃO crie issues públicas no GitHub** contendo detalhes exploráveis da falha.
2. Envie um e-mail com a descrição detalhada, passos de reprodução e prova de conceito (PoC) para:
   **security@bipdesk.local** (ou utilize o canal privado de Security Advisories do repositório GitHub).
3. Nossa equipe analisará e responderá ao relatório no prazo de até 48 horas úteis.
4. Uma correção será coordenada e distribuída antes de qualquer divulgação pública coordenada.
