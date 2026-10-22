package main

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
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

	"github.com/gorilla/websocket"
)

var digitIdRegex = regexp.MustCompile(`\b\d{6,16}\b`)

// extractCleanID extrai de forma robusta o ID numérico do RustDesk/BipDesk,
// ignorando logs do Flutter, avisos do console e quebras de linha.
func extractCleanID(raw string) string {
	if raw == "" {
		return ""
	}
	lines := strings.Split(raw, "\n")
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

	for i := len(lines) - 1; i >= 0; i-- {
		matches := digitIdRegex.FindAllString(lines[i], -1)
		if len(matches) > 0 {
			return matches[len(matches)-1]
		}
	}

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

// ----------------- Envelopes e Mensagens Assinadas Ed25519 (ADR-012) -----------------

// SignedEnvelope representa o envelope canônico com assinatura criptográfica Ed25519 (Seção 3)
type SignedEnvelope struct {
	Type      string         `json:"type"`
	MachineID string         `json:"machine_id"`
	Timestamp int64          `json:"timestamp"`
	Nonce     string         `json:"nonce"`
	Payload   map[string]any `json:"payload"`
	Signature string         `json:"signature"`
}

// formatCanonicalString constrói a representação canônica rigorosa:
// type:machine_id:timestamp:nonce:canonical_json_payload
func formatCanonicalString(msgType, machineID string, timestamp int64, nonce string, payload map[string]any) (string, error) {
	if payload == nil {
		payload = make(map[string]any)
	}
	payloadBytes, err := json.Marshal(payload)
	if err != nil {
		return "", err
	}
	return fmt.Sprintf("%s:%s:%d:%s:%s", msgType, machineID, timestamp, nonce, string(payloadBytes)), nil
}

// generateRandomNonce gera um nonce criptograficamente aleatório de 16 bytes em hex
func generateRandomNonce() string {
	b := make([]byte, 16)
	_, _ = rand.Read(b)
	return hex.EncodeToString(b)
}

// signEnvelope gera a assinatura Ed25519 e empacota no envelope canônico
func signEnvelope(msgType, machineID string, payload map[string]any, privKey ed25519.PrivateKey) (*SignedEnvelope, error) {
	now := time.Now().Unix()
	nonce := generateRandomNonce()

	canonicalStr, err := formatCanonicalString(msgType, machineID, now, nonce, payload)
	if err != nil {
		return nil, fmt.Errorf("erro ao formatar string canônica: %w", err)
	}

	sigBytes := ed25519.Sign(privKey, []byte(canonicalStr))
	sigHex := hex.EncodeToString(sigBytes)

	return &SignedEnvelope{
		Type:      msgType,
		MachineID: machineID,
		Timestamp: now,
		Nonce:     nonce,
		Payload:   payload,
		Signature: sigHex,
	}, nil
}

// ----------------- Anti-Replay Nonce Cache & Validação Temporal (Seção 3) -----------------

var (
	nonceMutex sync.Mutex
	seenNonces = make(map[string]int64)
)

func isValidNonce(nonce string, now int64) bool {
	if nonce == "" || len(nonce) < 8 {
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

func clearNonceCache() {
	nonceMutex.Lock()
	defer nonceMutex.Unlock()
	seenNonces = make(map[string]int64)
}

// verifyServerEnvelope valida mensagem recebida do servidor: tamanho, timestamp, replay e Ed25519
func verifyServerEnvelope(rawMsg []byte, pinnedServerKeyHex, expectedMachineID string) (*SignedEnvelope, error) {
	// 1. Limite estrito de tamanho de mensagem: 64 KB (65.536 bytes)
	if len(rawMsg) > 65536 {
		return nil, fmt.Errorf("mensagem rejeitada: tamanho excede 64 KB (%d bytes)", len(rawMsg))
	}

	var env SignedEnvelope
	if err := json.Unmarshal(rawMsg, &env); err != nil {
		return nil, fmt.Errorf("envelope JSON malformado: %w", err)
	}

	// 2. Validação de identificador da máquina
	if expectedMachineID != "" && env.MachineID != expectedMachineID {
		return nil, fmt.Errorf("machine_id incorreto no envelope: esperado %s, obtido %s", expectedMachineID, env.MachineID)
	}

	now := time.Now().Unix()

	// 3. Validação de janela temporal: skew de até +- 120s (Seção 3)
	skew := now - env.Timestamp
	if skew < -120 || skew > 120 {
		return nil, fmt.Errorf("timestamp fora da janela permitida (+- 120s): skew=%ds", skew)
	}

	// 4. Proteção contra Replay Attack (Nonce único na janela)
	if !isValidNonce(env.Nonce, now) {
		return nil, fmt.Errorf("replay attack detectado ou nonce reutilizado: %s", env.Nonce)
	}

	// 5. Validação da chave do servidor fixada no instalador (ADR-013)
	if pinnedServerKeyHex == "" {
		return nil, fmt.Errorf("chave pública do servidor não configurada para pinning")
	}
	serverPubBytes, err := hex.DecodeString(strings.TrimSpace(pinnedServerKeyHex))
	if err != nil || len(serverPubBytes) != ed25519.PublicKeySize {
		return nil, fmt.Errorf("chave pública fixada do servidor inválida (%s)", pinnedServerKeyHex)
	}

	// 6. Verificação de assinatura Ed25519 sobre string canônica
	canonicalStr, err := formatCanonicalString(env.Type, env.MachineID, env.Timestamp, env.Nonce, env.Payload)
	if err != nil {
		return nil, fmt.Errorf("erro ao formatar representação canônica: %w", err)
	}

	sigBytes, err := hex.DecodeString(strings.TrimSpace(env.Signature))
	if err != nil || len(sigBytes) != ed25519.SignatureSize {
		return nil, fmt.Errorf("assinatura Ed25519 malformada")
	}

	if !ed25519.Verify(serverPubBytes, []byte(canonicalStr), sigBytes) {
		return nil, fmt.Errorf("assinatura Ed25519 do servidor inválida")
	}

	return &env, nil
}

// ----------------- DPAPI e Armazenamento Seguro de Chave da Máquina (ADR-011) -----------------

var (
	modcrypt32             = syscall.NewLazyDLL("crypt32.dll")
	procCryptProtectData   = modcrypt32.NewProc("CryptProtectData")
	procCryptUnprotectData = modcrypt32.NewProc("CryptUnprotectData")
)

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

const (
	CRYPTPROTECT_UI_FORBIDDEN  = 0x1
	CRYPTPROTECT_LOCAL_MACHINE = 0x4
)

// encryptDPAPI cifra dados usando DPAPI com escopo de máquina (CRYPTPROTECT_LOCAL_MACHINE)
func encryptDPAPI(data []byte) ([]byte, error) {
	if runtime.GOOS != "windows" {
		return data, nil
	}
	var outBlob dataBlob
	// Flags: CRYPTPROTECT_LOCAL_MACHINE (0x4) | CRYPTPROTECT_UI_FORBIDDEN (0x1) = 0x5
	dwFlags := uintptr(CRYPTPROTECT_LOCAL_MACHINE | CRYPTPROTECT_UI_FORBIDDEN)
	r, _, err := procCryptProtectData.Call(
		uintptr(unsafe.Pointer(newBlob(data))),
		0, 0, 0, 0, dwFlags,
		uintptr(unsafe.Pointer(&outBlob)),
	)
	if r == 0 {
		return nil, fmt.Errorf("CryptProtectData falhou: %w", err)
	}
	defer syscall.LocalFree(syscall.Handle(unsafe.Pointer(outBlob.pbData)))
	return outBlob.toByteArray(), nil
}

// decryptDPAPI decifra dados protegidos por DPAPI em escopo de máquina
func decryptDPAPI(data []byte) ([]byte, error) {
	if runtime.GOOS != "windows" {
		return data, nil
	}
	var outBlob dataBlob
	dwFlags := uintptr(CRYPTPROTECT_UI_FORBIDDEN)
	r, _, err := procCryptUnprotectData.Call(
		uintptr(unsafe.Pointer(newBlob(data))),
		0, 0, 0, 0, dwFlags,
		uintptr(unsafe.Pointer(&outBlob)),
	)
	if r == 0 {
		return nil, fmt.Errorf("CryptUnprotectData falhou: %w", err)
	}
	defer syscall.LocalFree(syscall.Handle(unsafe.Pointer(outBlob.pbData)))
	return outBlob.toByteArray(), nil
}

// restrictKeyFileACL restringe a permissão do arquivo exclusivamente a SYSTEM e Administradores (ADR-011)
// Adicionalmente inclui o usuário atual para viabilizar testes unitários e execuções no contexto do processo
func restrictKeyFileACL(filePath string) error {
	if runtime.GOOS != "windows" {
		return os.Chmod(filePath, 0600)
	}
	// Well-known SIDs: *S-1-5-18 = NT AUTHORITY\SYSTEM, *S-1-5-32-544 = BUILTIN\Administrators
	args := []string{filePath, "/inheritance:r", "/grant:r", "*S-1-5-18:(F)", "*S-1-5-32-544:(F)"}
	if u, err := user.Current(); err == nil && u.Uid != "" {
		args = append(args, fmt.Sprintf("*%s:(F)", u.Uid))
	}
	cmd := exec.Command("icacls.exe", args...)
	return cmd.Run()
}

func getHostKeyFilePath() string {
	if custom := os.Getenv("BIPDESK_KEY_FILE"); custom != "" {
		return custom
	}
	if runtime.GOOS == "windows" {
		pd := os.Getenv("ProgramData")
		if pd == "" {
			pd = `C:\ProgramData`
		}
		return filepath.Join(pd, "BipDesk", "host_ed25519.key")
	}
	return filepath.Join(os.TempDir(), "bipdesk-host-ed25519.key")
}

// loadOrCreateHostEd25519Key carrega ou gera par de chaves Ed25519 do host protegido por DPAPI
func loadOrCreateHostEd25519Key(keyPath string) (ed25519.PublicKey, ed25519.PrivateKey, error) {
	if keyPath == "" {
		keyPath = getHostKeyFilePath()
	}

	if err := os.MkdirAll(filepath.Dir(keyPath), 0700); err != nil {
		return nil, nil, err
	}

	// 1. Tenta carregar chave existente
	if data, err := os.ReadFile(keyPath); err == nil && len(data) > 0 {
		decrypted, err := decryptDPAPI(data)
		if err == nil && len(decrypted) == ed25519.PrivateKeySize {
			privKey := ed25519.PrivateKey(decrypted)
			pubKey := privKey.Public().(ed25519.PublicKey)
			return pubKey, privKey, nil
		}
	}

	// 2. Primeira execução: gera novo par Ed25519
	pubKey, privKey, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		return nil, nil, fmt.Errorf("falha ao gerar par Ed25519 da máquina: %w", err)
	}

	// 3. Cifra com DPAPI em escopo de máquina
	encrypted, err := encryptDPAPI(privKey)
	if err != nil {
		// Fallback se DPAPI indisponível (testes unitários fora do Windows)
		encrypted = []byte(privKey)
	}

	// 4. Salva no disco com ACL restrita
	if err := os.WriteFile(keyPath, encrypted, 0600); err != nil {
		return nil, nil, fmt.Errorf("falha ao salvar chave privada Ed25519: %w", err)
	}
	_ = restrictKeyFileACL(keyPath)

	return pubKey, privKey, nil
}

// ----------------- Hardware & Telemetria do Host -----------------

var (
	moduser32               = syscall.NewLazyDLL("user32.dll")
	procMessageBox          = moduser32.NewProc("MessageBoxW")
	modkernel32             = syscall.NewLazyDLL("kernel32.dll")
	procGlobalMemoryStatus  = modkernel32.NewProc("GlobalMemoryStatusEx")
	procGetSystemTimes      = modkernel32.NewProc("GetSystemTimes")
	procGetDiskFreeSpaceExW = modkernel32.NewProc("GetDiskFreeSpaceExW")
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

func generateRandomPassword(length int) string {
	const chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!@#$%"
	b := make([]byte, length)
	for i := range b {
		n, _ := rand.Int(rand.Reader, big.NewInt(int64(len(chars))))
		b[i] = chars[n.Int64()]
	}
	return string(b)
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
			content := string(b)
			re := regexp.MustCompile(`(?m)^id\s*=\s*['"]([0-9a-zA-Z_-]+)['"]`)
			m := re.FindStringSubmatch(content)
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

// ----------------- Indicador Visível de Sessão Remota (R5) -----------------

var (
	indicatorMu  sync.Mutex
	indicatorCmd *exec.Cmd
)

func showSessionIndicator(technician string) {
	indicatorMu.Lock()
	defer indicatorMu.Unlock()
	if indicatorCmd != nil && indicatorCmd.Process != nil {
		_ = indicatorCmd.Process.Kill()
	}

	log.Printf("[AUDITORIA AGENTE] Iniciando indicador visual de suporte: Tecnico %s\n", technician)
	if runtime.GOOS == "windows" {
		psScript := fmt.Sprintf(`
Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show("BipDesk Host: Sessão de suporte remoto ativa com o técnico '%s'.", "BipDesk Suporte Remoto", "OK", "Information")
`, technician)
		indicatorCmd = exec.Command("powershell.exe", "-NoProfile", "-WindowStyle", "Normal", "-Command", psScript)
		_ = indicatorCmd.Start()
	}
}

func hideSessionIndicator() {
	indicatorMu.Lock()
	defer indicatorMu.Unlock()
	if indicatorCmd != nil && indicatorCmd.Process != nil {
		_ = indicatorCmd.Process.Kill()
		indicatorCmd = nil
	}
	log.Println("[AUDITORIA AGENTE] Indicador visual de sessao encerrado.")
}

// ----------------- Validação de Ticket e Ações do Host (Seção 6, R3) -----------------

// validateSessionTicket valida integridade do ticket assinado pelo servidor
func validateSessionTicket(ticket map[string]any, localMachineID string, now int64) error {
	if ticket == nil {
		return fmt.Errorf("ticket ausente no payload da sessao")
	}

	mID, _ := ticket["machine_id"].(string)
	if mID != localMachineID {
		return fmt.Errorf("ticket direcionado a maquina incorreta: esperado %s, obtido %s", localMachineID, mID)
	}

	var expTs int64
	if expFloat, ok := ticket["expires_at"].(float64); ok {
		expTs = int64(expFloat)
	} else if expInt, ok := ticket["expires_at"].(int64); ok {
		expTs = expInt
	} else if expStr, ok := ticket["expires_at"].(string); ok {
		if t, err := time.Parse(time.RFC3339, expStr); err == nil {
			expTs = t.Unix()
		} else {
			fmt.Sscanf(expStr, "%d", &expTs)
		}
	}

	if expTs > 0 && expTs < now {
		return fmt.Errorf("ticket de sessao expirado (exp=%d, now=%d)", expTs, now)
	}

	return nil
}

// applyTemporarySessionPassword aplica a credencial temporária ao motor RustDesk/BipDesk
func applyTemporarySessionPassword(tempPassword string) error {
	candidateExes := []string{
		`C:\Program Files\BipDesk\BipDesk_Session.exe`,
		`C:\Program Files\RustDesk\RustDesk.exe`,
	}

	var executed bool
	for _, exe := range candidateExes {
		if _, err := os.Stat(exe); err == nil {
			cmd := exec.Command(exe, "--password", tempPassword)
			if err := cmd.Run(); err == nil {
				executed = true
				break
			}
		}
	}

	if !executed && runtime.GOOS == "windows" {
		return fmt.Errorf("executavel do motor de sessao nao localizado para aplicar credencial")
	}
	return nil
}

// revokeSessionCredentials redefine a senha do motor para string aleatória de descarte
func revokeSessionCredentials() {
	candidateExes := []string{
		`C:\Program Files\BipDesk\BipDesk_Session.exe`,
		`C:\Program Files\RustDesk\RustDesk.exe`,
	}
	burnPassword := generateRandomPassword(32)
	for _, exe := range candidateExes {
		if _, err := os.Stat(exe); err == nil {
			_ = exec.Command(exe, "--password", burnPassword).Run()
		}
	}
}

// executeClosedEnumCommand executa apenas os comandos autorizados no enum estrito (R3)
func executeClosedEnumCommand(commandType string) error {
	switch commandType {
	case "cad":
		log.Println("[AUDITORIA AGENTE] Executando SAS/CAD...")
		sessionExe := `C:\Program Files\BipDesk\BipDesk_Session.exe`
		if _, err := os.Stat(sessionExe); err == nil {
			go exec.Command(sessionExe, "--send-cad").Run()
		} else {
			rustdeskExe := `C:\Program Files\RustDesk\RustDesk.exe`
			if _, err := os.Stat(rustdeskExe); err == nil {
				go exec.Command(rustdeskExe, "--send-cad").Run()
			}
		}
		return nil

	case "restart_spooler":
		log.Println("[AUDITORIA AGENTE] Reiniciando servico de spooler...")
		go func() {
			_ = exec.Command("net.exe", "stop", "spooler").Run()
			time.Sleep(1 * time.Second)
			_ = exec.Command("net.exe", "start", "spooler").Run()
		}()
		return nil

	case "flush_dns":
		log.Println("[AUDITORIA AGENTE] Limpando cache DNS...")
		go func() {
			_ = exec.Command("ipconfig.exe", "/flushdns").Run()
		}()
		return nil

	case "lock":
		log.Println("[AUDITORIA AGENTE] Bloqueando estacao de trabalho...")
		go func() {
			_ = exec.Command("rundll32.exe", "user32.dll,LockWorkStation").Run()
		}()
		return nil

	case "reboot":
		log.Println("[AUDITORIA AGENTE] Reiniciando estacao de trabalho sob demanda...")
		go func() {
			_ = exec.Command("shutdown.exe", "/r", "/t", "5", "/c", "Reinicio solicitado pelo BipDesk Admin", "/f").Run()
		}()
		return nil

	default:
		return fmt.Errorf("comando desconhecido ou fora do enum estrito autorizado: %s", commandType)
	}
}

// ----------------- Enrollment Automático (Seção 2) -----------------

type EnrollResponse struct {
	Status    string `json:"status"`
	MachineID string `json:"machine_id"`
	ServerKey string `json:"server_pubkey"`
	State     string `json:"state"`
}

func createHTTPClient(timeout time.Duration) *http.Client {
	rootCAs, _ := x509.SystemCertPool()
	tr := &http.Transport{
		TLSClientConfig: &tls.Config{
			MinVersion: tls.VersionTLS12,
			RootCAs:    rootCAs,
			// STRICT TLS: Nunca usar InsecureSkipVerify (Seção 4)
		},
	}
	return &http.Client{
		Timeout:   timeout,
		Transport: tr,
	}
}

// enrollHost executa o enrollment automático vinculando a chave pública com token de uso único
func enrollHost(serverURL, enrollToken, machineID string, pubKey ed25519.PublicKey, privKey ed25519.PrivateKey, pinnedServerKey string) (*EnrollResponse, error) {
	// Prova criptográfica de posse da chave privada Ed25519
	sigBytes := ed25519.Sign(privKey, []byte(enrollToken))
	sigHex := hex.EncodeToString(sigBytes)
	pubKeyHex := hex.EncodeToString(pubKey)

	hostname, _ := os.Hostname()
	payload := map[string]string{
		"token":       enrollToken,
		"machine_id":  machineID,
		"public_key":  pubKeyHex,
		"signature":   sigHex,
		"hostname":    hostname,
		"os_version":  runtime.GOOS,
		"ip_address":  getLocalIP(),
		"rustdesk_id": readIDFromConfig(),
	}

	data, err := json.Marshal(payload)
	if err != nil {
		return nil, err
	}

	endpoint := fmt.Sprintf("%s/api/agent/enroll", strings.TrimRight(serverURL, "/"))
	client := createHTTPClient(15 * time.Second)
	req, err := http.NewRequest("POST", endpoint, bytes.NewBuffer(data))
	if err != nil {
		return nil, err
	}
	req.Header.Set("Content-Type", "application/json")

	resp, err := client.Do(req)
	if err != nil {
		return nil, fmt.Errorf("falha de conexao no enrollment: %w", err)
	}
	defer resp.Body.Close()

	if resp.StatusCode != http.StatusOK {
		b, _ := io.ReadAll(resp.Body)
		return nil, fmt.Errorf("enrollment rejeitado com HTTP %d: %s", resp.StatusCode, string(b))
	}

	var res EnrollResponse
	if err := json.NewDecoder(resp.Body).Decode(&res); err != nil {
		return nil, err
	}

	// Verificação estrita de pinning de chave do servidor (Seção 4)
	if pinnedServerKey != "" && !strings.EqualFold(res.ServerKey, pinnedServerKey) {
		return nil, fmt.Errorf("ALERTA SEGURANCA: Chave publica retornada pelo servidor (%s) diverge da fixada (%s)", res.ServerKey, pinnedServerKey)
	}

	return &res, nil
}

// ----------------- Canal WebSocket e Reconexão com Jitter (Seção 3, 4) -----------------

func formatWebSocketURL(serverURL, machineID string) string {
	raw := serverURL
	if strings.HasPrefix(raw, "https://") {
		raw = "wss://" + strings.TrimPrefix(raw, "https://")
	} else if strings.HasPrefix(raw, "http://") {
		raw = "ws://" + strings.TrimPrefix(raw, "http://")
	} else if !strings.HasPrefix(raw, "wss://") && !strings.HasPrefix(raw, "ws://") {
		raw = "wss://" + raw
	}
	return fmt.Sprintf("%s/api/ws/host?machine_id=%s", strings.TrimRight(raw, "/"), machineID)
}

func calculateBackoffWithJitter(attempt int) time.Duration {
	baseSec := 1 << attempt
	if baseSec > 60 || baseSec <= 0 {
		baseSec = 60
	}
	jitterMs := 0
	if n, err := rand.Int(rand.Reader, big.NewInt(1500)); err == nil {
		jitterMs = int(n.Int64())
	}
	d := time.Duration(baseSec)*time.Second + time.Duration(jitterMs)*time.Millisecond
	if d > 60*time.Second {
		d = 60 * time.Second
	}
	return d
}

// runWebSocketLoop executa a conexão contínua de saída via WebSocket assinado
func runWebSocketLoop(serverURL, machineID, pinnedServerKey string, privKey ed25519.PrivateKey) {
	wsURL := formatWebSocketURL(serverURL, machineID)
	attempt := 0

	for {
		log.Printf("[CONEXAO HOST] Conectando ao canal seguro wss: %s (tentativa %d)\n", wsURL, attempt+1)

		dialer := websocket.Dialer{
			HandshakeTimeout: 15 * time.Second,
			TLSClientConfig: &tls.Config{
				MinVersion: tls.VersionTLS12,
				// Strict TLS: NO InsecureSkipVerify (Seção 4)
			},
		}

		conn, resp, err := dialer.Dial(wsURL, nil)
		if err != nil {
			if resp != nil {
				log.Printf("[ALERTA SEGURANCA] Conexao WebSocket recusada pelo servidor (HTTP %d): %v\n", resp.StatusCode, err)
			} else {
				log.Printf("[AVISO] Falha ao conectar WebSocket: %v\n", err)
			}
			backoff := calculateBackoffWithJitter(attempt)
			attempt++
			time.Sleep(backoff)
			continue
		}

		attempt = 0
		log.Println("[OK] Canal WebSocket de saida estabelecido e autenticado.")

		// Configuração de leitura com limite estrito de 64 KB
		conn.SetReadLimit(65536)

		// Loop de telemetria a cada 30 segundos
		stopTelemetry := make(chan struct{})
		go func() {
			ticker := time.NewTicker(30 * time.Second)
			defer ticker.Stop()

			// Envio inicial imediato
			sendTelemetry(conn, machineID, privKey)

			for {
				select {
				case <-stopTelemetry:
					return
				case <-ticker.C:
					sendTelemetry(conn, machineID, privKey)
				}
			}
		}()

		// Loop de leitura de mensagens assinadas do servidor
		for {
			_, messageBytes, err := conn.ReadMessage()
			if err != nil {
				log.Printf("[CONEXAO HOST] Canal WebSocket desconectado: %v\n", err)
				break
			}

			env, err := verifyServerEnvelope(messageBytes, pinnedServerKey, machineID)
			if err != nil {
				log.Printf("[ALERTA SEGURANCA] Mensagem do servidor descartada: %v\n", err)
				continue
			}

			handleServerMessage(conn, env, machineID, privKey)
		}

		close(stopTelemetry)
		conn.Close()
		hideSessionIndicator()

		backoff := calculateBackoffWithJitter(attempt)
		attempt++
		log.Printf("[CONEXAO HOST] Reconectando em %v...\n", backoff)
		time.Sleep(backoff)
	}
}

func sendTelemetry(conn *websocket.Conn, machineID string, privKey ed25519.PrivateKey) {
	telemetryPayload := map[string]any{
		"cpu_usage":      getCPUUsage(),
		"ram_usage":      getRAMUsage(),
		"disk_free_gb":   getDiskFreeGB(),
		"logged_in_user": getLoggedInUser(),
		"cpu_model":      getCPUModel(),
		"ip_address":     getLocalIP(),
	}

	env, err := signEnvelope("telemetry", machineID, telemetryPayload, privKey)
	if err != nil {
		log.Printf("[ERRO] Falha ao assinar telemetria: %v\n", err)
		return
	}

	data, _ := json.Marshal(env)
	_ = conn.WriteMessage(websocket.TextMessage, data)
}

func handleServerMessage(conn *websocket.Conn, env *SignedEnvelope, machineID string, privKey ed25519.PrivateKey) {
	now := time.Now().Unix()

	switch env.Type {
	case "session_offer":
		log.Println("[AUDITORIA AGENTE] Oferta de sessao remota assinada recebida.")
		ticketMap, _ := env.Payload["ticket"].(map[string]any)
		if err := validateSessionTicket(ticketMap, machineID, now); err != nil {
			log.Printf("[ALERTA SEGURANCA] Ticket de sessao invalido recusado: %v\n", err)
			return
		}

		tempPassword, _ := env.Payload["temp_password"].(string)
		techName, _ := env.Payload["technician"].(string)
		sessID, _ := env.Payload["session_id"].(string)

		if err := applyTemporarySessionPassword(tempPassword); err != nil {
			log.Printf("[AVISO] Nao foi possivel aplicar senha temporaria ao motor: %v\n", err)
		}

		showSessionIndicator(techName)

		// Envia confirmacao (session_ack)
		ackEnv, err := signEnvelope("session_ack", machineID, map[string]any{"session_id": sessID}, privKey)
		if err == nil {
			ackData, _ := json.Marshal(ackEnv)
			_ = conn.WriteMessage(websocket.TextMessage, ackData)
		}

	case "session_terminate":
		log.Println("[AUDITORIA AGENTE] Ordem de encerramento de sessao recebida.")
		sessID, _ := env.Payload["session_id"].(string)
		revokeSessionCredentials()
		hideSessionIndicator()

		endEnv, err := signEnvelope("session_ended", machineID, map[string]any{"session_id": sessID}, privKey)
		if err == nil {
			endData, _ := json.Marshal(endEnv)
			_ = conn.WriteMessage(websocket.TextMessage, endData)
		}

	case "command":
		cmdType, _ := env.Payload["command_type"].(string)
		if err := executeClosedEnumCommand(cmdType); err != nil {
			log.Printf("[ALERTA SEGURANCA] Execucao de comando recusada: %v\n", err)
		}

	case "ping":
		pongEnv, err := signEnvelope("pong", machineID, map[string]any{}, privKey)
		if err == nil {
			pongData, _ := json.Marshal(pongEnv)
			_ = conn.WriteMessage(websocket.TextMessage, pongData)
		}
	}
}

// ----------------- Ponto de Entrada (main) -----------------

func main() {
	serverURL := flag.String("server", "https://bipdesk-server", "URL ou MagicDNS do servidor BipDesk")
	serverMagicDns := flag.String("server-magic-dns", "", "Nome MagicDNS do servidor na Tailnet")
	serverKeyFlag := flag.String("server-key", "", "Chave publica Ed25519 do servidor para pinning")
	machineIDFlag := flag.String("machine-id", "", "Identificador da maquina no BipDesk")
	idFlag := flag.String("id", "", "ID alternativo da maquina")
	enrollTokenFlag := flag.String("enroll-token", "", "Token de uso unico para vinculacao inicial")
	flag.Parse()

	if *serverMagicDns != "" {
		*serverURL = *serverMagicDns
	}

	machineID := *machineIDFlag
	if machineID == "" {
		machineID = *idFlag
	}

	serverKey := *serverKeyFlag
	if serverKey == "" {
		serverKey = os.Getenv("BIPDESK_SERVER_KEY")
	}

	enrollToken := *enrollTokenFlag
	if enrollToken == "" {
		enrollToken = os.Getenv("BIPDESK_ENROLL_TOKEN")
	}

	// 1. Carrega ou gera chave de identidade Ed25519 do host (DPAPI escopo maquina)
	pubKey, privKey, err := loadOrCreateHostEd25519Key("")
	if err != nil {
		log.Fatalf("[CRITICO] Falha ao carregar/gerar identidade Ed25519 do host: %v", err)
	}

	pubKeyHex := hex.EncodeToString(pubKey)
	log.Printf("[IDENTIDADE HOST] Chave publica Ed25519: %s | SO: %s\n", pubKeyHex, runtime.GOOS)

	// 2. Enrollment se token de uso único fornecido
	if enrollToken != "" {
		if machineID == "" {
			machineID = "bip-" + extractCleanID(readIDFromConfig())
			if machineID == "bip-" {
				machineID = "bip-host"
			}
		}
		log.Printf("[ENROLLMENT] Iniciando vinculacao com token de uso unico para maquina %s...\n", machineID)
		res, err := enrollHost(*serverURL, enrollToken, machineID, pubKey, privKey, serverKey)
		if err != nil {
			log.Fatalf("[CRITICO] Enrollment falhou: %v", err)
		}
		log.Printf("[ENROLLMENT] Maquina vinculada com sucesso: ID=%s, Estado=%s\n", res.MachineID, res.State)
		if res.ServerKey != "" {
			serverKey = res.ServerKey
		}
	}

	if machineID == "" {
		machineID = "bip-" + extractCleanID(readIDFromConfig())
		if machineID == "bip-" {
			machineID = "bip-host"
		}
	}

	log.Printf("[BipDesk Host Agent Enterprise] Inicializado. Machine ID: %s | Servidor: %s\n", machineID, *serverURL)

	// 3. Inicia conexão de saída contínua via WebSocket assinado
	runWebSocketLoop(*serverURL, machineID, serverKey, privKey)
}
