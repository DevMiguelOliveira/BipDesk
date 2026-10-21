# Script para atualizar o DuckDNS automaticamente com o IP do seu PC
param(
    [string]$Token = ""
)

if (-not $Token) {
    Write-Host "Por favor, informe seu token do DuckDNS:" -ForegroundColor Yellow
    Write-Host "Exemplo: .\atualizar-duckdns.ps1 -Token 'seu-token-aqui'" -ForegroundColor Cyan
    exit
}

$url = "https://www.duckdns.org/update?domains=bipdesk&token=$Token&ip="
try {
    $res = (Invoke-RestMethod -Uri $url).Trim()
    if ($res -eq "OK") {
        Write-Host "DuckDNS atualizado com sucesso para bipdesk.duckdns.org!" -ForegroundColor Green
    } else {
        Write-Host "Resposta do DuckDNS: $res (verifique se o token esta correto)" -ForegroundColor Red
    }
} catch {
    Write-Host "Erro ao conectar com DuckDNS: $_" -ForegroundColor Red
}
