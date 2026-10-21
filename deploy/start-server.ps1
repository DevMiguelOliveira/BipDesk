# Inicia os serviços do BipDesk no Docker Desktop
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
Set-Location $PSScriptRoot

Write-Host "Iniciando BipDesk Server no Docker Desktop..." -ForegroundColor Cyan
docker compose up -d

Write-Host "`nServicos iniciados!" -ForegroundColor Green
docker compose ps
