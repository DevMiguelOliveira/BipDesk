# Libera as portas do BipDesk no Firewall do Windows Defender
# Execute este script como Administrador

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Elevando privilegios para Administrador..." -ForegroundColor Yellow
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}

Write-Host "Configurando regras no Firewall do Windows..." -ForegroundColor Cyan

# Portas TCP (Sinalizacao, Relay e Caddy HTTPS)
netsh advfirewall firewall delete rule name="BipDesk Server TCP" | Out-Null
netsh advfirewall firewall add rule name="BipDesk Server TCP" dir=in action=allow protocol=TCP localport=80,443,21115,21116,21117

# Porta UDP (Perfuracao NAT / P2P)
netsh advfirewall firewall delete rule name="BipDesk Server UDP" | Out-Null
netsh advfirewall firewall add rule name="BipDesk Server UDP" dir=in action=allow protocol=UDP localport=21116

Write-Host "Regras do Firewall criadas com sucesso!" -ForegroundColor Green
Write-Host "Portas liberadas:"
Write-Host "  TCP: 80, 443, 21115, 21116, 21117"
Write-Host "  UDP: 21116"
Start-Sleep -Seconds 3
