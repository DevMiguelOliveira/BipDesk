# Plano de Migração do Console Técnico: .NET 8 para .NET 10 (LTS)

## 1. Contexto e Ciclo de Vida do .NET
O console do técnico (`tech-app`) foi construído sobre a plataforma **.NET 8 (LTS)** em C# / WPF.

De acordo com a política oficial de ciclo de vida da Microsoft:
- **.NET 8 (LTS)**: Lançado em novembro de 2023, seu suporte oficial de longo prazo encerra em **10 de novembro de 2026**.
- **.NET 9 (STS)**: Lançamento de curto prazo (Standard Term Support), com encerramento de suporte previsto para **maio de 2026**.
- **.NET 10 (LTS)**: Próximo lançamento de longo prazo, com lançamento previsto para **novembro de 2025** e suporte estendido até **novembro de 2028**.

> **Diretriz Arquitetural:** Para manter a estabilidade corporativa e suporte ininterrupto, o projeto BipDesk **não** deve migrar para versões STS intermediárias (.NET 9), devendo planejar a transição direta de **.NET 8 LTS** para **.NET 10 LTS** antes do marco de novembro de 2026.

---

## 2. Cronograma de Transição Recomendado

| Marco | Janela Temporal | Objetivo |
|---|---|---|
| **M1: Avaliação de Pré-Requisitos** | Q1 2026 | Testar compilação do `BipDesk.Tech.csproj` com os previews do .NET 10 SDK. |
| **M2: Atualização de Pacotes NuGet** | Q2 2026 | Atualizar `Microsoft.Extensions.*` e dependências para versões compatíveis com .NET 10. |
| **M3: Validação de Interop Win32/WPF** | Q3 2026 | Homologar o wrapper `EmbeddedSessionHost` (`SetParent`, `SetWindowPos`, `SetWindowLong`) e Single-File publish no novo runtime. |
| **M4: Virada em Produção** | Q4 2026 (até Out/2026) | Promover release oficial compilada em .NET 10 antes do EOL do .NET 8 (10/11/2026). |

---

## 3. Passos Técnicos de Execução

1. **Atualização do Target Framework**:
   No arquivo `tech-app/BipDesk.Tech.csproj`:
   ```xml
   <!-- Antes -->
   <TargetFramework>net8.0-windows</TargetFramework>
   
   <!-- Depois -->
   <TargetFramework>net10.0-windows</TargetFramework>
   ```

2. **Verificação de Single-File e AOT/Trimming**:
   Verificar se flags de compilação única continuam compatíveis:
   ```xml
   <PublishSingleFile>true</PublishSingleFile>
   <SelfContained>true</SelfContained>
   <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
   ```

3. **Atualização do Workflow CI/CD**:
   No arquivo `.github/workflows/build-tech-app.yml`:
   ```yaml
   - name: Setup .NET SDK
     uses: actions/setup-dotnet@...
     with:
       dotnet-version: '10.0.x'
   ```
