# ADR-007: Integridade de Binários Distribuídos e Verificação de Hash SHA-256

- **Status:** Aprovado
- **Data:** 2026-10-20
- **Contexto:** 
  O agente e o console técnico realizam o download de binários executáveis (`BipDesk_Session.exe`, `icon.ico`, `bipdesk-agent.exe`) a partir do servidor web Caddy (`/downloads/*`) e os executam diretamente sem verificar o hash criptográfico ou a assinatura digital (A8). Se o servidor for comprometido ou se ocorrer envenenamento de DNS/cache, um binário adulterado seria executado com privilégios elevados.

## Opções Consideradas

1. **Confiar Exclusivamente no Transporte TLS**:
   - *Prós*: Sem overhead de código.
   - *Contras*: Insuficiente. Se o servidor for invadido ou um arquivo corrompido, o cliente executa o malware com sucesso através do túnel TLS legítimo.
2. **Manifesto de Hashes SHA-256 Publicado com Validação Pré-Execução**:
   - *Prós*: 
     - O servidor publica um arquivo de manifesto `checksums.json` contendo o mapa de executáveis e seus respectivos hashes SHA-256 calculados durante o processo de build do CI/CD.
     - Tanto o agente Go quanto o console WPF C# calculam o hash SHA-256 do arquivo baixado e comparam com o valor esperado antes de gravar em `Program Files` ou invocar `Process.Start`.
     - Caso o hash divirja, a execução é abortada imediatamente com registro de auditoria e alerta na UI.
   - *Contras*: Requer geração automatizada de `checksums.json` na esteira do GitHub Actions.

## Decisão

Adotar a **Opção 2**:
1. Gerar e publicar hashes SHA-256 dos executáveis no diretório `/downloads/` e em manifesto json.
2. No agente Go: implementar função `verifySHA256(filePath, expectedHash)` antes de instalar o serviço ou executar o motor de sessão.
3. No console C#: implementar cálculo de hash SHA-256 com `System.Security.Cryptography.SHA256` no método `EnsureSessionExecutableAsync` de `SessionLauncher.cs`.

## Consequências

- **Positivas**: Impede a execução de binários corrompidos ou adulterados no cliente e no console. Fornece uma camada robusta de integridade na cadeia de suprimentos de software (*supply chain*).
- **Negativas/Atenção**: Cada nova versão dos executáveis distribuídos pelo servidor deve ter seu hash devidamente registrado no manifesto de distribuição.
