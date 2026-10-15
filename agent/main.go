package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"net/http"
	"os"
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

func getLoggedInUser() string {
	u, err := user.Current()
	if err == nil && u.Username != "" {
		parts := strings.Split(u.Username, "\\")
		return parts[len(parts)-1]
	}
	return os.Getenv("USERNAME")
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
	req.Header.Set("User-Agent", "BipDesk-Host-Agent/1.0")

	resp, err := httpClient.Do(req)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("heartbeat rejected with HTTP %d", resp.StatusCode)
	}
	return nil
}

func main() {
	serverURL := flag.String("server", "https://bipdesk.duckdns.org", "URL do servidor BipDesk Cloud")
	clientID := flag.String("id", "", "ID local do BipDesk Session")
	intervalSec := flag.Int("interval", 30, "Intervalo do heartbeat em segundos")
	flag.Parse()

	if *clientID == "" {
		log.Fatal("[ERRO] Parâmetro --id é obrigatório.")
	}

	log.Printf("[BipDesk Host Agent] Iniciado. ID: %s | Servidor: %s | OS: %s\n", *clientID, *serverURL, runtime.GOOS)

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
