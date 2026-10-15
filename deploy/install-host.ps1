<#
.SYNOPSIS
    BipDesk Host - Instalador Silencioso Não Assistido (Estilo TeamViewer Host)
    Execução: irm https://seu-dominio.duckdns.org/install/TOKEN | iex
#>

param(
    [string]$ServerUrl = "https://bipdesk.duckdns.org",
    [string]$EnrollToken = ""
)

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "         BipDesk Host - Instalação de Suporte Remoto       " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Verificar privilégios de Administrador
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "Permissões de administrador necessárias. Solicitando elevação..."
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -Command `"& { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; (New-Object Net.WebClient).DownloadString('$ServerUrl/install/$EnrollToken') | iex }`""
    Exit
}

$InstallDir = "C:\Program Files\BipDesk"
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

$SessionExe = "$InstallDir\BipDesk_Session.exe"
$AgentExe = "$InstallDir\bipdesk-agent.exe"

# 2. Download dos Binários
Write-Host "[1/5] Baixando binários do BipDesk Host..." -ForegroundColor Green
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$WebClient = New-Object System.Net.WebClient

try {
    $WebClient.DownloadFile("$ServerUrl/downloads/BipDesk_Session.exe", $SessionExe)
    $WebClient.DownloadFile("$ServerUrl/downloads/bipdesk-agent.exe", $AgentExe)
}
catch {
    Write-Error "Falha ao baixar arquivos do servidor: $_"
    Exit 1
}

# 3. Gerar Senha Segura Não Assistida (24 caracteres aleatórios)
Write-Host "[2/5] Gerando credenciais seguras de acesso..." -ForegroundColor Green
Add-Type -AssemblyName System.Web
$Password = [System.Web.Security.Membership]::GeneratePassword(24, 6)

# 4. Instalar e Configurar o Serviço do Windows
Write-Host "[3/5] Registrando serviço de sistema BipDesk..." -ForegroundColor Green
& $SessionExe --install-service | Out-Null
Start-Sleep -Seconds 2

# Configurar Servidor BipDesk Cloud e Chave Criptográfica
& $SessionExe --server "bipdesk.duckdns.org" --key "gsTzsIewn3wB9umlEvZgT+Z9ZUKNxcAwbhl+bz2pJFw=" | Out-Null
Start-Sleep -Seconds 1

# Definir a senha permanente silenciosamente
& $SessionExe --password $Password | Out-Null

# 5. Obter ID do BipDesk
Write-Host "[4/5] Identificando ID da máquina..." -ForegroundColor Green
$MachineId = ""
for ($i = 0; $i -lt 5; $i++) {
    $MachineId = (& $SessionExe --get-id)
    if ($MachineId -and $MachineId.Trim().Length -ge 6) {
        $MachineId = $MachineId.Trim()
        break
    }
    Start-Sleep -Seconds 2
}

if (-not $MachineId) {
    # Fallback: tentar ler do arquivo de configuração
    $ConfigPath = "$env:APPDATA\BipDesk\config\BipDesk.toml"
    if (Test-Path $ConfigPath) {
        $Match = Select-String -Path $ConfigPath -Pattern 'id = "([^"]+)"'
        if ($Match) { $MachineId = $Match.Matches[0].Groups[1].Value }
    }
}

# 6. Registrar Máquina na API (Enroll)
Write-Host "[5/5] Registrando no painel BipDesk Admin..." -ForegroundColor Green
$ComputerName = $env:COMPUTERNAME
$Username = [System.Environment]::UserName
$OS = (Get-CimInstance Win32_OperatingSystem).Caption

$Payload = @{
    token = $EnrollToken
    rustdesk_id = $MachineId
    hostname = $ComputerName
    username = $Username
    os_version = $OS
    password = $Password
} | ConvertTo-Json

try {
    $EnrollUrl = "$ServerUrl/api/agent/enroll"
    $Response = Invoke-RestMethod -Uri $EnrollUrl -Method Post -Body $Payload -ContentType "application/json"
    Write-Host ">> Sucesso! Máquina registrada como ID: $MachineId" -ForegroundColor Cyan
}
catch {
    Write-Warning "Falha ao registrar máquina na API: $_"
}

# 7. Iniciar Agent de Heartbeat
Write-Host ">> Inicializando BipDesk Agent em segundo plano..." -ForegroundColor Green
Start-Process -FilePath $AgentExe -ArgumentList "--server $ServerUrl --id $MachineId" -WindowStyle Hidden

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   BipDesk Host instalado e operacional com sucesso!       " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
