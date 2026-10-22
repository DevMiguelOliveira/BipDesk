# ADR-013: Pinning de Chave Pública do Servidor e Proteção Anti-Tampering

- **Status:** Aprovado
- **Data:** 2026-10-21
- **Contexto:** 
  Ataques de intermediário (Man-in-the-Middle) ou redirecionamentos de DNS maliciosos poderiam tentar redirecionar o agente host para um servidor falso controlado por um atacante. Para prevenir que um servidor invasor forneça comandos ou ofertas de sessão para as máquinas legítimas, o host deve validar criptograficamente a identidade do servidor.

## Opções Consideradas

1. **Confiar exclusivamente na cadeia de certificados TLS do SO**:
   - *Prós*: Padrão da internet.
   - *Contras*: Se uma autoridade certificadora (CA) raiz for comprometida ou um certificado autoassinado/corporativo for instalado na máquina cliente (ex: interceptadores corporativos de SSL), o tráfego pode ser inspecionado ou forjado.
2. **Pinning de Chave Pública Ed25519 do Servidor no Host**:
   - *Prós*: O instalador do host embute ou recebe a chave pública Ed25519 oficial do servidor central (`SERVER_PUBLIC_KEY`). Todas as mensagens críticas enviadas pelo servidor (ordens de sessão, comandos, encerramentos) devem conter assinatura Ed25519 válida correspondente a essa chave exata. Assinaturas de servidores desconhecidos ou divergentes são sumariamente descartadas.
   - *Contras*: Exige procedimento de rotação assistida da chave do servidor caso a chave mestra seja alterada.

## Decisão

Adotar a **Opção 2**:
1. O servidor central possui um par Ed25519 permanente salvo em `%ProgramData%\BipDesk\server_ed25519.key` (ou `/app/secrets/server_ed25519.key` no Docker).
2. A chave pública do servidor é servida em `/api/server-key` e embutida no comando de instalação do agente gerado pelo painel (`install-host.ps1 -ServerKey <hex>`).
3. O agente Go armazena o valor do pinning na flag `-server-key` ou em arquivo de configuração local protegido.
4. No método `verifyServerEnvelope`, se a chave que assinou a mensagem não corresponder rigorosamente ao pinning configurado, o envelope é rejeitado imediatamente.

## Consequências

- **Positivas**: Proteção matemática contra servidores falsos; nem mesmo um atacante com controle sobre o domínio DNS do servidor ou com CA intermediária instalada consegue forjar mensagens para os hosts.
- **Negativas/Atenção**: A rotação de chave privada do servidor exige publicação prévia e período de transição com suporte a chaves duplas ou atualização remota supervisionada.
