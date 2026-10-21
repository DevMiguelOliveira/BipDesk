# ADR-009: Escala do Console do Técnico, Paginação e Otimização de Polling

- **Status:** Aprovado
- **Data:** 2026-10-20
- **Contexto:** 
  O console do operador técnico (`tech-app`) realiza polling periódico a cada 5 segundos na rota `/api/machines`. À medida que o parque de computadores cresce para mais de 1.000 máquinas gerenciadas por múltiplos técnicos simultâneos, trafegar o JSON completo com todos os metadados a cada 5 segundos pode sobrecarregar a largura de banda e aumentar a latência da API SQLite.

## Opções Consideradas

1. **WebSockets Bidirecionais de Alta Frequência para Todo o Catálogo**:
   - *Prós*: Atualizações em tempo real instantâneas.
   - *Contras*: Alto consumo de memória e conexões abertas no servidor para centenas de operadores técnicos; complexidade de reconexão e buffers em C# e FastAPI.
2. **Polling Inteligente com Suporte a Paginação / Filtro por Grupo e Intervalo Configurável**:
   - *Prós*:
     - Manter a robustez do HTTP REST, adicionando suporte aos parâmetros `skip`, `limit`, `group` e `since` (timestamp de modificação) em `/api/machines`.
     - O console pode atualizar apenas as máquinas visíveis na página/grupo atual ou realizar requisições delta (`since=<last_poll_utc>`).
     - Tornar o intervalo de polling configurável no console (ex: 5s em foco, 15s em segundo plano, ou pausado durante sessões ativas).
     - Configurar `PRAGMA busy_timeout = 5000` no SQLite para evitar erros de bloqueio de escrita sob concorrência de múltiplos operadores e heartbeats simultâneos.
   - *Contras*: Requer implementação de parâmetros de paginação e delta na rota de listagem da API.

## Decisão

Adotar a **Opção 2**:
1. Implementar paginação opcional (`skip`, `limit`) e filtro temporal em `/api/machines`.
2. Configurar `PRAGMA busy_timeout = 5000` e verificar integridade contínua do modo WAL no SQLite.
3. Permitir no console técnico o ajuste do intervalo de polling e a atualização inteligente apenas de máquinas alteradas.

## Consequências

- **Positivas**: A API suporta mais de 1.000 estações de trabalho e dezenas de técnicos simultâneos sem gargalos de banco de dados ou consumo desnecessário de CPU/rede.
- **Negativas/Atenção**: O frontend MVVM deve sincronizar deltas de máquinas sem resetar o scroll da lista.
