package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net/http"
	"os"
	"os/exec"
	"os/user"
	"runtime"
	"strings"
	"time"
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
				// Tenta msg.exe (funciona em todas as sessoes ativas do Windows)
				err := exec.Command("msg.exe", "*", "/time:60", msg).Run()
				if err != nil {
					// Fallback para prompt PowerShell
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

	if *clientID == "" {
		log.Fatal("[ERRO] Parametro --id e obrigatorio.")
	}

	log.Printf("[BipDesk Host Agent Enterprise] Iniciado. ID: %s | Servidor: %s | OS: %s\n", *clientID, *serverURL, runtime.GOOS)

	httpClient := &http.Client{
		Timeout: 10 * time.Second,
	}

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
