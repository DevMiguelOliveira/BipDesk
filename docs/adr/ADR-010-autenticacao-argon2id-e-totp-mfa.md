# ADR-010: Autenticação de Operadores com Argon2id e MFA TOTP (RFC 6238)

- **Status:** Aprovado
- **Data:** 2026-10-21
- **Contexto:** 
  A autenticação de técnicos e administradores na API anteriormente utilizava hashing bcrypt e permitia login por senha única. Em conformidade com os requisitos de auditoria e endurecimento corporativo, credenciais de operadores exigem proteção robusta contra ataques de GPU/ASIC e suporte nativo a segundo fator de autenticação (MFA).

## Opções Consideradas

1. **Manter bcrypt sem MFA**:
   - *Prós*: Sem mudanças para operadores.
   - *Contras*: Suscetível a ataques de dicionário offline em caso de vazamento de dump do banco e ausência de barreira adicional contra roubo de credenciais.
2. **Argon2id + TOTP RFC 6238 + Tokens de Acesso Curto com Refresh Token**:
   - *Prós*: Argon2id é o padrão de ouro (vencedor da Password Hashing Competition), resistente a ataques em GPU e side-channel. O TOTP adiciona autenticação multifator compatível com Google Authenticator, Bitwarden, etc. Tokens de acesso expiram em 15 minutos e refresh tokens permitem renovação segura.
   - *Contras*: Requer bibliotecas adicionais (`argon2-cffi` e `pyotp`).

## Decisão

Adotar a **Opção 2**:
1. O algoritmo padrão de derivação de senhas passa a ser **Argon2id** (time_cost=3, memory_cost=65536, parallelism=4, hash_len=32).
2. Fallback transparente de leitura é mantido para senhas legadas em bcrypt durante a migração.
3. Implementadas rotas de configuração e validação de TOTP:
   - `/api/totp/setup`: Gera segredo Base32 e URI para QR Code.
   - `/api/totp/verify`: Valida código de 6 dígitos com tolerância de janela de +- 30 segundos.
4. Tokens de acesso JWT têm expiração estrita de 15 minutos, complementados por `refresh_token` de alta entropia (48 bytes urlsafe).

## Consequências

- **Positivas**: Resistência criptográfica de ponta para senhas de operadores e proteção efetiva de segundo fator contra credenciais vazadas.
- **Negativas/Atenção**: Operadores com TOTP ativo devem fornecer o código `totp_code` no payload de autenticação.
