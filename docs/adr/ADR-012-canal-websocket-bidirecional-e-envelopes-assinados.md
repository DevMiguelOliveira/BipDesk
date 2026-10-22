# ADR-012: Canal WebSocket Bidirecional Exclusivamente de Saída e Envelopes Canônicos Assinados

- **Status:** Aprovado
- **Data:** 2026-10-21
- **Contexto:** 
  O modelo legado de polling periódico via HTTP Heartbeat (a cada 5-10 segundos) gerava latência perceptível no envio de comandos de suporte, consumia recursos desnecessários do servidor e dependia de tráfego intermitente. O sistema necessita de comunicação em tempo real, mantendo a regra de segurança de conexões exclusivamente de saída (outbound-only).

## Opções Consideradas

1. **Abrir porta de escuta TCP no Host do Cliente**:
   - *Prós*: Servidor conecta diretamente no host.
   - *Contras*: Risco gravíssimo de exposição de superfície de rede (A11), bloqueio por NATs e firewalls corporativos/residenciais sem redirecionamento de portas (port forwarding).
2. **Canal WebSocket Bidirecional de Saída (`wss://`) com Envelopes Canônicos Assinados e Anti-Replay**:
   - *Prós*: O agente host inicia a conexão de saída para o servidor central através do túnel seguro (`/api/ws/host`). Toda mensagem trafega dentro de um envelope canônico estrito (`type:machine_id:timestamp:nonce:canonical_json_payload`) assinado com Ed25519. Ambas as pontas validam a assinatura, rejeitam mensagens com timestamp defasado (> 120s), descartam nonces repetidos e limitam o tamanho máximo a 64 KB.
   - *Contras*: Requer reconexão resiliente com backoff exponencial e jitter no cliente Go.

## Decisão

Adotar a **Opção 2**:
1. O host estabelece conexão WebSocket de saída persistente em `/api/ws/host?machine_id=...`.
2. O formato canônico para verificação e assinatura é definido rigorosamente:
   ```
   type:machine_id:timestamp:nonce:canonical_json_payload
   ```
3. O payload JSON é ordenado por chaves alfabeticamente (`sort_keys=True`) e sem espaços nos separadores para garantir determinismo binário em Go e Python.
4. **Proteção Anti-Replay**: Cada nó mantém um cache em memória com TTL de 300 segundos para nonces já vistos. Tentativas de replay são imediatamente descartadas.
5. **Janela de Skew Temporal**: Mensagens com desvio temporal superior a +- 120 segundos são sumariamente recusadas.
6. **Limite de Tamanho**: Mensagens com tamanho superior a 64 KB (65.536 bytes) resultam em encerramento da conexão com código 4002 e registro de `SecurityEvent`.
7. O agente implementa reconexão automática com backoff exponencial (1s a 30s) e jitter pseudo-aleatório.

## Consequências

- **Positivas**: Resposta em milissegundos para comandos e ofertas de sessão; detecção em tempo real de queda de estações via ping/pong; imunidade completa a ataques de replay e tampering em trânsito.
- **Negativas/Atenção**: Servidores em escala exigem gerenciamento de conexões persistentes em memória (suportado com baixo consumo pelo FastAPI/Starlette).
