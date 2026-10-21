# ADR-004: Gestão de Segredos, Armazenamento Externo e Rotação com MultiFernet

- **Status:** Aprovado
- **Data:** 2026-10-20
- **Contexto:** 
  Valores padrão como `SECRET_KEY`, `ADMIN_PASSWORD` e chaves Fernet estavam presentes no código ou commitados no repositório (`deploy/.env`, `api/app/security.py`). Além disso, o sistema utilizava uma chave Fernet estática única sem identificação de versão, inviabilizando a rotação periódica de chaves de criptografia das senhas de máquinas sem quebrar o acesso autônomo (A2, A3, A10).

## Opções Consideradas

1. **HashiCorp Vault ou AWS KMS**:
   - *Prós*: Padrão da indústria para enterprise.
   - *Contras*: Complexidade e consumo de recursos excessivos para uma infraestrutura self-hosted compacta que deve rodar com menos de 1 GB de RAM.
2. **Variáveis de Ambiente / Arquivo de Segredos com Falha Rápida (Fail-Fast) + MultiFernet Versionado**:
   - *Prós*: 
     - A API não inicia se `SECRET_KEY` ou as chaves Fernet estiverem ausentes (fora do modo explicitamente declarado `ENV=development`).
     - A cifra das senhas de computadores passa a usar `cryptography.fernet.MultiFernet`, mantendo uma lista de chaves válidas. A primeira chave da lista é usada para encriptar novos dados; chaves secundárias são mantidas para descriptografar dados históricos e re-encriptar sob a chave nova na próxima leitura (rotação transparente).
     - Segredos são excluídos do controle de versão via `.gitignore` e armazenados em volume Docker seguro (`/data/secrets`) com permissões `0600`.
   - *Contras*: Requer implementação cuidadosa do ciclo de vida de rotação no backend.

## Decisão

Adotar a **Opção 2**:
1. O backend recusará a inicialização se `SECRET_KEY` não for informada via variável de ambiente ou arquivo `/data/secrets/secret_key.txt`.
2. Criação do arquivo de modelo `.env.example` sem valores reais e inclusão de `.env`, `.env.local` e `*.key` no `.gitignore`.
3. Implementação de `MultiFernet` no módulo `api/app/security.py` suportando rotação de chaves.
4. Implementação de suíte de testes em `pytest` cobrindo o processo de rotação e re-encriptação de credenciais.

## Consequências

- **Positivas**: Elimina 100% de segredos estáticos no código. Permite aos administradores rotacionar chaves de segurança anualmente sem corromper as credenciais de mais de 1.000 máquinas clientes.
- **Negativas/Atenção**: O volume de dados `/data/secrets` deve ser incluído obrigatoriamente na rotina de backup do servidor.
