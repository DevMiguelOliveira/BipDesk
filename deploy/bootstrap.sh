#!/bin/bash
set -euo pipefail

echo "=========================================================="
echo "    BipDesk Server - Linux Host Bootstrap (ADR-003)       "
echo "=========================================================="

# 1. Configuração de Swap de 2GB (Vital para instâncias com 1GB a 2GB RAM)
if ! grep -q '/swapfile' /etc/fstab; then
    echo ">> Configurando Swap de 2GB..."
    fallocate -l 2G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=2048
    chmod 600 /swapfile
    mkswap /swapfile
    swapon /swapfile
    echo '/swapfile none swap sw 0 0' >> /etc/fstab
    sysctl vm.swappiness=20
    echo 'vm.swappiness=20' >> /etc/sysctl.d/99-bipdesk.conf
else
    echo ">> Swap já configurado."
fi

# 2. Ajustes de Kernel e Rede
echo ">> Aplicando ajustes de rede (sysctl)..."
cat << 'EOF' > /etc/sysctl.d/99-bipdesk-net.conf
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
net.ipv4.tcp_rmem = 4096 87380 16777216
net.ipv4.tcp_wmem = 4096 65536 16777216
net.core.somaxconn = 4096
EOF
sysctl -p /etc/sysctl.d/99-bipdesk-net.conf || true

# 3. Configuração de Regras de Firewall (iptables / ufw)
echo ">> Ajustando regras de firewall para serviços BipDesk..."
if command -v iptables &> /dev/null; then
    # Permite portas de sinalização, relay e Caddy HTTPS
    iptables -I INPUT 1 -p tcp --dport 80 -j ACCEPT || true
    iptables -I INPUT 1 -p tcp --dport 443 -j ACCEPT || true
    iptables -I INPUT 1 -p tcp --dport 21115 -j ACCEPT || true
    iptables -I INPUT 1 -p tcp --dport 21116 -j ACCEPT || true
    iptables -I INPUT 1 -p udp --dport 21116 -j ACCEPT || true
    iptables -I INPUT 1 -p tcp --dport 21117 -j ACCEPT || true

    # Salva regras se iptables-persistent ou netfilter-persistent existir
    if command -v netfilter-persistent &> /dev/null; then
        netfilter-persistent save || true
    fi
fi

# 4. Instalação do Docker (se não estiver instalado)
if ! command -v docker &> /dev/null; then
    echo ">> Instalando Docker Engine..."
    curl -fsSL https://get.docker.com -o get-docker.sh
    sh get-docker.sh
    rm -f get-docker.sh
    systemctl enable docker
    systemctl start docker
fi

# 5. Criação de Pastas de Dados
echo ">> Criando diretórios persistentes do BipDesk..."
mkdir -p /var/lib/bipdesk-server
mkdir -p /var/lib/bipdesk-api/data
mkdir -p /var/lib/caddy/data
mkdir -p /var/lib/caddy/config
mkdir -p /var/www/bipdesk-downloads

# Permissões seguras
chmod 700 /var/lib/bipdesk-server
chmod 700 /var/lib/bipdesk-api

if [ -f "$(dirname "$0")/icon.ico" ]; then
    cp "$(dirname "$0")/icon.ico" /var/www/bipdesk-downloads/icon.ico
    chmod 644 /var/www/bipdesk-downloads/icon.ico
fi

echo "=========================================================="
echo ">> Bootstrap concluído com sucesso!"
echo ">> Proximo passo: configurar o arquivo .env com seu DOMAIN"
echo ">> e executar: docker compose up -d"
echo "=========================================================="
