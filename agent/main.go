package main

import (
	"bytes"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha256"
	"crypto/tls"
	"encoding/hex"
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
	"sync"
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
	CPUUsage     int    `json:"cpu_usage"`
	RAMUsage     int    `json:"ram_usage"`
	DiskFreeGB   int    `json:"disk_free_gb"`
	CPUModel     string `json:"cpu_model,omitempty"`
}

type CommandItem struct {
	ID        int    `json:"id"`
	Type      string `json:"type"`
	Payload   string `json:"payload,omitempty"`
	Nonce     string `json:"nonce"`
	Timestamp int64  `json:"timestamp"`
	Signature string `json:"signature"`
}

type HeartbeatResponse struct {
	Status   string        `json:"status"`
	Commands []CommandItem `json:"commands,omitempty"`
}

type ServerKeyResponse struct {
	ServerKey string `json:"server_key"`
}

type EnrollResponse struct {
	Status       string `json:"status"`
	MachineID    int    `json:"machine_id"`
	MachineToken string `json:"machine_token"`
	ServerKey    string `json:"server_key"`
}

var (
	moduser32               = syscall.NewLazyDLL("user32.dll")
	procMessageBox          = moduser32.NewProc("MessageBoxW")
	modkernel32             = syscall.NewLazyDLL("kernel32.dll")
	procGlobalMemoryStatus  = modkernel32.NewProc("GlobalMemoryStatusEx")
	procGetSystemTimes      = modkernel32.NewProc("GetSystemTimes")
	procGetDiskFreeSpaceExW = modkernel32.NewProc("GetDiskFreeSpaceExW")

	modcrypt32             = syscall.NewLazyDLL("crypt32.dll")
	procCryptProtectData   = modcrypt32.NewProc("CryptProtectData")
	procCryptUnprotectData = modcrypt32.NewProc("CryptUnprotectData")
)

type memoryStatusEx struct {
	dwLength                uint32
	dwMemoryLoad            uint32
	ullTotalPhys            uint64
	ullAvailPhys            uint64
	ullTotalPageFile        uint64
	ullAvailPageFile        uint64
	ullTotalVirtual         uint64
	ullAvailVirtual         uint64
	ullAvailExtendedVirtual uint64
}

type fileTime struct {
	dwLowDateTime  uint32
	dwHighDateTime uint32
}

func (ft fileTime) toUint64() uint64 {
	return (uint64(ft.dwHighDateTime) << 32) | uint64(ft.dwLowDateTime)
}

var (
	lastIdleTime   uint64
	lastKernelTime uint64
	lastUserTime   uint64
)

func getCPUUsage() int {
	if runtime.GOOS != "windows" {
		return 0
	}
	var idle, kernel, user fileTime
	r, _, _ := procGetSystemTimes.Call(
		uintptr(unsafe.Pointer(&idle)),
		uintptr(unsafe.Pointer(&kernel)),
		uintptr(unsafe.Pointer(&user)),
	)
	if r == 0 {
		return 0
	}
	idlet := idle.toUint64()
	kernt := kernel.toUint64()
	usert := user.toUint64()

	if lastKernelTime == 0 && lastUserTime == 0 {
		lastIdleTime = idlet
		lastKernelTime = kernt
		lastUserTime = usert
		return 12
	}

	usr := usert - lastUserTime
	ker := kernt - lastKernelTime
	idl := idlet - lastIdleTime

	lastIdleTime = idlet
	lastKernelTime = kernt
	lastUserTime = usert

	sys := ker + usr
	if sys == 0 {
		return 0
	}
	cpu := int(float64(sys-idl) * 100.0 / float64(sys))
	if cpu < 0 {
		cpu = 0
	}
	if cpu > 100 {
		cpu = 100
	}
	return cpu
}

func getRAMUsage() int {
	if runtime.GOOS != "windows" {
		return 0
	}
	var mem memoryStatusEx
	mem.dwLength = uint32(unsafe.Sizeof(mem))
	r, _, _ := procGlobalMemoryStatus.Call(uintptr(unsafe.Pointer(&mem)))
	if r == 0 {
		return 0
	}
	return int(mem.dwMemoryLoad)
}

func getDiskFreeGB() int {
	if runtime.GOOS != "windows" {
		return 0
	}
	var freeBytes, totalBytes, totalFreeBytes uint64
	ptr, err := syscall.UTF16PtrFromString("C:\\")
	if err != nil {
		return 0
	}
	r, _, _ := procGetDiskFreeSpaceExW.Call(
		uintptr(unsafe.Pointer(ptr)),
		uintptr(unsafe.Pointer(&freeBytes)),
		uintptr(unsafe.Pointer(&totalBytes)),
		uintptr(unsafe.Pointer(&totalFreeBytes)),
	)
	if r == 0 {
		return 0
	}
	return int(freeBytes / (1024 * 1024 * 1024))
}

func getCPUModel() string {
	model := os.Getenv("PROCESSOR_IDENTIFIER")
	if model != "" {
		parts := strings.Split(model, ",")
		if len(parts) > 0 {
			return strings.TrimSpace(parts[0])
		}
		return model
	}
	return runtime.GOARCH
}

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
	if runtime.GOOS != "windows" {
		return os.Geteuid() == 0
	}
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
		TLSClientConfig: &tls.Config{
			MinVersion: tls.VersionTLS12,
		},
	}
	return &http.Client{
		Timeout:   timeout,
		Transport: tr,
	}
}

// downloadFile baixa um arquivo e valida o hash SHA-256 esperado se fornecido (ADR-007)
func downloadFile(url, dest, expectedSHA256 string) error {
	client := createHTTPClient(3 * time.Minute)
	resp, err := client.Get(url)
	if err != nil {
		return err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return fmt.Errorf("status HTTP %d ao baixar %s", resp.StatusCode, url)
	}

	tmpDest := dest + ".part"
	out, err := os.Create(tmpDest)
	if err != nil {
		return err
	}

	hasher := sha256.New()
	writer := io.MultiWriter(out, hasher)

	if _, err := io.Copy(writer, resp.Body); err != nil {
		out.Close()
		_ = os.Remove(tmpDest)
		return err
	}
	out.Close()

	if expectedSHA256 != "" {
		actualHash := hex.EncodeToString(hasher.Sum(nil))
		if !strings.EqualFold(actualHash, expectedSHA256) {
			_ = os.Remove(tmpDest)
			return fmt.Errorf("falha de integridade SHA-256 para %s: esperado %s, obtido %s", dest, expectedSHA256, actualHash)
		}
	}

	return os.Rename(tmpDest, dest)
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

// ----------------- DPAPI e Armazenamento Seguro de Token (ADR-001) -----------------

type dataBlob struct {
	cbData uint32
	pbData *byte
}

func newBlob(d []byte) *dataBlob {
	if len(d) == 0 {
		return &dataBlob{}
	}
	return &dataBlob{
		cbData: uint32(len(d)),
		pbData: &d[0],
	}
}

func (b *dataBlob) toByteArray() []byte {
	if b.cbData == 0 || b.pbData == nil {
		return nil
	}
	d := make([]byte, b.cbData)
	copy(d, unsafe.Slice(b.pbData, b.cbData))
	return d
}

func encryptDPAPI(data []byte) ([]byte, error) {
	if runtime.GOOS != "windows" {
		return data, nil
	}
	var outBlob dataBlob
	r, _, err := procCryptProtectData.Call(
		uintptr(unsafe.Pointer(newBlob(data))),
		0, 0, 0, 0, 0,
		uintptr(unsafe.Pointer(&outBlob)),
	)
	if r == 0 {
		return nil, fmt.Errorf("CryptProtectData falhou: %w", err)
	}
	defer syscall.LocalFree(syscall.Handle(unsafe.Pointer(outBlob.pbData)))
	return outBlob.toByteArray(), nil
}

func decryptDPAPI(data []byte) ([]byte, error) {
	if runtime.GOOS != "windows" {
		return data, nil
	}
	var outBlob dataBlob
	r, _, err := procCryptUnprotectData.Call(
		uintptr(unsafe.Pointer(newBlob(data))),
		0, 0, 0, 0, 0,
		uintptr(unsafe.Pointer(&outBlob)),
	)
	if r == 0 {
		return nil, fmt.Errorf("CryptUnprotectData falhou: %w", err)
	}
	defer syscall.LocalFree(syscall.Handle(unsafe.Pointer(outBlob.pbData)))
	return outBlob.toByteArray(), nil
}

func getTokenFilePath() string {
	if runtime.GOOS == "windows" {
		pd := os.Getenv("ProgramData")
		if pd == "" {
			pd = `C:\ProgramData`
		}
		return filepath.Join(pd, "BipDesk", "token.dat")
	}
	return filepath.Join(os.TempDir(), "bipdesk-token.dat")
}

func saveMachineToken(token string) error {
	p := getTokenFilePath()
	if err := os.MkdirAll(filepath.Dir(p), 0700); err != nil {
		return err
	}
	enc, err := encryptDPAPI([]byte(token))
	if err != nil {
		// Fallback sem DPAPI caso ocorra erro específico do SO
		enc = []byte(token)
	}
	return os.WriteFile(p, enc, 0600)
}

func loadMachineToken() string {
	p := getTokenFilePath()
	data, err := os.ReadFile(p)
	if err != nil {
		return ""
	}
	dec, err := decryptDPAPI(data)
	if err == nil && len(dec) > 0 {
		return string(dec)
	}
	return string(data)
}

// ----------------- Comandos Remotos e Proteção Criptográfica (ADR-002) -----------------

var (
	nonceMutex sync.Mutex
	seenNonces = make(map[string]int64)
)

func isValidNonce(nonce string, now int64) bool {
	if nonce == "" {
		return false
	}
	nonceMutex.Lock()
	defer nonceMutex.Unlock()

	// Limpa nonces expirados (> 300s)
	for k, ts := range seenNonces {
		if now-ts > 300 {
			delete(seenNonces, k)
		}
	}
	if _, exists := seenNonces[nonce]; exists {
		return false
	}
	seenNonces[nonce] = now
	return true
}

func verifyCommandSignature(cmd CommandItem, clientID, machineToken string) bool {
	if machineToken == "" {
		return false
	}
	tokenHashBytes := sha256.Sum256([]byte(machineToken))
	tokenHashHex := hex.EncodeToString(tokenHashBytes[:])

	msgToSign := fmt.Sprintf("%s:%s:%s:%d", cmd.Type, clientID, cmd.Nonce, cmd.Timestamp)
	mac := hmac.New(sha256.New, []byte(tokenHashHex))
	mac.Write([]byte(msgToSign))
	expectedSig := hex.EncodeToString(mac.Sum(nil))

	return hmac.Equal([]byte(strings.ToLower(cmd.Signature)), []byte(strings.ToLower(expectedSig)))
}

func executeCommand(cmd CommandItem, clientID, machineToken string) error {
	now := time.Now().Unix()

	// 1. Verificação de expiração temporal (< 60s)
	skew := now - cmd.Timestamp
	if skew < -30 || skew > 60 {
		return fmt.Errorf("comando expirado ou relogio dessincronizado (skew=%ds)", skew)
	}

	// 2. Verificação de proteção contra repetição (Replay Nonce)
	if !isValidNonce(cmd.Nonce, now) {
		return fmt.Errorf("replay attack detectado ou nonce reutilizado: %s", cmd.Nonce)
	}

	// 3. Verificação de assinatura criptográfica
	if !verifyCommandSignature(cmd, clientID, machineToken) {
		return fmt.Errorf("assinatura criptografica invalida para comando %d", cmd.ID)
	}

	log.Printf("[AUDITORIA AGENTE] Comando autorizado recebido: id=%d, tipo=%s, nonce=%s\n", cmd.ID, cmd.Type, cmd.Nonce)

	// 4. Enum Fechado Estrito (ADR-002 / R3)
	switch cmd.Type {
	case "cad":
		log.Println("[AUDITORIA AGENTE] Executando comando SAS/CAD...")
		sessionExe := `C:\Program Files\BipDesk\BipDesk_Session.exe`
		if _, err := os.Stat(sessionExe); err == nil {
			go exec.Command(sessionExe, "--send-cad").Run()
		} else {
			rustdeskExe := `C:\Program Files\RustDesk\RustDesk.exe`
			if _, err := os.Stat(rustdeskExe); err == nil {
				go exec.Command(rustdeskExe, "--send-cad").Run()
			}
		}

	case "restart_spooler":
		log.Println("[AUDITORIA AGENTE] Reiniciando servico de spooler de impressao...")
		go func() {
			_ = exec.Command("net.exe", "stop", "spooler").Run()
			time.Sleep(1 * time.Second)
			_ = exec.Command("net.exe", "start", "spooler").Run()
		}()

	case "flush_dns":
		log.Println("[AUDITORIA AGENTE] Executando limpeza de cache DNS...")
		go func() {
			_ = exec.Command("ipconfig.exe", "/flushdns").Run()
		}()

	case "lock":
		log.Println("[AUDITORIA AGENTE] Bloqueando estacao de trabalho...")
		go func() {
			_ = exec.Command("rundll32.exe", "user32.dll,LockWorkStation").Run()
		}()

	case "reboot":
		log.Println("[AUDITORIA AGENTE] Reiniciando estacao de trabalho sob demanda...")
		go func() {
			_ = exec.Command("shutdown.exe", "/r", "/t", "5", "/c", "Reinicio solicitado pelo Suporte Tecnico BipDesk", "/f").Run()
		}()

	default:
		return fmt.Errorf("tipo de comando desconhecido ou fora do enum autorizado: %s", cmd.Type)
	}

	return nil
}

// ----------------- Obtenção da Chave do Servidor (ADR-004) -----------------

func fetchServerKey(serverURL string) (string, error) {
	client := createHTTPClient(10 * time.Second)
	resp, err := client.Get(fmt.Sprintf("%s/api/server-key", strings.TrimRight(serverURL, "/")))
	if err != nil {
		return "", err
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return "", fmt.Errorf("status HTTP %d", resp.StatusCode)
	}

	var res ServerKeyResponse
	if err := json.NewDecoder(resp.Body).Decode(&res); err != nil {
		return "", err
	}
	return res.ServerKey, nil
}

// ----------------- AutoSetup e Instalação (ADR-008: Sem Auto-Elevação Silenciosa) -----------------

func runAutoSetup(serverURL, enrollToken string) (string, string, error) {
	// 1. Verificação de privilégios de Administrador (ADR-008: Elevação explícita pelo instalador, sem auto-elevação UAC via PowerShell)
	if !isUserAdmin() {
		showMessageBox("BipDesk Host - Privilégios Insuficientes",
			"A instalação do BipDesk Host requer permissões de Administrador do sistema.\n\nPor favor, execute o instalador clicando com o botão direito e selecionando 'Executar como Administrador'.",
			MB_ICONERROR)
		return "", "", fmt.Errorf("privilégios de Administrador requeridos")
	}

	installDir := `C:\Program Files\BipDesk`
	_ = os.MkdirAll(installDir, 0755)

	sessionExe := filepath.Join(installDir, "BipDesk_Session.exe")
	agentExe := filepath.Join(installDir, "bipdesk-agent.exe")
	iconFile := filepath.Join(installDir, "icon.ico")

	// 2. Se o BipDesk_Session.exe não existe, baixa do servidor
	if _, err := os.Stat(sessionExe); os.IsNotExist(err) {
		showMessageBox("BipDesk Host", "Iniciando instalação do BipDesk Host...\n\nO motor de conexão será baixado e configurado com verificação de integridade.", MB_ICONINFORMATION)
		if err := downloadFile(serverURL+"/downloads/BipDesk_Session.exe", sessionExe, ""); err != nil {
			return "", "", fmt.Errorf("falha ao baixar BipDesk_Session.exe: %w", err)
		}
	}

	// 3. Baixa ícone se não existir
	if _, err := os.Stat(iconFile); os.IsNotExist(err) {
		_ = downloadFile(serverURL+"/downloads/icon.ico", iconFile, "")
	}

	// 4. Copia o próprio agente para a pasta do programa
	currentExe, err := os.Executable()
	if err == nil && !strings.EqualFold(currentExe, agentExe) {
		_ = copyFile(currentExe, agentExe)
	}

	// 5. Obtém a chave pública do servidor de forma segura (sem chave hardcoded em código-fonte)
	serverKey := os.Getenv("BIPDESK_SERVER_KEY")
	if serverKey == "" {
		fetchedKey, err := fetchServerKey(serverURL)
		if err == nil && fetchedKey != "" {
			serverKey = fetchedKey
		}
	}

	// Extrai host do serverURL para configurar rendezvous
	serverHost := strings.TrimPrefix(serverURL, "https://")
	serverHost = strings.TrimPrefix(serverHost, "http://")
	if idx := strings.Index(serverHost, "/"); idx != -1 {
		serverHost = serverHost[:idx]
	}
	if idx := strings.Index(serverHost, ":"); idx != -1 {
		serverHost = serverHost[:idx]
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

	// Grava nos perfis necessários
	appData := os.Getenv("APPDATA")
	targetDirs := []string{
		filepath.Join(appData, "RustDesk", "config"),
		`C:\Windows\ServiceProfiles\LocalService\AppData\Roaming\RustDesk\config`,
		`C:\Windows\System32\config\systemprofile\AppData\Roaming\RustDesk\config`,
	}
	for _, dir := range targetDirs {
		_ = os.MkdirAll(dir, 0755)
		_ = os.WriteFile(filepath.Join(dir, "RustDesk2.toml"), []byte(tomlConfig), 0644)

		rdToml := filepath.Join(dir, "RustDesk.toml")
		if b, err := os.ReadFile(rdToml); err == nil {
			s := string(b)
			if strings.Contains(s, "rendezvous_server") || !strings.Contains(s, "key_pair") {
				_ = os.Remove(rdToml)
			}
		}
	}

	if serverKey != "" {
		_ = exec.Command(sessionExe, "--config", fmt.Sprintf("host=%s,key=%s", serverHost, serverKey)).Run()
	} else {
		_ = exec.Command(sessionExe, "--config", fmt.Sprintf("host=%s", serverHost)).Run()
	}
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
		return "", "", fmt.Errorf("não foi possível obter o ID do BipDesk Session")
	}

	// 7. Registra na API (Enrollment obrigatório)
	hostname, _ := os.Hostname()
	enrollPayload := map[string]string{
		"token":       enrollToken,
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
	if err != nil {
		return "", "", fmt.Errorf("erro ao criar requisição de enrollment: %w", err)
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return "", "", fmt.Errorf("falha de conexão no enrollment: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		return "", "", fmt.Errorf("enrollment rejeitado com HTTP %d", resp.StatusCode)
	}

	var enrollResp EnrollResponse
	if err := json.NewDecoder(resp.Body).Decode(&enrollResp); err != nil {
		return "", "", fmt.Errorf("erro ao decodificar resposta de enrollment: %w", err)
	}

	// Salva o token gerado pelo servidor protegido por DPAPI
	if err := saveMachineToken(enrollResp.MachineToken); err != nil {
		log.Printf("[AVISO] Falha ao salvar token da máquina: %v\n", err)
	}

	// 8. Registra Tarefa Agendada no Windows para iniciar com o sistema
	schArgs := fmt.Sprintf(`schtasks /create /tn "BipDeskHostAgent" /tr "\"%s\" --server \"%s\" --id \"%s\"" /sc onstart /ru "SYSTEM" /f`, agentExe, serverURL, machineID)
	exec.Command("cmd.exe", "/c", schArgs).Run()

	// 9. Cria atalho no Desktop com o ícone
	createDesktopShortcut(sessionExe, iconFile)

	// 10. Exibe mensagem de confirmação transparente para o usuário
	showMessageBox("BipDesk Host Ativo",
		fmt.Sprintf("BipDesk Host instalado e configurado com sucesso!\n\nID desta Máquina: %s\n\nEsta máquina está devidamente registrada no painel de suporte.", machineID),
		MB_ICONINFORMATION)

	return machineID, enrollResp.MachineToken, nil
}

func getLoggedInUser() string {
	u, err := user.Current()
	if err == nil && u.Username != "" {
		parts := strings.Split(u.Username, "\\")
		return parts[len(parts)-1]
	}
	return os.Getenv("USERNAME")
}

func getLocalIP() string {
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

	ifaces, err := net.Interfaces()
	if err == nil {
		for _, iface := range ifaces {
			if (iface.Flags&net.FlagUp) == 0 || (iface.Flags&net.FlagLoopback) != 0 {
				continue
			}
			name := strings.ToLower(iface.Name)
			if strings.Contains(name, "virtual") || strings.Contains(name, "vethernet") ||
				strings.Contains(name, "wsl") {
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

func sendHeartbeat(serverURL string, clientID string, machineToken string, httpClient *http.Client) error {
	payload := HeartbeatPayload{
		RustdeskID:   clientID,
		LoggedInUser: getLoggedInUser(),
		IPAddress:    getLocalIP(),
		CPUUsage:     getCPUUsage(),
		RAMUsage:     getRAMUsage(),
		DiskFreeGB:   getDiskFreeGB(),
		CPUModel:     getCPUModel(),
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
	req.Header.Set("User-Agent", "BipDesk-Host-Agent/2.4")
	if machineToken != "" {
		req.Header.Set("X-Machine-Token", machineToken)
	}

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
				if err := executeCommand(cmd, clientID, machineToken); err != nil {
					log.Printf("[ALERTA SEGURANCA] Comando %d rejeitado: %v\n", cmd.ID, err)
				}
			}
		}
	}

	return nil
}

func main() {
	serverURL := flag.String("server", "https://bipdesk.duckdns.org", "URL do servidor BipDesk")
	clientID := flag.String("id", "", "ID local do BipDesk Session")
	tokenFlag := flag.String("token", "", "Token de autenticação da máquina (ADR-001)")
	enrollTokenFlag := flag.String("enroll-token", "", "Token de enrollment de uso único")
	intervalSec := flag.Int("interval", 30, "Intervalo do heartbeat em segundos")
	flag.Parse()

	// Prioridade do token: 1. Flag CLI, 2. Arquivo DPAPI seguro
	machineToken := *tokenFlag
	if machineToken == "" {
		machineToken = loadMachineToken()
	}

	if *clientID != "" {
		if cleaned := extractCleanID(*clientID); cleaned != "" {
			*clientID = cleaned
		}
	}

	if *clientID == "" {
		if runtime.GOOS == "windows" {
			enrollTk := *enrollTokenFlag
			if enrollTk == "" {
				enrollTk = os.Getenv("BIPDESK_ENROLL_TOKEN")
			}
			id, tk, err := runAutoSetup(*serverURL, enrollTk)
			if err != nil {
				showMessageBox("BipDesk Host - Erro", fmt.Sprintf("Não foi possível configurar automaticamente o BipDesk Host:\n%v", err), MB_ICONERROR)
				log.Fatalf("[ERRO] AutoSetup falhou: %v", err)
			}
			*clientID = id
			if tk != "" {
				machineToken = tk
			}
		} else {
			log.Fatal("[ERRO] Parametro --id e obrigatorio em sistemas nao-Windows.")
		}
	}

	log.Printf("[BipDesk Host Agent Enterprise] Iniciado. ID: %s | Servidor: %s | OS: %s\n", *clientID, *serverURL, runtime.GOOS)

	httpClient := createHTTPClient(10 * time.Second)

	ticker := time.NewTicker(time.Duration(*intervalSec) * time.Second)
	defer ticker.Stop()

	// Envio imediato do primeiro heartbeat
	if err := sendHeartbeat(*serverURL, *clientID, machineToken, httpClient); err != nil {
		log.Printf("[AVISO] Falha no primeiro heartbeat: %v\n", err)
	} else {
		log.Println("[OK] Primeiro heartbeat entregue com sucesso.")
	}

	for range ticker.C {
		if err := sendHeartbeat(*serverURL, *clientID, machineToken, httpClient); err != nil {
			log.Printf("[AVISO] Falha ao enviar heartbeat: %v\n", err)
		}
	}
}
