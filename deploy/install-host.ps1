<#
.SYNOPSIS
    BipDesk Host - Instalador Silencioso Não Assistido (Estilo TeamViewer Host)
    Execução: irm https://seu-dominio.duckdns.org/install/TOKEN | iex
#>

if (-not (Test-Path variable:ServerUrl) -or -not $ServerUrl) { $ServerUrl = "https://bipdesk.duckdns.org" }
if (-not (Test-Path variable:EnrollToken) -or -not $EnrollToken) { $EnrollToken = "" }


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

$ServerHost = "bipdesk.duckdns.org"
$ServerKey = $env:BIPDESK_SERVER_KEY
if (-not $ServerKey) {
    $ServerKey = "gsTzsIewn3wB9umlEvZgT+Z9ZUKNxcAwbhl+bz2pJFw="
}

$tomlContent = @"
rendezvous_server = '$ServerHost'
relay_server = '$ServerHost'
api_server = 'https://$ServerHost'

[options]
custom-rendezvous-server = '$ServerHost'
relay-server = '$ServerHost'
api-server = 'https://$ServerHost'
key = '$ServerKey'
"@

$tomlContent = @"
rendezvous_server = '$ServerHost:21116'
relay_server = '$ServerHost:21117'
api_server = 'https://$ServerHost'

[options]
custom-rendezvous-server = '$ServerHost'
relay-server = '$ServerHost'
api-server = 'https://$ServerHost'
key = '$ServerKey'
"@

$targetDirs = @(
    "$env:APPDATA\RustDesk\config",
    "C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config",
    "C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk\config"
)

foreach ($dir in $targetDirs) {
    try {
        if (-not (Test-Path $dir)) {
            New-Item -ItemType Directory -Path $dir -Force -ErrorAction SilentlyContinue | Out-Null
        }
        # Grava apenas em RustDesk2.toml (opções de conexão e servidor customizado)
        Set-Content -Path "$dir\RustDesk2.toml" -Value $tomlContent -Encoding UTF8 -Force -ErrorAction SilentlyContinue | Out-Null

        # Se RustDesk.toml foi corrompido anteriormente por sobreposição de servidor sem chaves, remove para o motor regenerar o par Ed25519
        $mainToml = "$dir\RustDesk.toml"
        if (Test-Path $mainToml) {
            $txt = Get-Content $mainToml -Raw -ErrorAction SilentlyContinue
            if ($txt -and ($txt -like "*rendezvous_server*" -or $txt -notlike "*key_pair*")) {
                Remove-Item -Path $mainToml -Force -ErrorAction SilentlyContinue
            }
        }
    } catch { }
}

# Configurar via CLI oficial
& $SessionExe --config "host=$ServerHost,key=$ServerKey" | Out-Null
Start-Sleep -Milliseconds 800

# Definir a senha permanente silenciosamente
& $SessionExe --password $Password | Out-Null
Start-Sleep -Milliseconds 800

# Registra e inicia o serviço
& $SessionExe --install-service | Out-Null
Start-Sleep -Seconds 1
Restart-Service -Name "RustDesk" -Force -ErrorAction SilentlyContinue | Out-Null

function Get-CleanRustdeskId {
    param([string]$RawText)
    if (-not $RawText) { return "" }
    $lines = ($RawText -split "`r?`n") | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne "" }
    
    # 1. Linha estritamente de dígitos (6 a 16 dígitos)
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '^\d{6,16}$') {
            return $lines[$i]
        }
    }
    # 2. Padrão de dígitos isolados (ignorando logs de compilação ou diagnóstico)
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '\b(\d{6,16})\b' -and $lines[$i] -notmatch 'skip|version|error|warn|info|debug') {
            return $matches[1]
        }
    }
    return ""
}

function Get-RustDeskRealId {
    param([string]$SessionExe)

    # 1. Candidatos a executável do motor nativo
    $candidateExes = @(
        "C:\Program Files\RustDesk\RustDesk.exe",
        "C:\Program Files\BipDesk\rustdesk.exe",
        "$env:LOCALAPPDATA\rustdesk\rustdesk.exe"
    )
    $candidateExes += (Get-ChildItem -Path "C:\Users\*\AppData\Local\rustdesk\rustdesk.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)

    # 2. Se o executável descompactado ainda não existe, executa brevemente o SessionExe para extrair
    $engineExe = $candidateExes | Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $engineExe -and (Test-Path $SessionExe)) {
        Start-Process -FilePath $SessionExe -ArgumentList "--get-id" -WindowStyle Hidden -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        $candidateExes += (Get-ChildItem -Path "C:\Users\*\AppData\Local\rustdesk\rustdesk.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
        $engineExe = $candidateExes | Where-Object { Test-Path $_ } | Select-Object -First 1
    }

    # Copia o motor nativo descompactado para C:\Program Files\RustDesk
    if ($engineExe -and (Test-Path $engineExe)) {
        try {
            $engineDir = Split-Path -Path $engineExe -Parent
            $destDir = "C:\Program Files\RustDesk"
            if (-not (Test-Path $destDir)) { New-Item -ItemType Directory -Path $destDir -Force | Out-Null }
            Copy-Item -Path "$engineDir\*" -Destination $destDir -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
        } catch {}
    }

    # Garante que o serviço RustDesk está em execução
    $svc = Get-Service -Name "RustDesk" -ErrorAction SilentlyContinue
    if ($svc -and $svc.Status -ne 'Running') {
        Start-Service -Name "RustDesk" -ErrorAction SilentlyContinue
    }

    $allExes = @(
        "C:\Program Files\RustDesk\RustDesk.exe",
        "C:\Program Files\BipDesk\rustdesk.exe",
        "$env:LOCALAPPDATA\rustdesk\rustdesk.exe"
    )
    $allExes += (Get-ChildItem -Path "C:\Users\*\AppData\Local\rustdesk\rustdesk.exe" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $allExes += $SessionExe

    # 3. Tenta obter o ID via --get-id em todos os executáveis candidatos
    for ($round = 0; $round -lt 15; $round++) {
        foreach ($exe in $allExes) {
            if ($exe -and (Test-Path $exe)) {
                try {
                    $outText = (& $exe --get-id 2>&1 | Out-String)
                    $clean = Get-CleanRustdeskId -RawText $outText
                    if ($clean -and $clean.Length -ge 6) {
                        return $clean
                    }
                } catch {}
            }
        }

        # 4. Busca nos logs por 'Generated id (\d+)'
        $logDirs = @(
            "C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk\log",
            "C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\log",
            "$env:APPDATA\RustDesk\log"
        )
        $logDirs += (Get-ChildItem -Path "C:\Users\*\AppData\Roaming\RustDesk\log" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)

        foreach ($ld in $logDirs) {
            if ($ld -and (Test-Path $ld)) {
                $logFiles = Get-ChildItem -Path $ld -Filter "*.log" -Recurse -ErrorAction SilentlyContinue
                foreach ($lf in $logFiles) {
                    try {
                        $txt = Get-Content -Path $lf.FullName -Raw -ErrorAction SilentlyContinue
                        if ($txt -match 'Generated id (\d{6,16})') {
                            return $matches[1]
                        }
                    } catch {}
                }
            }
        }

        # 5. Busca nas configurações por 'id = "..."' ou 'id = ''...'''
        $cfgDirs = @(
            "C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk\config",
            "C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config",
            "$env:APPDATA\RustDesk\config"
        )
        $cfgDirs += (Get-ChildItem -Path "C:\Users\*\AppData\Roaming\RustDesk\config" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)

        foreach ($cd in $cfgDirs) {
            if ($cd -and (Test-Path $cd)) {
                $cfgFiles = Get-ChildItem -Path $cd -Filter "*.toml" -ErrorAction SilentlyContinue
                foreach ($cf in $cfgFiles) {
                    try {
                        $txt = Get-Content -Path $cf.FullName -Raw -ErrorAction SilentlyContinue
                        if ($txt -match '(?m)^id\s*=\s*[''"](\d{6,16})[''"]') {
                            return $matches[1]
                        }
                    } catch {}
                }
            }
        }

        # Se ainda não gerou, executa o motor brevemente como servidor por 2 segundos para forçar o handshake com HBBS
        if ($round -eq 2 -and $engineExe -and (Test-Path $engineExe)) {
            $p = Start-Process -FilePath $engineExe -ArgumentList "--server" -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
            if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
        }

        Start-Sleep -Seconds 1
    }

    return ""
}

# 5. Obter ID do BipDesk
Write-Host "[4/5] Identificando ID da máquina..." -ForegroundColor Green
$MachineId = Get-RustDeskRealId -SessionExe $SessionExe


# 6. Registrar Máquina na API (Enroll)
Write-Host "[5/5] Registrando no painel BipDesk Admin..." -ForegroundColor Green
$ComputerName = $env:COMPUTERNAME
$Username = [System.Environment]::UserName
$OS = (Get-CimInstance Win32_OperatingSystem).Caption

# Detectar IP local real da máquina (o mesmo do ipconfig na interface com gateway)
$LocalIp = ""
try {
    $s = New-Object System.Net.Sockets.Socket([System.Net.Sockets.AddressFamily]::InterNetwork, [System.Net.Sockets.SocketType]::Dgram, [System.Net.Sockets.ProtocolType]::Udp)
    $s.Connect("8.8.8.8", 80)
    $LocalIp = ($s.LocalEndPoint -as [System.Net.IPEndPoint]).Address.IPAddressToString
    $s.Close()
} catch {}

if (-not $LocalIp -or $LocalIp -like "127.*") {
    $adapters = [System.Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {
        $_.OperationalStatus -eq 'Up' -and
        $_.NetworkInterfaceType -ne 'Loopback' -and
        $_.Description -notmatch 'Virtual|Hyper-V|vEthernet|WSL|Tailscale|ZeroTier'
    }
    foreach ($adapter in $adapters) {
        $ipProps = $adapter.GetIPProperties()
        if ($ipProps.GatewayAddresses.Count -gt 0) {
            foreach ($unicast in $ipProps.UnicastAddresses) {
                if ($unicast.Address.AddressFamily -eq 'InterNetwork' -and -not $unicast.Address.IPAddressToString.StartsWith('169.254')) {
                    $LocalIp = $unicast.Address.IPAddressToString
                    break
                }
            }
        }
        if ($LocalIp) { break }
    }
}


if (-not $MachineId -or $MachineId.Length -lt 6) {
    Write-Error "ERRO CRÍTICO: Não foi possível determinar o ID do serviço BipDesk Host nesta máquina. A instalação não pode prosseguir sem um ID válido."
    Exit 1
}

$Payload = @{


    token = $EnrollToken
    rustdesk_id = $MachineId
    hostname = $ComputerName
    username = $Username
    os_version = $OS
    ip_address = $LocalIp
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
} catch {
    Write-Warning "Aviso: Não foi possível registrar tarefa agendada: $_"
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
