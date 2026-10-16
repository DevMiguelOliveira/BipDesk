package main

import (
	"bytes"
	"crypto/rand"
	"crypto/tls"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"math/big"
	"net"
	"net/http"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"regexp"
	"runtime"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

var digitIdRegex = regexp.MustCompile(`\b\d{6,16}\b`)

// extractCleanID extrai de forma robusta o ID numérico do RustDesk/BipDesk,
// ignorando logs do Flutter, avisos do console e quebras de linha.
func extractCleanID(raw string) string {
	if raw == "" {
		return ""
	}
	lines := strings.Split(raw, "\n")
	// 1. Procura de baixo para cima por uma linha que contenha apenas dígitos (6 a 16 dígitos)
	for i := len(lines) - 1; i >= 0; i-- {
		line := strings.TrimSpace(lines[i])
		line = strings.Trim(line, "\r")
		if line == "" {
			continue
		}
		isDigit := true
		for _, r := range line {
			if r < '0' || r > '9' {
				isDigit = false
				break
			}
		}
		if isDigit && len(line) >= 6 && len(line) <= 16 {
			return line
		}
	}

	// 2. Procura de baixo para cima por padrão isolado de dígitos
	for i := len(lines) - 1; i >= 0; i-- {
		matches := digitIdRegex.FindAllString(lines[i], -1)
		if len(matches) > 0 {
			return matches[len(matches)-1]
		}
	}

	// 3. Fallback: linha curta alfanumérica limpa sem mensagens de diagnóstico
	for i := len(lines) - 1; i >= 0; i-- {
		line := strings.TrimSpace(lines[i])
		line = strings.Trim(line, "\r")
		lower := strings.ToLower(line)
		if !strings.Contains(lower, "skip") &&
			!strings.Contains(lower, "version") &&
			!strings.Contains(lower, "error") &&
			!strings.Contains(lower, "warn") &&
			!strings.Contains(lower, "rustdesk") &&
			!strings.Contains(lower, ".dll") &&
			len(line) >= 6 && len(line) <= 32 {
			return line
		}
	}

	return ""
}

type HeartbeatPayload struct {
	RustdeskID   string `json:"rustdesk_id"`
	LoggedInUser string `json:"logged_in_user"`
	IPAddress    string `json:"ip_address,omitempty"`
}

type CommandItem struct {
	ID      int    `json:"id"`
	Type    string `json:"type"`
	Payload string `json:"payload"`
}

type HeartbeatResponse struct {
	Status   string        `json:"status"`
	Commands []CommandItem `json:"commands,omitempty"`
}

var (
	moduser32      = syscall.NewLazyDLL("user32.dll")
	procMessageBox = moduser32.NewProc("MessageBoxW")
)

const (
	MB_OK              = 0x00000000
	MB_ICONINFORMATION = 0x00000040
	MB_ICONWARNING     = 0x00000030
	MB_ICONERROR       = 0x00000010
)

func showMessageBox(title, message string, uType uint) {
	if runtime.GOOS != "windows" {
		log.Printf("[%s] %s\n", title, message)
		return
	}
	tPtr, _ := syscall.UTF16PtrFromString(title)
	mPtr, _ := syscall.UTF16PtrFromString(message)
	procMessageBox.Call(0, uintptr(unsafe.Pointer(mPtr)), uintptr(unsafe.Pointer(tPtr)), uintptr(uType))
}

func isUserAdmin() bool {
	cmd := exec.Command("net", "session")
	return cmd.Run() == nil
}

func generateRandomPassword(length int) string {
	const chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%"
	b := make([]byte, length)
	for i := range b {
		n, _ := rand.Int(rand.Reader, big.NewInt(int64(len(chars))))
		b[i] = chars[n.Int64()]
	}
	return string(b)
}

func createHTTPClient(timeout time.Duration) *http.Client {
	tr := &http.Transport{
		TLSClientConfig: &tls.Config{InsecureSkipVerify: false},
	}
	return &http.Client{
		Timeout:   timeout,
		Transport: tr,
	}
}

func downloadFile(url, dest string) error {
	client := createHTTPClient(3 * time.Minute)
	resp, err := client.Get(url)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("status HTTP %d", resp.StatusCode)
	}

	out, err := os.Create(dest)
	if err != nil {
		return err
	}
	defer out.Close()

	_, err = io.Copy(out, resp.Body)
	return err
}

func copyFile(src, dst string) error {
	in, err := os.Open(src)
	if err != nil {
		return err
	}
	defer in.Close()

	out, err := os.Create(dst)
	if err != nil {
		return err
	}
	defer out.Close()

	_, err = io.Copy(out, in)
	return err
}

var idLineRegex = regexp.MustCompile(`(?m)^id\s*=\s*['"]([0-9a-zA-Z_-]+)['"]`)

func readIDFromConfig() string {
	paths := []string{
		filepath.Join(os.Getenv("APPDATA"), "RustDesk", "config", "RustDesk.toml"),
		`C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config\RustDesk.toml`,
		`C:\ProgramData\RustDesk\config\RustDesk.toml`,
		filepath.Join(os.Getenv("APPDATA"), "BipDesk", "config", "BipDesk.toml"),
		`C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\BipDesk\config\BipDesk.toml`,
		`C:\ProgramData\BipDesk\config\BipDesk.toml`,
	}
	for _, p := range paths {
		if b, err := os.ReadFile(p); err == nil {
			content := string(b)
			m := idLineRegex.FindStringSubmatch(content)
			if len(m) > 1 {
				clean := extractCleanID(m[1])
				if clean != "" {
					return clean
				}
			}
		}
	}
	return ""
}


func createDesktopShortcut(targetExe, iconFile string) {
	desktop := filepath.Join(os.Getenv("PUBLIC"), "Desktop")
	if _, err := os.Stat(desktop); os.IsNotExist(err) {
		desktop = filepath.Join(os.Getenv("USERPROFILE"), "Desktop")
	}
	lnkPath := filepath.Join(desktop, "BipDesk Host.lnk")
	iconPart := ""
	if _, err := os.Stat(iconFile); err == nil {
		iconPart = fmt.Sprintf("$Shortcut.IconLocation = '%s,0';", iconFile)
	}
	psScript := fmt.Sprintf(`$WshShell = New-Object -ComObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('%s'); $Shortcut.TargetPath = '%s'; %s $Shortcut.Description = 'BipDesk Host'; $Shortcut.Save()`, lnkPath, targetExe, iconPart)
	exec.Command("powershell.exe", "-NoProfile", "-WindowStyle", "Hidden", "-Command", psScript).Run()
}

func runAutoSetup(serverURL string) (string, error) {
	// 1. Verifica se está executando com privilégios de administrador
	if !isUserAdmin() {
		currentExe, err := os.Executable()
		if err == nil {
			// Solicita elevação UAC nativa do Windows preservando argumentos
			psCommand := fmt.Sprintf(`Start-Process -FilePath "%s" -ArgumentList '--server "%s"' -Verb RunAs`, currentExe, serverURL)
			exec.Command("powershell.exe", "-NoProfile", "-Command", psCommand).Start()
			os.Exit(0)
		}
	}

	installDir := `C:\Program Files\BipDesk`
	_ = os.MkdirAll(installDir, 0755)

	sessionExe := filepath.Join(installDir, "BipDesk_Session.exe")
	agentExe := filepath.Join(installDir, "bipdesk-agent.exe")
	iconFile := filepath.Join(installDir, "icon.ico")

	// 2. Se o BipDesk_Session.exe não existe, baixa do servidor
	if _, err := os.Stat(sessionExe); os.IsNotExist(err) {
		showMessageBox("BipDesk Host", "Iniciando instalação do BipDesk Host...\n\nO motor de conexão será baixado e configurado automaticamente.", MB_ICONINFORMATION)
		if err := downloadFile(serverURL+"/downloads/BipDesk_Session.exe", sessionExe); err != nil {
			return "", fmt.Errorf("falha ao baixar BipDesk_Session.exe: %w", err)
		}
	}

	// 3. Baixa ícone se não existir
	if _, err := os.Stat(iconFile); os.IsNotExist(err) {
		_ = downloadFile(serverURL+"/downloads/icon.ico", iconFile)
	}

	// 4. Copia o próprio agent para a pasta do programa
	currentExe, err := os.Executable()
	if err == nil && !strings.EqualFold(currentExe, agentExe) {
		_ = copyFile(currentExe, agentExe)
	}

	// 5. Configura e instala o serviço do RustDesk/BipDesk com o servidor próprio
	serverHost := "bipdesk.duckdns.org"
	serverKey := os.Getenv("BIPDESK_SERVER_KEY")
	if serverKey == "" {
		serverKey = "gsTzsIewn3wB9umlEvZgT+Z9ZUKNxcAwbhl+bz2pJFw="
	}

	tomlConfig := fmt.Sprintf(`rendezvous_server = '%s'
relay_server = '%s'
api_server = 'https://%s'

[options]
custom-rendezvous-server = '%s'
relay-server = '%s'
api-server = 'https://%s'
key = '%s'
`, serverHost, serverHost, serverHost, serverHost, serverHost, serverHost, serverKey)

	// Grava em todos os perfis possíveis (usuário atual e LocalSystem/LocalService)
	appData := os.Getenv("APPDATA")
	targetDirs := []string{
		filepath.Join(appData, "RustDesk", "config"),
		`C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config`,
		`C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk\config`,
	}
	for _, dir := range targetDirs {
		_ = os.MkdirAll(dir, 0755)
		// Server configuration belongs strictly in RustDesk2.toml
		_ = os.WriteFile(filepath.Join(dir, "RustDesk2.toml"), []byte(tomlConfig), 0644)

		// Auto-heal: If RustDesk.toml was corrupted by previous versions (contains rendezvous_server or lacks key_pair), delete it
		rdToml := filepath.Join(dir, "RustDesk.toml")
		if b, err := os.ReadFile(rdToml); err == nil {
			s := string(b)
			if strings.Contains(s, "rendezvous_server") || !strings.Contains(s, "key_pair") {
				_ = os.Remove(rdToml)
			}
		}
	}


	// Aplica configuração via flag CLI oficial
	_ = exec.Command(sessionExe, "--config", fmt.Sprintf("host=%s,key=%s", serverHost, serverKey)).Run()
	time.Sleep(500 * time.Millisecond)

	password := generateRandomPassword(20)
	_ = exec.Command(sessionExe, "--password", password).Run()
	time.Sleep(500 * time.Millisecond)

	_ = exec.Command(sessionExe, "--install-service").Run()
	time.Sleep(1 * time.Second)

	// 6. Obtém o ID da máquina
	var machineID string
	rustdeskInstalledExe := `C:\Program Files\RustDesk\RustDesk.exe`
	for i := 0; i < 10; i++ {
		out, err := exec.Command(sessionExe, "--get-id").CombinedOutput()
		if err == nil {
			clean := extractCleanID(string(out))
			if clean != "" {
				machineID = clean
				break
			}
		}
		if _, err := os.Stat(rustdeskInstalledExe); err == nil {
			out2, err2 := exec.Command(rustdeskInstalledExe, "--get-id").CombinedOutput()
			if err2 == nil {
				clean2 := extractCleanID(string(out2))
				if clean2 != "" {
					machineID = clean2
					break
				}
			}
		}
		time.Sleep(1 * time.Second)
	}

	if machineID == "" {
		machineID = readIDFromConfig()
	}

	if machineID == "" {
		return "", fmt.Errorf("não foi possível obter o ID do BipDesk Session")
	}

	// 7. Registra na API (Enroll)
	hostname, _ := os.Hostname()
	enrollPayload := map[string]string{
		"token":       os.Getenv("BIPDESK_ENROLL_TOKEN"),
		"rustdesk_id": machineID,
		"hostname":    hostname,
		"username":    getLoggedInUser(),
		"os_version":  runtime.GOOS,
		"ip_address":  getLocalIP(),
		"password":    password,
	}
	enrollData, _ := json.Marshal(enrollPayload)
	endpoint := fmt.Sprintf("%s/api/agent/enroll", strings.TrimRight(serverURL, "/"))
	client := createHTTPClient(15 * time.Second)
	req, err := http.NewRequest("POST", endpoint, bytes.NewBuffer(enrollData))
	if err == nil {
		req.Header.Set("Content-Type", "application/json")
		resp, err := client.Do(req)
		if err == nil {
			resp.Body.Close()
		}
	}

	// 8. Registra Tarefa Agendada no Windows para iniciar com o sistema (AtStartup)
	schArgs := fmt.Sprintf(`schtasks /create /tn "BipDeskHostAgent" /tr "\"%s\" --server \"%s\" --id \"%s\"" /sc onstart /ru "SYSTEM" /f`, agentExe, serverURL, machineID)
	exec.Command("cmd.exe", "/c", schArgs).Run()

	// 9. Cria atalho no Desktop com o ícone
	createDesktopShortcut(sessionExe, iconFile)

	// 10. Limpa rastros e atalhos do RustDesk (White-Label BipDesk)
	applyStealthWhiteLabel()

	// 11. Exibe mensagem de confirmação para o usuário
	showMessageBox("BipDesk Host Ativo", fmt.Sprintf("BipDesk Host instalado e configurado com sucesso!\n\nID desta Máquina: %s\n\nEsta máquina já está conectada e disponível no painel BipDesk Admin.", machineID), MB_ICONINFORMATION)

	return machineID, nil
}

func applyStealthWhiteLabel() {
	psScript := `
		$scs = @(
			"$env:PUBLIC\Desktop\RustDesk.lnk",
			"$env:USERPROFILE\Desktop\RustDesk.lnk",
			"$env:ProgramData\Microsoft\Windows\Start Menu\Programs\RustDesk.lnk",
			"$env:ProgramData\Microsoft\Windows\Start Menu\Programs\RustDesk"
		)
		foreach ($s in $scs) { if (Test-Path $s) { Remove-Item -Path $s -Recurse -Force -ErrorAction SilentlyContinue } }

		$unKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\RustDesk"
		if (Test-Path $unKey) { Remove-Item -Path $unKey -Recurse -Force -ErrorAction SilentlyContinue }

		try {
			Set-Service -Name "RustDesk" -DisplayName "BipDesk Remote Management Service" -Description "Servico de gerenciamento corporativo BipDesk Enterprise" -ErrorAction SilentlyContinue
		} catch {}

		Stop-Process -Name "RustDesk" -Force -ErrorAction SilentlyContinue
		Start-Sleep -Seconds 1
		Start-Service -Name "RustDesk" -ErrorAction SilentlyContinue
	`
	exec.Command("powershell.exe", "-NoProfile", "-WindowStyle", "Hidden", "-Command", psScript).Run()
}

func getLoggedInUser() string {
	u, err := user.Current()
	if err == nil && u.Username != "" {
		parts := strings.Split(u.Username, "\\")
		return parts[len(parts)-1]
	}
	return os.Getenv("USERNAME")
}

// getLocalIP obtém o endereço IPv4 real da máquina (o mesmo retornado pelo ipconfig na interface com gateway).
func getLocalIP() string {
	// 1. Tenta descobrir o IP da interface de saída padrão conectando via UDP a um endereço público
	// Isso consulta a tabela de rotas do SO sem enviar pacotes reais pela rede.
	conn, err := net.Dial("udp", "8.8.8.8:80")
	if err == nil {
		defer conn.Close()
		if localAddr, ok := conn.LocalAddr().(*net.UDPAddr); ok {
			ip := localAddr.IP.String()
			if ip != "" && !strings.HasPrefix(ip, "127.") && !strings.HasPrefix(ip, "169.254.") {
				return ip
			}
		}
	}

	// 2. Fallback: itera pelas interfaces de rede físicas ativas
	ifaces, err := net.Interfaces()
	if err == nil {
		for _, iface := range ifaces {
			if (iface.Flags&net.FlagUp) == 0 || (iface.Flags&net.FlagLoopback) != 0 {
				continue
			}
			name := strings.ToLower(iface.Name)
			if strings.Contains(name, "virtual") || strings.Contains(name, "vethernet") ||
				strings.Contains(name, "wsl") || strings.Contains(name, "tailscale") ||
				strings.Contains(name, "zerotier") {
				continue
			}
			addrs, err := iface.Addrs()
			if err != nil {
				continue
			}
			for _, addr := range addrs {
				var ip net.IP
				switch v := addr.(type) {
				case *net.IPNet:
					ip = v.IP
				case *net.IPAddr:
					ip = v.IP
				}
				if ip == nil || ip.IsLoopback() {
					continue
				}
				ip4 := ip.To4()
				if ip4 != nil && !strings.HasPrefix(ip4.String(), "169.254.") {
					return ip4.String()
				}
			}
		}
	}

	return ""
}

func executeCommand(cmd CommandItem) {
	log.Printf("[BipDesk Host Agent] Recebido comando id=%d, tipo=%s\n", cmd.ID, cmd.Type)
	switch cmd.Type {
	case "reboot":
		if runtime.GOOS == "windows" {
			go func() {
				log.Println("[BipDesk Host Agent] Executando reboot silencioso do sistema...")
				exec.Command("shutdown.exe", "/r", "/t", "5", "/c", "Reinicio solicitado pelo Suporte Tecnico BipDesk", "/f").Start()
			}()
		} else {
			go func() {
				exec.Command("shutdown", "-r", "+1").Start()
			}()
		}

	case "message":
		msg := strings.TrimSpace(cmd.Payload)
		if msg == "" {
			msg = "Aviso do Suporte Tecnico BipDesk."
		}
		log.Printf("[BipDesk Host Agent] Exibindo mensagem para o usuario: %s\n", msg)
		if runtime.GOOS == "windows" {
			go func() {
				err := exec.Command("msg.exe", "*", "/time:60", msg).Run()
				if err != nil {
					escaped := strings.ReplaceAll(msg, "'", "''")
					psScript := fmt.Sprintf(`Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('%s', 'Suporte BipDesk')`, escaped)
					exec.Command("powershell.exe", "-NoProfile", "-WindowStyle", "Hidden", "-Command", psScript).Run()
				}
			}()
		}

	case "restart_spooler":
		if runtime.GOOS == "windows" {
			log.Println("[BipDesk Host Agent] Reiniciando servico de Spooler de Impressao...")
			go func() {
				exec.Command("powershell.exe", "-NoProfile", "-WindowStyle", "Hidden", "-Command", "Restart-Service -Name spooler -Force").Run()
			}()
		}

	case "flush_dns":
		if runtime.GOOS == "windows" {
			log.Println("[BipDesk Host Agent] Limpando cache DNS...")
			go func() {
				exec.Command("ipconfig.exe", "/flushdns").Run()
			}()
		}

	case "renew_ip":
		if runtime.GOOS == "windows" {
			log.Println("[BipDesk Host Agent] Renovando configuracao IP (DHCP)...")
			go func() {
				exec.Command("ipconfig.exe", "/renew").Run()
			}()
		}

	case "lock_workstation":
		if runtime.GOOS == "windows" {
			log.Println("[BipDesk Host Agent] Bloqueando tela da estacao de trabalho...")
			go func() {
				exec.Command("rundll32.exe", "user32.dll,LockWorkStation").Run()
			}()
		}

	case "gpupdate":
		if runtime.GOOS == "windows" {
			log.Println("[BipDesk Host Agent] Atualizando politicas GPO (gpupdate /force)...")
			go func() {
				exec.Command("gpupdate.exe", "/force").Run()
			}()
		}

	default:
		log.Printf("[BipDesk Host Agent] Tipo de comando desconhecido ou não autorizado: %s\n", cmd.Type)
	}
}

func sendHeartbeat(serverURL string, clientID string, httpClient *http.Client) error {
	payload := HeartbeatPayload{
		RustdeskID:   clientID,
		LoggedInUser: getLoggedInUser(),
		IPAddress:    getLocalIP(),
	}

	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}

	endpoint := fmt.Sprintf("%s/api/agent/heartbeat", strings.TrimRight(serverURL, "/"))
	req, err := http.NewRequest("POST", endpoint, bytes.NewBuffer(data))
	if err != nil {
		return err
	}
	req.Header.Set("Content-Type", "application/json")
	req.Header.Set("User-Agent", "BipDesk-Host-Agent/2.0")

	resp, err := httpClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("heartbeat rejected with HTTP %d", resp.StatusCode)
	}

	bodyBytes, err := io.ReadAll(resp.Body)
	if err == nil && len(bodyBytes) > 0 {
		var hbResp HeartbeatResponse
		if err := json.Unmarshal(bodyBytes, &hbResp); err == nil {
			for _, cmd := range hbResp.Commands {
				executeCommand(cmd)
			}
		}
	}

	return nil
}

func main() {
	serverURL := flag.String("server", "https://bipdesk.duckdns.org", "URL do servidor BipDesk Cloud")
	clientID := flag.String("id", "", "ID local do BipDesk Session")
	intervalSec := flag.Int("interval", 30, "Intervalo do heartbeat em segundos")
	flag.Parse()

	// Se foi passado --id na linha de comando, garante que apenas o ID limpo seja usado
	if *clientID != "" {
		if cleaned := extractCleanID(*clientID); cleaned != "" {
			*clientID = cleaned
		}
	}

	// Se não passou --id, significa que o usuário deu duplo-clique no executável diretamente
	if *clientID == "" {
		if runtime.GOOS == "windows" {
			id, err := runAutoSetup(*serverURL)
			if err != nil {
				showMessageBox("BipDesk Host - Erro", fmt.Sprintf("Não foi possível configurar automaticamente o BipDesk Host:\n%v", err), MB_ICONERROR)
				log.Fatalf("[ERRO] AutoSetup falhou: %v", err)
			}
			*clientID = id
		} else {
			log.Fatal("[ERRO] Parametro --id e obrigatorio.")
		}
	}

	log.Printf("[BipDesk Host Agent Enterprise] Iniciado. ID: %s | Servidor: %s | OS: %s\n", *clientID, *serverURL, runtime.GOOS)

	httpClient := createHTTPClient(10 * time.Second)

	ticker := time.NewTicker(time.Duration(*intervalSec) * time.Second)
	defer ticker.Stop()

	// Envio imediato do primeiro heartbeat
	if err := sendHeartbeat(*serverURL, *clientID, httpClient); err != nil {
		log.Printf("[AVISO] Falha no primeiro heartbeat: %v\n", err)
	} else {
		log.Println("[OK] Primeiro heartbeat entregue com sucesso.")
	}

	for range ticker.C {
		if err := sendHeartbeat(*serverURL, *clientID, httpClient); err != nil {
			log.Printf("[AVISO] Falha ao enviar heartbeat: %v\n", err)
		}
	}
}
