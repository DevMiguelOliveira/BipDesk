# ADR-005: Política Estrita de TLS e Verificação de Certificados

- **Status:** Aprovado
- **Data:** 2026-10-20
- **Contexto:** 
  A auditoria apontou desativação de validação de certificados SSL/TLS (`InsecureSkipVerify: true` no relatório original, e no código atual em `ApiClient.cs:27` onde `ServerCertificateCustomValidationCallback = (...) => true`), violando gravemente a restrição inviolável R4 e permitindo ataques de interceptação MITM.

## Opções Consideradas

1. **Permitir Certificados Autoassinados com InsecureSkipVerify Opcional**:
   - *Prós*: Facilidade inicial para desenvolvedores.
   - *Contras*: Risco inaceitável em produção. Permite que qualquer intermediário na rede roube tokens JWT, senhas de máquinas e injete comandos.
2. **TLS 1.2+ Obrigatório com Validação da Cadeia de Certificados do Sistema Operacional (Root CA)**:
   - *Prós*: Cumpre a restrição R4. Caddy emite certificados válidos publicamente reconhecidos (Let's Encrypt / ZeroSSL) em ambientes com domínio e portas 80/443, ou utiliza ACME com DNS challenge. O cliente Windows valida nativamente usando a Root Store do Windows (CryptoAPI).
   - *Contras*: Requer que o servidor possua certificado TLS válido e confiável.

## Decisão

Adotar a **Opção 2**:
1. Remover incondicionalmente qualquer callback de bypass TLS (`ServerCertificateCustomValidationCallback = (...) => true` em C# e `InsecureSkipVerify: true` em Go).
2. Configurar a versão mínima do protocolo no agente Go como `tls.VersionTLS12` e conjuntos de cifras modernos (AEAD: AES-GCM / ChaCha20-Poly1305).
3. O console do técnico C# e o agente Go devem validar os certificados contra a raiz de certificados confiáveis do Windows.
4. Para testes locais ou redes isoladas, deve-se instalar a CA raiz corporativa no repositório de certificados do Windows (`certmgr.msc` -> Trusted Root Certification Authorities), nunca desativando a verificação no código-fonte.

## Consequências

- **Positivas**: Interceptação de tráfego e falsificação de endpoints tornam-se criptograficamente impraticáveis. Comunicação do técnico e do agente operam no mesmo padrão bancário/militar.
- **Negativas/Atenção**: Se o servidor central estiver usando um certificado com nome incorreto ou vencido, clientes e console não conectarão e emitirão erro explícito de TLS, exigindo que o administrador mantenha a emissão de certificados saudável.
