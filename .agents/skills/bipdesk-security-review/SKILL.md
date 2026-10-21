---
name: bipdesk-security-review
description: Comprehensive security review checklist, code patterns (Go, Python, C#), and pull request auditing guidelines for BipDesk remote access software. Use when conducting security audits, code reviews on PRs, implementing remote command handlers, machine authentication, or checking for secret leakage.
---

# BipDesk Security Review Skill

Esta skill padroniza os procedimentos de auditoria de código, conformidade de segurança e revisão de pull requests para todos os componentes do ecossistema BipDesk (Agente Go, API FastAPI e Console WPF .NET 8).

---

## 1. Checklist de Segurança Obrigatório (Critérios de Aceite)

Ao revisar qualquer alteração de código ou pull request, verifique item por item:

- [ ] **Zero Segredos no Working Tree**: Nenhuma chave privada (`BEGIN PRIVATE KEY`), credencial literal, token estático ou chave pública hardcoded.
- [ ] **Validação de Testes Automatizados**: `go test ./...` e `pytest` passam com 100% de sucesso.
- [ ] **Autenticação Obrigatória no Heartbeat**: Endpoint `/api/agent/heartbeat` rejeita requisições sem o cabeçalho `X-Machine-Token` (HTTP 401) ou com token adulterado/revogado (HTTP 403).
- [ ] **Enum Fechado de Comandos**: Nenhum comando arbitrário permitido. Comandos remotos aceitam apenas `cad`, `restart_spooler`, `flush_dns`, `lock`, `reboot`. Qualquer ação fora do enum é rejeitada na API e no agente.
- [ ] **Assinatura e Proteção Anti-Replay**: Todo comando recebido pelo agente deve validar assinatura HMAC-SHA256, nonce de uso único e carimbo de data/hora (< 60s).
- [ ] **Rate Limiting Ativo**: Endpoints `/api/login`, `/api/agent/enroll` e `/api/agent/heartbeat` retornam HTTP 429 quando os limites de requisição por minuto são excedidos.
- [ ] **Fail-Fast de Segredos**: A API não inicializa em produção sem `SECRET_KEY` e `ADMIN_PASSWORD` explicitamente configurados.
- [ ] **Validação TLS Estrita**: O código do agente não contém `InsecureSkipVerify`. O console C# não contém `ServerCertificateCustomValidationCallback` com bypass.
- [ ] **Ausência de Shell Arbitrário**: O agente não contém a ação `cmd` nem invocações de `cmd.exe` com parâmetros dinâmicos da rede.
- [ ] **Isolamento de Contêineres**: `docker-compose.yml` não usa `network_mode: host` e não expõe a porta interna da API (8080) externamente.
- [ ] **Regras de Firewall Mínimas**: `liberar-firewall.ps1` não libera portas desnecessárias (como 8080/UDP).
- [ ] **Transparência com Usuário (LGPD)**: Sem recursos furtivos de ocultação (`applyStealthWhiteLabel`). O usuário deve receber notificação visual de sessões ativas.

---

## 2. Padrões de Código por Linguagem

### 2.1 Padrões em Go (Agente Cliente)

#### Verificação de Assinatura e Nonce
```go
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
```

#### Execução de Comandos com Argumentos Vetorizados (Sem Concatenação de Shell)
```go
// CORRETO: argumentos separados diretamente para o binário do sistema
exec.Command("net.exe", "stop", "spooler").Run()

// PROIBIDO: shell interpolação com risco de injeção
exec.Command("cmd.exe", "/c", "net stop "+input).Run()
```

---

### 2.2 Padrões em Python / FastAPI (Backend Server)

#### Fail-Fast de Variáveis de Ambiente
```python
def load_or_init_secret_key() -> str:
    env_secret = os.getenv("SECRET_KEY", "").strip()
    if env_secret:
        return env_secret
    if os.getenv("ENV", "production").lower() == "development":
        return secrets.token_urlsafe(32)
    raise RuntimeError("CRÍTICO: SECRET_KEY obrigatória não configurada no ambiente.")
```

#### Rotação de Criptografia com MultiFernet
```python
def get_cipher() -> MultiFernet:
    keys = load_or_init_fernet_keys()
    return MultiFernet([Fernet(k) for k in keys])

def rotate_encrypted_password(cipher_text: str) -> str:
    cipher = get_cipher()
    decrypted = cipher.decrypt(cipher_text.encode())
    return cipher.encrypt(decrypted).decode()
```

---

### 2.3 Padrões em C# / .NET 8 WPF (Console do Técnico)

#### HttpClient com Validação TLS Estrita
```csharp
// CORRETO: Validação de certificado da cadeia nativa do Windows
var handler = new HttpClientHandler
{
    CheckCertificateRevocationList = false
};
_http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

// PROIBIDO: Bypass de TLS
ServerCertificateCustomValidationCallback = (m, c, ch, e) => true;
```

#### Tratamento Seguro de Janelas Embutidas (Win32 Interop)
```csharp
var parentRes = SetParent(_childHwnd, hwndParent.Handle);
if (parentRes == IntPtr.Zero)
{
    // Fallback gracioso para janela flutuante se houver restrição UAC
    var origStyle = GetWindowLong(_childHwnd, GWL_STYLE);
    origStyle = (origStyle & ~WS_CHILD) | WS_POPUP | WS_CAPTION | WS_THICKFRAME;
    SetWindowLong(_childHwnd, GWL_STYLE, origStyle);
    ShowWindow(_childHwnd, 5);
    return new HandleRef(this, IntPtr.Zero);
}
```

---

## 3. Guia de Revisão de Pull Request

Ao analisar o diff de um Pull Request:
1. **Audite os Imports e Dependências**: Verifique se foram adicionadas bibliotecas externas desnecessárias ou pacotes sem versão pinada.
2. **Procure por Segredos Acidentais**: Execute `git diff` procurando por strings longas em Base64, tokens de teste fixos ou IPs internos.
3. **Analise o Tratamento de Erros**: Certifique-se de que exceções não exibem stack traces nem senhas no console ou logs.
4. **Valide a Concorrência no Banco**: Em rotas que alteram status de máquinas, garanta que transações usem locks ou `commit()` atômico.
5. **Execute a Suíte Completa**: Garanta que os testes em Go e Python continuem verdes antes de aprovar o merge.
