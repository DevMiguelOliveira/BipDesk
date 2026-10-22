# ADR-011: Identidade Criptográfica do Host com Ed25519, DPAPI e Enrollment de Uso Único

- **Status:** Aprovado
- **Data:** 2026-10-21
- **Contexto:** 
  Para erradicar a possibilidade de sequestro de identidade de máquinas gerenciadas ou injeção de máquinas fantasmas, cada estação cliente precisa possuir um par de chaves assimétricas único e intransferível gerado localmente, com armazenamento protegido pelo subsistema de segurança do sistema operacional (DPAPI no Windows).

## Opções Consideradas

1. **Tokens Simétricos Compartilhados**:
   - *Prós*: Simplicidade de implementação.
   - *Contras*: O servidor precisa armazenar o segredo ou seu hash; se a chave vazar do cliente ou servidor, comandos e telemetria podem ser forjados por atacantes.
2. **Identidade Assimétrica Ed25519 com Proteção DPAPI e Enrollment de Uso Único (256-bit Token)**:
   - *Prós*: A chave privada nunca sai da máquina host. É cifrada com a Windows Data Protection API (`CryptProtectData`) e com ACL restrita estritamente a `SYSTEM` e `Administradores`. O enrollment exige um token de uso único gerado pelo técnico com validade de 24h, assinado com a chave do host no primeiro contato.
   - *Contras*: Requer implementação nativa de chamadas Win32 Crypt32.dll em Go.

## Decisão

Adotar a **Opção 2**:
1. O agente Go gera seu par de chaves **Ed25519** (32 bytes pública, 64 bytes privada) na primeira execução.
2. A chave privada é cifrada via **DPAPI** (`CryptProtectData`) com flags de proteção do sistema e gravada em `%ProgramData%\BipDesk\host_ed25519.key`.
3. As permissões ACL do arquivo de chave são restritas estritamente para `NT AUTHORITY\SYSTEM` e `BUILTIN\Administrators` via `icacls.exe`.
4. O processo de vinculação (Enrollment) utiliza um token de uso único de 256 bits (`/api/machines/pending`). Ao submeter o token assinado via `/api/agent/enroll`, o token é permanentemente consumido (`consumed_at`), vinculando a chave pública do host no banco de dados.
5. Tentativas de reuso de token ou submissão de token expirado/pertencente a outra máquina geram `SecurityEvent` crítico e retornam HTTP 403.

## Consequências

- **Positivas**: Impossibilita falsificação de identidade de hosts mesmo em redes partilhadas. A chave privada não pode ser extraída por usuários não administradores.
- **Negativas/Atenção**: Clonagem de imagem de disco (ghost/sysprep) sem limpar o arquivo de chave gerará conflito de chave pública na API; o instalador deve remover a chave antes de capturar imagens.
