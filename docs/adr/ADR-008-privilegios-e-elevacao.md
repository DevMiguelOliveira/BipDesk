# ADR-008: Política de Menor Privilégio, Elevação UAC e Transparência Total de Sessão

- **Status:** Aprovado pelo Proprietário
- **Data:** 2026-10-20
- **Contexto:** 
  O instalador do agente continha rotina de auto-elevação UAC invocando PowerShell com `-Verb RunAs` de forma implícita e passando argumentos concatenados em strings (A9). Adicionalmente, havia rotinas legadas de limpeza silenciosa (`applyStealthWhiteLabel`) que tentavam ocultar rastros do software, o que viola o princípio de transparência com o usuário final e a restrição inviolável R5.

## Opções Consideradas

1. **Auto-Elevação Silenciosa e Ocultação na Estação**:
   - *Prós*: Sem cliques extras para o instalador.
   - *Contras*: Comportamento idêntico a trojans; violação das diretrizes de segurança da Microsoft, LGPD e restrições R5 e R8 do BipDesk.
2. **Totalmente Transparente (Aprovada pelo Proprietário)**:
   - *Prós*:
     - **Manifesto de Aplicação Explícito**: O executável declara elevação formal ou o instalador solicita permissão de administrador via prompt UAC nativo do Windows com assinatura e identificação do editor.
     - **Indicação Visual Inequívoca Durante a Sessão**: Quando um técnico se conecta, o usuário final recebe notificação visual na área de trabalho e um ícone na barra de tarefas/bandeja do sistema indicando que uma sessão de suporte está em andamento.
     - **Sem Acesso Silencioso**: Toda intervenção é visível e gera log de auditoria no servidor e no log local de eventos do Windows.
     - Conformidade total com o Art. 6º da LGPD (Transparência e Finalidade Legítima).
   - *Contras*: Usuário final tem plena visibilidade da atividade de suporte (o que é uma exigência ética e de compliance corporativo).

## Decisão Aprovada

Adotar a **Opção 2 (Totalmente Transparente)**:
1. Remover a chamada de auto-elevação oculta via PowerShell `Start-Process -Verb RunAs` do `agent/main.go`.
2. Remover métodos de ocultação/stealth (`applyStealthWhiteLabel`).
3. Manter aviso e ícone na bandeja do sistema informando quando o BipDesk Host estiver executando e quando um técnico estiver conectado ativamente.

## Consequências

- **Positivas**: Conformidade jurídica integral com a LGPD; ausência de falsos-positivos em soluções de antivírus e EDR; eliminação de técnicas de evasão.
- **Negativas/Atenção**: Operações de suporte remoto exigem ciência do colaborador/cliente, reforçando o uso ético da ferramenta.
