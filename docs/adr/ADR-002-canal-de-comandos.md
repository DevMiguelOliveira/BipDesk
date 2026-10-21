# ADR-002: Canal de Comandos Remotos Pré-Definidos e Assinados

- **Status:** Aprovado
- **Data:** 2026-10-20
- **Contexto:** 
  O agente do cliente roda com privilégios de `SYSTEM` no Windows para tarefas administrativas. Qualquer canal que permita execução de comandos sem validação de integridade ou com tipos arbitrários (`cmd`) constitui uma vulnerabilidade de RCE (Execução Remota de Código) crítica para o parque inteiro de máquinas (A1).

## Opções Consideradas

1. **Permitir Shell Remoto Arbitrário com Autenticação de Operador**:
   - *Prós*: Flexibilidade para suporte.
   - *Contras*: Risco inaceitável (R3). Um comprometimento da API ou conta de técnico permitiria instalar malwares/ransomwares em todas as máquinas gerenciadas como SYSTEM.
2. **Enum Fechado com Assinatura Criptográfica, Nonce e Janela de Expiração**:
   - *Prós*: Conformidade estrita com a restrição R3. Comandos limitados a um catálogo estrito (`cad`, `restart_spooler`, `flush_dns`, `lock`, `reboot`). O servidor assina o comando (`action:machine_id:nonce:timestamp`) usando HMAC-SHA256 derivado da chave da máquina. O agente valida a assinatura, rejeita timestamps defasados (>60s) e nonces já executados.
   - *Contras*: Requer implementação de controle de nonces em memória/disco no agente Go.

## Decisão

Adotar a **Opção 2**:
1. O conjunto de comandos é estritamente limitado ao enum:
   - `cad`: Sinal seguro Ctrl+Alt+Del (SAS).
   - `restart_spooler`: Reinicialização do serviço de spooler de impressão.
   - `flush_dns`: Limpeza de cache DNS do resolvedor.
   - `lock`: Bloqueio imediato da estação de trabalho (`LockWorkStation`).
   - `reboot`: Reinício controlado do Windows.
2. Qualquer comando fora desse conjunto será sumariamente rejeitado pelo backend e pelo agente.
3. Não haverá comandos `cmd` ou execução de shell arbitrário.
4. Toda execução gera registro local no log de eventos/arquivo de auditoria do agente e no banco da API.

## Consequências

- **Positivas**: RCE arbitrário torna-se impossível mesmo que um invasor comprometa o banco de dados da API. Ataques de replay de comandos antigos são neutralizados pela verificação de nonce e validade de 60 segundos.
- **Negativas/Atenção**: Operadores não poderão rodar scripts ad-hoc no cliente através deste canal; manutenções avançadas continuam exigindo sessão de desktop interativo visível com o usuário.
