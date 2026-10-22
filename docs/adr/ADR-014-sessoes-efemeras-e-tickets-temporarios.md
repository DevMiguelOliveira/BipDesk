# ADR-014: Sessões Efêmeras com Tickets Temporários Assinados (Zero Senha Fixa de Host)

- **Status:** Aprovado
- **Data:** 2026-10-21
- **Contexto:** 
  Anteriormente, cada máquina host mantinha uma senha de acesso estática gerada na instalação, armazenada no banco com cifra simétrica (MultiFernet). Embora criptografada, a existência de uma senha estática permitia que técnicos com acesso à chave se conectassem a qualquer momento sem consentimento ou geração de credenciais novas. A meta corporativa é erradicar senhas fixas de acesso autônomo (A3, ADR-006).

## Opções Consideradas

1. **Manter Senhas Estáticas com Rotação Semanal**:
   - *Prós*: Sem mudanças no protocolo RustDesk de conexão direta.
   - *Contras*: Janela de exposição onde uma credencial compartilhada permanece válida para múltiplos acessos não auditados.
2. **Sessões Efêmeras com SessionTickets de Uso Único e Validade de 60 Segundos**:
   - *Prós*: Nenhuma máquina mantém senha estática. Quando o técnico clica em "Conectar" no console:
     1. A API gera um ticket temporário (`SessionTicket`) assinado com Ed25519 e uma senha de alta entropia de uso único (16 bytes urlsafe).
     2. A oferta de sessão é enviada pelo túnel assinado WebSocket para o host.
     3. O host aplica a credencial efêmera no engine RustDesk e exibe indicador visual de suporte ativo com o nome do técnico.
     4. A credencial expira em 60 segundos se não utilizada e é imediatamente revogada (`revokeSessionCredentials`) ao término da sessão (`/api/sessions/{id}/terminate`).
   - *Contras*: Requer coordenação síncrona entre o console do técnico e o serviço host.

## Decisão

Adotar a **Opção 2**:
1. O endpoint `/api/sessions/request` torna-se a única fonte geradora de credenciais temporárias para estações online.
2. Toda sessão gera um registro em `SessionTicket` e eventos de auditoria em `ConnectionAudit` e `SecurityEvent`.
3. O host cliente recusa qualquer ticket direcionado a outro `machine_id` ou com assinatura divergente da chave do servidor.
4. O término de sessão é acionado explicitamente pelo técnico ou detectado via encerramento do processo do cliente, enviando `session_terminate` para purgar a senha efêmera no host.

## Consequências

- **Positivas**: Senhas de máquinas tornam-se 100% efêmeras e descartáveis; invasores que acessem o banco de dados da API não encontram nenhuma credencial utilizável para invadir computadores do parque.
- **Negativas/Atenção**: Conexões diretas fora do console BipDesk são bloqueadas, pois a máquina não aceita senhas antigas.
