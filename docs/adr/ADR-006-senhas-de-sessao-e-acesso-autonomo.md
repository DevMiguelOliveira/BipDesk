# ADR-006: Senhas de Sessão, Acesso Autônomo e Proteção de Linha de Comando

- **Status:** Aprovado
- **Data:** 2026-10-20
- **Contexto:** 
  Havia discrepância entre documentos: a especificação citava passagem de senha via `--password` na linha de comando (`Process.Start`), enquanto o README falava em senha aleatória de 24 caracteres e ticket de 60 segundos. Passar credenciais pela linha de comando expõe a senha em texto claro a qualquer processo ou usuário local sem privilégios via `tasklist /v`, `wmic`, `Get-Process` ou Process Explorer.

## Opções Consideradas

1. **Passagem de Senha por Argumentos de Linha de Comando (`--password <pwd>`)**:
   - *Prós*: Fácil implementação.
   - *Contras*: Risco crítico de segurança local. A senha fica visível na tabela de processos do Windows até o processo finalizar.
2. **Senha Aleatória por Máquina Injetada via Configuração de IPC / Arquivo Temporário Protegido**:
   - *Prós*: 
     - No cadastro (*Enrollment*), o agente gera uma senha aleatória de alta entropia (24 caracteres alfanuméricos com símbolos).
     - Essa senha é enviada cifrada à API via HTTPS e armazenada com Fernet no banco.
     - Durante o lançamento da sessão pelo técnico, a senha obtida via `/api/machines/{id}/connect` é injetada de maneira segura via pipe de automação, arquivo temporário com ACL restrita ou variável de ambiente efêmera limpa imediatamente após o handshake, nunca figurando na linha de comando exposta de `ProcessStartInfo.Arguments`.
     - O ticket de conexão (`expires_in_seconds: 60`) garante que uma autorização concedida pela API expire caso o técnico não inicie a sessão imediatamente.
   - *Contras*: Requer ajuste no `SessionLauncher.cs` do console WPF para não expor a flag `--password` no executável filho.

## Decisão

Adotar a **Opção 2**:
1. Padronizar o modelo: Senha única e aleatória gerada localmente por máquina cliente durante o enrollment.
2. O endpoint `/api/machines/{id}/connect` valida o ticket e fornece a credencial sob sessão do técnico autenticado com validade de 60 segundos.
3. Eliminar a passagem de `--password <pwd>` em texto claro nos argumentos de processo do `SessionLauncher.cs` e `main.go`.

## Consequências

- **Positivas**: Usuários locais sem privilégios ou malwares operando como usuário comum não conseguem capturar a senha de acesso da estação inspecionando a linha de comando do processo.
- **Negativas/Atenção**: O console WPF gerencia a injeção da credencial no motor RustDesk de forma encapsulada.
