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
	"net/http"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"
	"time"
	"unsafe"
)

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
		TLSClientConfig: &tls.Config{InsecureSkipVerify: true},
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
			lines := strings.Split(string(b), "\n")
			for _, l := range lines {
				l = strings.TrimSpace(l)
				if strings.HasPrefix(l, "id =") {
					parts := strings.Split(l, "\"")
					if len(parts) >= 2 {
						return parts[1]
					}
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

	// 5. Instala e configura o serviço do RustDesk/BipDesk
	_ = exec.Command(sessionExe, "--install-service").Run()
	time.Sleep(1 * time.Second)

	_ = exec.Command(sessionExe, "--server", "bipdesk.duckdns.org", "--key", "gsTzsIewn3wB9umlEvZgT+Z9ZUKNxcAwbhl+bz2pJFw=").Run()
	time.Sleep(500 * time.Millisecond)

	password := generateRandomPassword(20)
	_ = exec.Command(sessionExe, "--password", password).Run()
	time.Sleep(500 * time.Millisecond)

	// 6. Obtém o ID da máquina
	var machineID string
	rustdeskInstalledExe := `C:\Program Files\RustDesk\RustDesk.exe`
	for i := 0; i < 10; i++ {
		out, err := exec.Command(sessionExe, "--get-id").Output()
		if err == nil {
			s := strings.TrimSpace(string(out))
			if len(s) >= 6 {
				machineID = s
				break
			}
		}
		if _, err := os.Stat(rustdeskInstalledExe); err == nil {
			out2, err2 := exec.Command(rustdeskInstalledExe, "--get-id").Output()
			if err2 == nil {
				s := strings.TrimSpace(string(out2))
				if len(s) >= 6 {
					machineID = s
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
		"token":       "direct",
		"rustdesk_id": machineID,
		"hostname":    hostname,
		"username":    getLoggedInUser(),
		"os_version":  runtime.GOOS,
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

	// 10. Exibe mensagem de confirmação para o usuário
	showMessageBox("BipDesk Host Ativo", fmt.Sprintf("BipDesk Host instalado e configurado com sucesso!\n\nID desta Máquina: %s\n\nEsta máquina já está conectada e disponível no painel BipDesk Admin.", machineID), MB_ICONINFORMATION)

	return machineID, nil
}

func getLoggedInUser() string {
	u, err := user.Current()
	if err == nil && u.Username != "" {
		parts := strings.Split(u.Username, "\\")
		return parts[len(parts)-1]
	}
	return os.Getenv("USERNAME")
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

	case "cmd":
		if runtime.GOOS == "windows" && strings.TrimSpace(cmd.Payload) != "" {
			log.Printf("[BipDesk Host Agent] Executando comando customizado...\n")
			go func() {
				exec.Command("cmd.exe", "/c", cmd.Payload).Run()
			}()
		}
	}
}

func sendHeartbeat(serverURL string, clientID string, httpClient *http.Client) error {
	payload := HeartbeatPayload{
		RustdeskID:   clientID,
		LoggedInUser: getLoggedInUser(),
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
