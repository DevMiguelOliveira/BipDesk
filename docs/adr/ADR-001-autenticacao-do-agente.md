# ADR-001: Autenticação Criptográfica do Agente Cliente (Host)

- **Status:** Aprovado
- **Data:** 2026-10-20
- **Contexto:** 
  Anteriormente, o endpoint `/api/agent/heartbeat` não exigia autenticação. Qualquer requisição HTTP podia enviar um `rustdesk_id` forjado, atualizar dados de presença, registrar máquinas automaticamente com senha padrão conhecida (A5) ou sequestrar comandos remotos enfileirados para a estação legítima (A6).

## Opções Consideradas

1. **Autenticação por IP de Origem**:
   - *Prós*: Sem alteração no protocolo do agente.
   - *Contras*: Inviável em ambientes corporativos com múltiplos clientes sob o mesmo CGNAT/NAT de saída ou IPs dinâmicos; facilmente spoofável.
2. **Certificados Mútuos de Cliente (mTLS)**:
   - *Prós*: Criptograficamente muito seguro.
   - *Contras*: Complexidade extrema de emissão, renovação e distribuição de certificados x509 para milhares de computadores de usuários comuns.
3. **Token Criptográfico Único por Máquina (Machine Token + DPAPI no Windows)**:
   - *Prós*: Emitido pelo servidor durante o processo de *Enrollment* inicial sob um token de instalação de uso único. Armazenado no cliente protegido pela Windows DPAPI (`CryptProtectData`) no registro/arquivo. No servidor, a validação é feita via comparação de tempo constante (`hmac.compare_digest`). Suporta revogação imediata no painel.

## Decisão

Adotar a **Opção 3**:
1. A rota `/api/agent/heartbeat` passará a exigir o cabeçalho `X-Machine-Token`.
2. O token é um segredo de alta entropia (256 bits gerados via `secrets.token_urlsafe(32)`), gerado no enrollment legítimo (`/api/agent/enroll`).
3. O servidor armazena o hash SHA-256 do token no banco de dados (evitando armazenamento em texto claro) e valida com `hmac.compare_digest`.
4. Heartbeats sem token ou com token inválido retornarão imediatamente `401 Unauthorized` ou `403 Forbidden`.
5. O auto-registro silencioso no heartbeat é permanentemente desativado.

## Consequências

- **Positivas**: Elimina a criação de máquinas fantasmas, impede a interceptação de comandos de outras estações e garante rastreabilidade estrita da estação reportada.
- **Negativas/Atenção**: Agentes legados já instalados sem token precisarão ser re-vinculados (re-enrollment) para voltar a reportar presença ao servidor.
