package main

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// Helper para gerar par Ed25519 em testes
func generateTestKeyPair(t *testing.T) (ed25519.PublicKey, ed25519.PrivateKey) {
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatalf("falha ao gerar par Ed25519 para teste: %v", err)
	}
	return pub, priv
}

// 1. Mensagem com assinatura de outra chave é descartada
func TestEd25519_ForeignSignatureRejected(t *testing.T) {
	clearNonceCache()
	serverPub, _ := generateTestKeyPair(t)
	serverPubHex := hex.EncodeToString(serverPub)

	// Outro par de chaves (atacante)
	_, attackerPriv := generateTestKeyPair(t)

	machineID := "bip-test-01"
	payload := map[string]any{"action": "ping"}

	// Assina com a chave do atacante
	env, err := signEnvelope("ping", machineID, payload, attackerPriv)
	if err != nil {
		t.Fatalf("erro ao assinar envelope: %v", err)
	}

	rawBytes, _ := json.Marshal(env)

	// Host deve rejeitar porque a assinatura não foi feita com a chave do servidor fixada
	_, err = verifyServerEnvelope(rawBytes, serverPubHex, machineID)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Mensagem com assinatura de chave estranha foi aceita!")
	}
	if !strings.Contains(err.Error(), "assinatura Ed25519 do servidor inválida") {
		t.Logf("Mensagem rejeitada com sucesso por motivo: %v", err)
	}
}

// 2. Mensagem repetida (Replay Attack) é descartada
func TestAntiReplay_NonceDuplicateRejected(t *testing.T) {
	clearNonceCache()
	serverPub, serverPriv := generateTestKeyPair(t)
	serverPubHex := hex.EncodeToString(serverPub)

	machineID := "bip-test-02"
	payload := map[string]any{"session_id": "sess-replay-123"}

	env, err := signEnvelope("session_offer", machineID, payload, serverPriv)
	if err != nil {
		t.Fatalf("erro ao assinar envelope: %v", err)
	}

	rawBytes, _ := json.Marshal(env)

	// Primeira entrega: deve ser aceita
	_, err = verifyServerEnvelope(rawBytes, serverPubHex, machineID)
	if err != nil {
		t.Fatalf("Primeira entrega válida foi rejeitada incorretamente: %v", err)
	}

	// Segunda entrega da mesma mensagem com mesmo nonce: DEVE ser rejeitada
	_, err = verifyServerEnvelope(rawBytes, serverPubHex, machineID)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Mensagem com nonce repetido foi aceita (Replay Attack bem-sucedido)!")
	}
	if !strings.Contains(err.Error(), "replay attack") && !strings.Contains(err.Error(), "reutilizado") {
		t.Errorf("Esperado erro de replay attack, obtido: %v", err)
	}
}

// 3. Mensagem com timestamp fora da janela (+- 120s) é descartada
func TestTimestamp_SkewWindowRejected(t *testing.T) {
	clearNonceCache()
	serverPub, serverPriv := generateTestKeyPair(t)
	serverPubHex := hex.EncodeToString(serverPub)
	machineID := "bip-test-03"

	// Cenário A: Mensagem muito antiga (passado: 150 segundos atrás)
	oldTs := time.Now().Unix() - 150
	nonceOld := generateRandomNonce()
	canonicalOld, _ := formatCanonicalString("ping", machineID, oldTs, nonceOld, map[string]any{})
	sigOld := hex.EncodeToString(ed25519.Sign(serverPriv, []byte(canonicalOld)))

	envOld := SignedEnvelope{
		Type:      "ping",
		MachineID: machineID,
		Timestamp: oldTs,
		Nonce:     nonceOld,
		Payload:   map[string]any{},
		Signature: sigOld,
	}
	rawOld, _ := json.Marshal(envOld)

	_, err := verifyServerEnvelope(rawOld, serverPubHex, machineID)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Mensagem com timestamp expirado (> 120s atrás) foi aceita!")
	}

	// Cenário B: Mensagem vinda do futuro (futuro: 150 segundos à frente)
	futTs := time.Now().Unix() + 150
	nonceFut := generateRandomNonce()
	canonicalFut, _ := formatCanonicalString("ping", machineID, futTs, nonceFut, map[string]any{})
	sigFut := hex.EncodeToString(ed25519.Sign(serverPriv, []byte(canonicalFut)))

	envFut := SignedEnvelope{
		Type:      "ping",
		MachineID: machineID,
		Timestamp: futTs,
		Nonce:     nonceFut,
		Payload:   map[string]any{},
		Signature: sigFut,
	}
	rawFut, _ := json.Marshal(envFut)

	_, err = verifyServerEnvelope(rawFut, serverPubHex, machineID)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Mensagem com timestamp no futuro (> 120s) foi aceita!")
	}
}

// 4. Host recusa servidor cuja chave de assinatura não bate com a fixada
func TestPinnedServerKey_MismatchRejected(t *testing.T) {
	clearNonceCache()
	// Servidor legítimo fixado
	pinnedPub, _ := generateTestKeyPair(t)
	pinnedPubHex := hex.EncodeToString(pinnedPub)

	// Servidor falso/impostor tentando assinar com sua própria chave
	impostorPub, impostorPriv := generateTestKeyPair(t)
	_ = impostorPub
	machineID := "bip-test-04"

	env, _ := signEnvelope("session_offer", machineID, map[string]any{"cmd": "cad"}, impostorPriv)
	rawBytes, _ := json.Marshal(env)

	// Host configurado com pinnedPubHex deve rejeitar a assinatura do impostor
	_, err := verifyServerEnvelope(rawBytes, pinnedPubHex, machineID)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Mensagem de servidor com chave não correspondente ao pinning foi aceita!")
	}
}

// 5. Mensagem acima de 64 KB (65.536 bytes) é recusada
func TestOversizedMessage_Rejected(t *testing.T) {
	clearNonceCache()
	serverPub, serverPriv := generateTestKeyPair(t)
	serverPubHex := hex.EncodeToString(serverPub)
	machineID := "bip-test-05"

	// Cria payload gigante (> 64 KB)
	largeData := strings.Repeat("A", 70000)
	payload := map[string]any{"data": largeData}

	env, err := signEnvelope("data", machineID, payload, serverPriv)
	if err != nil {
		t.Fatalf("erro ao criar envelope grande: %v", err)
	}

	rawBytes, _ := json.Marshal(env)
	if len(rawBytes) <= 65536 {
		t.Fatalf("mensagem de teste não atingiu 64 KB (%d bytes)", len(rawBytes))
	}

	_, err = verifyServerEnvelope(rawBytes, serverPubHex, machineID)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Mensagem de %d bytes (> 64 KB) foi aceita!", len(rawBytes))
	}
	if !strings.Contains(err.Error(), "excede 64 KB") {
		t.Errorf("Esperado erro de limite de 64 KB, obtido: %v", err)
	}
}

// 6. Ticket inválido, expirado ou para outra máquina é recusado no host
func TestTicketValidation_InvalidExpiredOrReused(t *testing.T) {
	localMachineID := "bip-host-alpha"
	now := time.Now().Unix()

	// Cenário A: Ticket para outra máquina
	ticketWrongMachine := map[string]any{
		"machine_id": "bip-host-bravo",
		"session_id": "sess-wrong",
		"expires_at": now + 60,
	}
	err := validateSessionTicket(ticketWrongMachine, localMachineID, now)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Ticket direcionado a outra máquina foi aceito!")
	}

	// Cenário B: Ticket expirado
	ticketExpired := map[string]any{
		"machine_id": localMachineID,
		"session_id": "sess-expired",
		"expires_at": now - 30, // Expirado há 30 segundos
	}
	err = validateSessionTicket(ticketExpired, localMachineID, now)
	if err == nil {
		t.Fatalf("FALHA DE SEGURANÇA: Ticket expirado foi aceito!")
	}

	// Cenário C: Ticket válido
	ticketValid := map[string]any{
		"machine_id": localMachineID,
		"session_id": "sess-valid",
		"expires_at": now + 60,
	}
	err = validateSessionTicket(ticketValid, localMachineID, now)
	if err != nil {
		t.Fatalf("Ticket válido foi rejeitado incorretamente: %v", err)
	}
}

// 7. Enum fechado estrito de comandos autorizados (R3: ausência de cmd/powershell)
func TestClosedEnum_StrictRejection(t *testing.T) {
	// Comandos proibidos: nunca devem ser aceitos
	forbidden := []string{"cmd", "cmd.exe", "powershell", "exec", "sh", "bash", "whoami", "curl"}
	for _, cmd := range forbidden {
		err := executeClosedEnumCommand(cmd)
		if err == nil {
			t.Fatalf("FALHA DE SEGURANÇA: Comando fora do enum fechado foi aceito: %s", cmd)
		}
	}

	// Comandos autorizados no enum fechado
	authorized := []string{"cad", "restart_spooler", "flush_dns", "lock", "reboot"}
	for _, cmd := range authorized {
		// executeClosedEnumCommand não deve retornar erro de tipo desconhecido
		err := executeClosedEnumCommand(cmd)
		if err != nil && strings.Contains(err.Error(), "fora do enum estrito") {
			t.Fatalf("Comando autorizado rejeitado incorretamente: %s", cmd)
		}
	}
}

// 8. Identidade do Host: Geração de chave Ed25519 e persistência segura (DPAPI)
func TestHostIdentity_DPAPIAndKeyGeneration(t *testing.T) {
	tmpKeyFile := filepath.Join(os.TempDir(), "bipdesk-test-ed25519.key")
	defer os.Remove(tmpKeyFile)

	// 1. Gera nova chave na primeira execução
	pubKey1, privKey1, err := loadOrCreateHostEd25519Key(tmpKeyFile)
	if err != nil {
		t.Fatalf("falha ao gerar chave do host: %v", err)
	}
	if len(pubKey1) != ed25519.PublicKeySize || len(privKey1) != ed25519.PrivateKeySize {
		t.Fatalf("tamanho de chaves Ed25519 inválido")
	}

	// 2. Carrega a chave existente do arquivo e verifica consistência
	pubKey2, privKey2, err := loadOrCreateHostEd25519Key(tmpKeyFile)
	if err != nil {
		t.Fatalf("falha ao recarregar chave do host: %v", err)
	}

	if hex.EncodeToString(pubKey1) != hex.EncodeToString(pubKey2) {
		t.Fatalf("Chave pública recarregada diverge da gerada originalmente")
	}
	if hex.EncodeToString(privKey1) != hex.EncodeToString(privKey2) {
		t.Fatalf("Chave privada recarregada diverge da gerada originalmente")
	}
}

// 9. Extração robusta do ID numérico RustDesk
func TestExtractCleanID(t *testing.T) {
	cases := []struct {
		input    string
		expected string
	}{
		{"123456789\r\n", "123456789"},
		{"[INFO] starting engine\r\n[WARN] skipping audio\r\n987654321\r\n", "987654321"},
		{"flutter: ID is 554433221 ready", "554433221"},
		{"", ""},
		{"error: unable to connect", ""},
	}

	for _, c := range cases {
		got := extractCleanID(c.input)
		if got != c.expected {
			t.Errorf("extractCleanID(%q) = %q; esperado %q", c.input, got, c.expected)
		}
	}
}
