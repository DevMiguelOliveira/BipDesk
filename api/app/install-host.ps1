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
    try { $WebClient.DownloadFile("$ServerUrl/downloads/icon.ico", "$InstallDir\icon.ico") } catch {}
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
    $RustDeskInstalled = "C:\Program Files\RustDesk\RustDesk.exe"
    if (Test-Path $RustDeskInstalled) {
        $MachineId = (& $RustDeskInstalled --get-id)
        if ($MachineId) { $MachineId = $MachineId.Trim() }
    }
}

if (-not $MachineId) {
    # Fallback: tentar ler do arquivo de configuração do RustDesk ou BipDesk
    $ConfigPaths = @(
        "$env:APPDATA\RustDesk\config\RustDesk.toml",
        "C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk.toml",
        "$env:ProgramData\RustDesk\config\RustDesk.toml",
        "$env:APPDATA\BipDesk\config\BipDesk.toml",
        "C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\BipDesk\config\BipDesk.toml",
        "$env:ProgramData\BipDesk\config\BipDesk.toml"
    )
    foreach ($cfg in $ConfigPaths) {
        if (Test-Path $cfg) {
            $Match = Select-String -Path $cfg -Pattern 'id = "([^"]+)"'
            if ($Match) { 
                $MachineId = $Match.Matches[0].Groups[1].Value
                break
            }
        }
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

# 7. Configurar Agent para Iniciar Automaticamente com o Windows (24/7)
Write-Host ">> Registrando BipDesk Agent para inicialização automática no boot..." -ForegroundColor Green
$TaskName = "BipDeskHostAgent"

try {
    # Cria tarefa agendada no boot com privilégios SYSTEM
    $Action = New-ScheduledTaskAction -Execute $AgentExe -Argument "--server `"$ServerUrl`" --id `"$MachineId`""
    $Trigger = New-ScheduledTaskTrigger -AtStartup
    $Principal = New-ScheduledTaskPrincipal -UserId "NT AUTHORITY\SYSTEM" -LogonType ServiceAccount -RunLevel Highest
    $Settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Register-ScheduledTask -TaskName $TaskName -Action $Action -Trigger $Trigger -Principal $Principal -Settings $Settings -Force | Out-Null
    Start-ScheduledTask -TaskName $TaskName | Out-Null
    Write-Host ">> BipDesk Agent registrado como tarefa de sistema (AtStartup)!" -ForegroundColor Cyan
}

# 8. Criar Atalhos Oficiais na Área de Trabalho e Menu Iniciar com o Ícone BipDesk
try {
    $WshShell = New-Object -ComObject WScript.Shell
    $IconPath = if (Test-Path "$InstallDir\icon.ico") { "$InstallDir\icon.ico,0" } else { "$SessionExe,0" }

    # Atalho na Área de Trabalho (Pública para todos os usuários da máquina)
    $DesktopPath = [Environment]::GetFolderPath("CommonDesktopDirectory")
    if (-not (Test-Path $DesktopPath)) { $DesktopPath = "$env:PUBLIC\Desktop" }
    $Shortcut = $WshShell.CreateShortcut("$DesktopPath\BipDesk Host.lnk")
    $Shortcut.TargetPath = $SessionExe
    $Shortcut.WorkingDirectory = $InstallDir
    $Shortcut.IconLocation = $IconPath
    $Shortcut.Description = "BipDesk Host - Suporte Remoto"
    $Shortcut.Save()

    # Atalho no Menu Iniciar
    $StartMenuPath = "$env:ProgramData\Microsoft\Windows\Start Menu\Programs"
    $StartShortcut = $WshShell.CreateShortcut("$StartMenuPath\BipDesk Host.lnk")
    $StartShortcut.TargetPath = $SessionExe
    $StartShortcut.WorkingDirectory = $InstallDir
    $StartShortcut.IconLocation = $IconPath
    $StartShortcut.Description = "BipDesk Host - Suporte Remoto"
    $StartShortcut.Save()
    Write-Host ">> Atalhos com ícone oficial do BipDesk criados com sucesso!" -ForegroundColor Cyan
} catch {
    Write-Warning "Não foi possível criar atalhos: $_"
}

# 9. White-Label & Stealth: Ocultar e Limpar Rastros do RustDesk
Write-Host ">> Aplicando modo corporativo silencioso (White-Label BipDesk)..." -ForegroundColor Green
try {
    # Remove atalhos padrão do RustDesk
    $RustDeskShortcuts = @(
        "$env:PUBLIC\Desktop\RustDesk.lnk",
        "$env:USERPROFILE\Desktop\RustDesk.lnk",
        "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\RustDesk.lnk",
        "$env:ProgramData\Microsoft\Windows\Start Menu\Programs\RustDesk"
    )
    foreach ($sc in $RustDeskShortcuts) {
        if (Test-Path $sc) { Remove-Item -Path $sc -Recurse -Force -ErrorAction SilentlyContinue }
    }

    # Remove do Painel de Controle (Adicionar ou Remover Programas)
    $UninstallKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk"
    if (Test-Path $UninstallKey) {
        Remove-Item -Path $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Renomeia o serviço do Windows para BipDesk
    Set-Service -Name "RustDesk" -DisplayName "BipDesk Remote Management Service" -Description "Serviço de suporte e gerenciamento corporativo BipDesk Enterprise" -ErrorAction SilentlyContinue

    # Fecha qualquer processo de janela do RustDesk e garante serviço em background
    Stop-Process -Name "RustDesk" -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 1
    Start-Service -Name "RustDesk" -ErrorAction SilentlyContinue
} catch {}

Write-Host "==========================================================" -ForegroundColor Green
Write-Host "   BipDesk Host instalado e operacional com sucesso!       " -ForegroundColor Green
Write-Host "   Acesso Não Assistido 24/7 Habilitado no Boot!          " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
