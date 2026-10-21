# Para os serviços do BipDesk
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","Machine") + ";" + [System.Environment]::GetEnvironmentVariable("Path","User")
Set-Location $PSScriptRoot

Write-Host "Parando servicos do BipDesk..." -ForegroundColor Yellow
docker compose down
Write-Host "Servicos finalizados com sucesso." -ForegroundColor Green
