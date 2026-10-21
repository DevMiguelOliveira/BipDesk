package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"testing"
	"time"
)

func generateValidSignature(cmdType, clientID, nonce string, timestamp int64, machineToken string) string {
	tokenHashBytes := sha256.Sum256([]byte(machineToken))
	tokenHashHex := hex.EncodeToString(tokenHashBytes[:])

	msgToSign := fmt.Sprintf("%s:%s:%s:%d", cmdType, clientID, nonce, timestamp)
	mac := hmac.New(sha256.New, []byte(tokenHashHex))
	mac.Write([]byte(msgToSign))
	return hex.EncodeToString(mac.Sum(nil))
}

func TestVerifyCommandSignature(t *testing.T) {
	clientID := "123456789"
	machineToken := "sec_test_token_987654321_alpha_beta"
	nonce := "a1b2c3d4e5f67890"
	ts := time.Now().Unix()

	validSig := generateValidSignature("reboot", clientID, nonce, ts, machineToken)

	cmdValid := CommandItem{
		ID:        1,
		Type:      "reboot",
		Nonce:     nonce,
		Timestamp: ts,
		Signature: validSig,
	}

	if !verifyCommandSignature(cmdValid, clientID, machineToken) {
		t.Errorf("Esperado assinatura válida, mas foi rejeitada")
	}

	cmdTampered := cmdValid
	cmdTampered.Type = "cad"
	if verifyCommandSignature(cmdTampered, clientID, machineToken) {
		t.Errorf("Esperado assinatura adulterada ser rejeitada após troca de tipo de comando")
	}

	cmdWrongToken := cmdValid
	if verifyCommandSignature(cmdWrongToken, clientID, "wrong_token_here") {
		t.Errorf("Esperado rejeição com token de máquina diferente")
	}
}

func TestCommandTimestampExpiration(t *testing.T) {
	clientID := "123456789"
	machineToken := "test_token_expiration"
	nonce := "nonce_ts_test_01"
	expiredTs := time.Now().Unix() - 120 // 2 minutos atrás (limite é 60s)

	sig := generateValidSignature("flush_dns", clientID, nonce, expiredTs, machineToken)
	cmd := CommandItem{
		ID:        2,
		Type:      "flush_dns",
		Nonce:     nonce,
		Timestamp: expiredTs,
		Signature: sig,
	}

	err := executeCommand(cmd, clientID, machineToken)
	if err == nil {
		t.Errorf("Esperado erro ao executar comando com timestamp expirado")
	}
}

func TestReplayAttackNonceRejection(t *testing.T) {
	nonce := "unique_nonce_for_replay_test"
	now := time.Now().Unix()

	if !isValidNonce(nonce, now) {
		t.Fatalf("Esperado primeira utilização de nonce ser válida")
	}

	// Segunda utilização do mesmo nonce dentro da janela temporal
	if isValidNonce(nonce, now) {
		t.Errorf("Esperado segunda utilização do mesmo nonce ser rejeitada (proteção anti-replay)")
	}
}

func TestClosedEnumRejection(t *testing.T) {
	clientID := "123456789"
	machineToken := "test_token_enum"
	nonce := "nonce_enum_test_01"
	now := time.Now().Unix()

	// Tentativa de executar comando arbitrário fora do enum fechado (ex: "cmd", "exec", "powershell")
	unauthorizedCmds := []string{"cmd", "exec", "powershell", "bash", "download_and_run"}

	for _, cmdType := range unauthorizedCmds {
		sig := generateValidSignature(cmdType, clientID, nonce+"_"+cmdType, now, machineToken)
		cmd := CommandItem{
			ID:        99,
			Type:      cmdType,
			Payload:   "whoami",
			Nonce:     nonce + "_" + cmdType,
			Timestamp: now,
			Signature: sig,
		}

		err := executeCommand(cmd, clientID, machineToken)
		if err == nil {
			t.Errorf("Esperado erro ao executar comando fora do enum fechado: %s", cmdType)
		}
	}
}

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
