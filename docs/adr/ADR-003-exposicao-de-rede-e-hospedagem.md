# ADR-003: Exposição de Rede e Estratégia de Hospedagem (Rede Mesh Privada)

- **Status:** Aprovado pelo Proprietário
- **Data:** 2026-10-20
- **Contexto:** 
  A infraestrutura anterior baseada em nuvem pública (Oracle Cloud Always Free) foi descontinuada. O ambiente atual de execução no computador particular do desenvolvedor sofria com restrições severas de rede residencial (bloqueio de portas 80/443 de entrada por operadoras de telecomunicação brasileiras, ausência de IP fixo e complexidade de NAT/CGNAT).
  
  Adicionalmente, a verificação no código-fonte do motor RustDesk 1.4.9 upstream (`libs/common/src/config.rs`) evidenciou que:
  - O cliente deriva as portas de teste de NAT ($P-1$) e de relay ($P+1$) diretamente a partir da porta de registro ($P$, padrão `21116`).
  - O handshake STUN e perfuração de NAT exigem **transporte UDP** na porta 21116.
  - Túneis HTTP convencionais (como Cloudflare Tunnel gratuito) não atendem aos requisitos de pacotes UDP raw do RustDesk.

## Opções Consideradas

1. **VPS Linux Dedicada com IP Público Fixo**:
   - *Prós*: Portas abertas nativamente na internet pública.
   - *Contras*: Custo mensal recorrente em moeda estrangeira.
2. **PC Particular com Port Forwarding no Roteador + DuckDNS**:
   - *Prós*: Sem custo direto de nuvem.
   - *Contras*: Portas 80/443 bloqueadas pelas operadoras de banda larga; exposição da rede doméstica pessoal a escaneamentos e ataques de força bruta.
3. **Rede Mesh Privada (Tailscale / Headscale / WireGuard)**:
   - *Prós*:
     - **Superfície de ataque pública reduzida a ZERO**: Nenhum container ou porta (8080, 21115, 21116 UDP/TCP, 21117) é exposto para a internet aberta.
     - **Imunidade a bloqueios de ISPs residenciais**: A comunicação do Tailscale utiliza conexões de saída autenticadas (WireGuard point-to-point via nós DERP).
     - **Suporte nativo a UDP e todas as portas do RustDesk**: O túnel WireGuard do Tailscale transporta UDP e TCP de ponta a ponta sem qualquer alteração na derivação de portas do RustDesk.
     - **Certificados TLS automáticos**: Tailscale provê certificados HTTPS válidos automaticamente via Let's Encrypt para os nós da tailnet (`*.ts.net`).
   - *Contras*: Exige que as máquinas clientes e a estação do técnico façam parte da mesma Tailnet / rede privada Headscale antes de estabelecer o acesso.

## Decisão Aprovada

Adotar a **Opção 3 (Rede Mesh Privada - Tailscale / Headscale)**:
1. O servidor central BipDesk operará conectado à rede Mesh privada.
2. O compose e os serviços vincularão os acessos às interfaces da rede privada mesh, eliminando a dependência de abertura de portas perigosas no roteador doméstico.
3. As configurações de DNS e endereço do servidor utilizarão os nomes internos da malha privada ou IPs do Tailscale (ex: `100.x.y.z` ou `bipdesk.internal`).
4. Scripts de liberação de firewall local e compose serão ajustados para isolamento e comunicação interna.

## Consequências

- **Positivas**: Segurança máxima contra ataques externos; tráfego 100% criptografado por túnel WireGuard em nível de rede antes mesmo de chegar à camada de aplicação; sem custo de provedores em nuvem.
- **Negativas/Atenção**: Cada máquina gerenciada deve possuir o cliente Tailscale/Headscale provisionado ou autenticado via chave de pré-autenticação (*auth key*) durante o processo de onboarding corporativo.
