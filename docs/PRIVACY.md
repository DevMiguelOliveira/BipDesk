# Nota de Privacidade e Conformidade LGPD (Privacy Notice)

## 1. Princípio da Minimização de Dados (LGPD - Lei 13.709/2018)
O BipDesk é uma solução corporativa de suporte técnico e gerenciamento de ativos. Em estrita conformidade com o princípio da necessidade e minimização de dados, o agente cliente (`bipdesk-agent`) coleta **exclusivamente** os dados técnicos essenciais para a prestação de assistência remota e diagnóstico operacional de saúde da máquina:

### Dados Coletados no Heartbeat Periódico:
- **Identificador de Conexão (`rustdesk_id`)**: Código numérico gerado pelo motor de comunicação para rotear a conexão de suporte.
- **Nome do Computador (`hostname`)**: Para identificação da estação de trabalho no catálogo do técnico.
- **Usuário Conectado (`logged_in_user`)**: Nome de usuário da sessão Windows ativa no momento (sem senhas ou credenciais).
- **Endereço IP Local (`ip_address`)**: Endereço IPv4 na rede local para diagnóstico de conectividade.
- **Uso de Recursos de Hardware**:
  - Porcentagem de uso da CPU (métrica instantânea de carga).
  - Porcentagem de uso da memória RAM (para identificar travamentos).
  - Espaço livre no disco primário `C:` (em GB, para alertas de disco cheio).
  - Modelo do processador (`cpu_model`).

### O que o BipDesk NUNCA Coleta:
- ❌ Histórico de navegação na internet ou cookies de navegadores.
- ❌ Conteúdo de arquivos pessoais, documentos ou e-mails.
- ❌ Digitação do teclado (sem keyloggers).
- ❌ Áudio de microfone ou imagens de webcam sem consentimento em sessão.
- ❌ Senhas digitadas pelo usuário.

---

## 2. Transparência com o Usuário Final (Sem Modo Oculto)
O BipDesk opera sob política de transparência absoluta:
1. **Atalho e Identificação**: O software é instalado no diretório padrão do sistema (`C:\Program Files\BipDesk`) com atalho visível no Desktop.
2. **Notificação de Sessão Ativa**: Quando um técnico inicia uma sessão remota, o usuário é claramente informado através de notificação no desktop e ícone ativo na bandeja do sistema (System Tray).
3. **Registro de Auditoria**: Toda conexão de suporte é registrada no banco de dados com carimbo de data/hora, identificador do técnico responsável e duração do atendimento.

---

## 3. Conformidade com a Licença AGPL-3.0
O projeto BipDesk utiliza e modifica componentes derivados do RustDesk, distribuídos sob os termos da **GNU Affero General Public License v3.0 (AGPL-3.0)**.

- **Disponibilização do Código-Fonte**: Qualquer usuário ou organização que execute ou interaja com uma versão modificada do BipDesk através de uma rede de computadores tem o direito de receber o código-fonte correspondente completo da versão em execução.
- **Manutenção dos Avisos de Copyright**: Todos os cabeçalhos de licença originais, arquivos `LICENSE` e `NOTICE` devem ser rigorosamente preservados em todas as distribuições e compilações.
- **Modificações Documentadas**: As alterações e rebranding realizados sobre o upstream do RustDesk 1.4.9 estão documentados no diretório `client/patches/`.
