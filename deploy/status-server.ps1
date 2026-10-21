# Exibe o status e as informações vitais do BipDesk Server
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
Set-Location $PSScriptRoot

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "       BIPDESK SERVER - STATUS LOCAL      " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

docker compose ps

Write-Host "`n[1] CHAVE PUBLICA DO SERVIDOR (RS_PUB_KEY):" -ForegroundColor Cyan
try {
    $pubkey = docker exec bipdesk-api cat /server-keys/id_ed25519.pub
    Write-Host "$pubkey" -ForegroundColor Yellow
} catch {
    Write-Host "Chave ainda nao disponivel." -ForegroundColor Red
}

Write-Host "`n[2] DADOS DE REDE:" -ForegroundColor Cyan
try {
    $localIp = (ipconfig | Select-String "IPv4" | Select-String "192\.168\." | Select-Object -First 1).ToString().Split(":")[-1].Trim()
    Write-Host "IP Local do PC: $localIp (usar no Port Forwarding do roteador)" -ForegroundColor Green
} catch {
    Write-Host "Consulte seu IP local via 'ipconfig'." -ForegroundColor Yellow
}

try {
    $pubIp = (Invoke-RestMethod -Uri "https://api.ipify.org?format=json" -TimeoutSec 3).ip
    Write-Host "IP Publico da Internet: $pubIp" -ForegroundColor Green
} catch {
    Write-Host "Nao foi possivel consultar o IP publico." -ForegroundColor Yellow
}

Write-Host "`n[3] TESTE DE COMUNICACAO DA API:" -ForegroundColor Cyan
try {
    $health = Invoke-RestMethod -Uri "http://localhost:8080/healthz" -TimeoutSec 3
    Write-Host "API Status: OK (Versao $($health.version))" -ForegroundColor Green
} catch {
    Write-Host "API offline ou nao acessivel na porta 8080." -ForegroundColor Red
}
