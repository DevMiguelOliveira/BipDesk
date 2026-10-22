# ADR-015: Defesa em Profundidade com Validação de Peer Tags na Tailscale LocalAPI

- **Status:** Aprovado
- **Data:** 2026-10-21
- **Contexto:** 
  Na arquitetura Tailscale Mesh (ADR-003), nós clientes e o servidor compartilham uma rede overlay WireGuard criptografada (`100.64.0.0/10`). Para estabelecer um modelo rigoroso de Zero-Trust em múltiplas camadas (Defense in Depth), a aplicação central deve validar tanto a camada de rede (identidade do peer WireGuard no Tailscale) quanto a camada de aplicação (assinatura Ed25519).

## Opções Consideradas

1. **Confiar exclusivamente na rede Tailscale ou exclusivamente na assinatura Ed25519**:
   - *Prós*: Menos código acoplado ao daemon Tailscale.
   - *Contras*: Se uma única camada tiver falha de configuração (ex: ACL do Tailscale permissiva demais), a proteção fica desguarnecida.
2. **Defesa em Profundidade Híbrida: Verificação de Peer Tags via LocalAPI + Assinatura Ed25519 Obrigatória**:
   - *Prós*: Quando uma conexão entra via WebSocket ou HTTP vinda de um IP Tailscale (100.x.y.z), o servidor consulta a LocalAPI do daemon `tailscaled` (`/localapi/v0/whois?addr=...` ou `tailscale whois --json <ip>`). O servidor valida se o nó solicitante possui a tag apropriada (`tag:bipdesk-host` para agentes ou `tag:bipdesk-tech` para operadores). Nós sem a tag são rejeitados antes de consumir processamento de autenticação.
   - *Contras*: O contêiner da API precisa de acesso ao socket `/var/run/tailscale/tailscaled.sock` para chamadas locais de alta performance.

## Decisão

Adotar a **Opção 2**:
1. Implementado o módulo `api/app/tailscale.py` com a função `verify_tailscale_peer_tag(client_ip, required_tag)`.
2. A consulta prioriza o socket UNIX da LocalAPI do Tailscale (`/var/run/tailscale/tailscaled.sock`) com fallback para o binário `tailscale whois`.
3. IPs de loopback (`127.0.0.1`, `localhost`, `testclient`) são liberados exclusivamente para execução de suítes de testes automatizados e desenvolvimento local.
4. Se o nó Tailscale reportado não possuir a tag requerida, a conexão WebSocket é encerrada com código `4003` e um `SecurityEvent` de severidade `CRITICAL` é gravado no banco de dados.

## Consequências

- **Positivas**: Nós que consigam entrar na rede Tailscale (ex: máquinas de convidados ou nós de outros projetos) são impedidos de se comunicar com a API ou se passar por agentes.
- **Negativas/Atenção**: Requer que as chaves de autorização (`auth-keys`) do Tailscale geradas para os hosts atribuam a tag `tag:bipdesk-host`.
