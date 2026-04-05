# Security

The Security subsystem provides capability tokens, leak scanning, secret proxy, and vault integration. It enforces fail-closed behavior — operations are blocked when security checks fail.

## Projects

| Project | Purpose |
|---------|---------|
| `Weave.Security` | Tokens, scanning, proxy, vault provider, grains |
| `Weave.Security.Tests` | Tests for all components |

## Capability Tokens

HMAC-SHA256 signed tokens that gate access to tools and secrets.

### Interface

```csharp
public interface ICapabilityTokenService
{
    CapabilityToken Mint(CapabilityTokenRequest request);
    bool Validate(CapabilityToken token);
    void Revoke(string tokenId);
    bool IsRevoked(string tokenId);
}
```

### Token Model

```csharp
record CapabilityToken {
    string TokenId;              // GUID-based
    string WorkspaceId;
    string IssuedTo;
    HashSet<string> Grants;      // e.g. "tool:git", "secret:*"
    DateTimeOffset IssuedAt;
    DateTimeOffset ExpiresAt;
    string Signature;            // Base64 HMACSHA256
}
```

### Key Behaviors

- **Minting**: creates signed token with workspace, issuer, grants, and lifetime (default: 24h).
- **Validation**: checks expiration, revocation status, and signature integrity using fixed-time comparison.
- **Revocation**: in-memory + file-based persistence for durability.
- **Grant matching**: supports specific grants (`tool:git`) and wildcards (`tool:*`).
- **Signing key**: configurable via `CapabilityTokens` config section. Defaults to a development key.

### Request Model

```csharp
record CapabilityTokenRequest {
    string WorkspaceId;
    string IssuedTo;
    string[] Grants;
    TimeSpan Lifetime;           // Default: 24 hours
}
```

## Leak Scanner

Detects secrets in tool I/O using pattern matching and entropy analysis.

### Interface

```csharp
public interface ILeakScanner
{
    Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> payload, ScanContext context, CancellationToken ct);
    Task<ScanResult> ScanStringAsync(string content, ScanContext context, CancellationToken ct);
}
```

### Detection Methods

#### Pattern Matching (15 regex patterns)

| Pattern | Example |
|---------|---------|
| `aws_access_key` | `AKIA[0-9A-Z]{16}` |
| `aws_secret_key` | AWS secret key pattern |
| `github_token` | `ghp_*` |
| `github_pat` | `github_pat_*` |
| `openai_key` | `sk-[A-Za-z0-9]{20,}` |
| `anthropic_key` | `sk-ant-[A-Za-z0-9\-]{20,}` |
| `slack_token` | `xox[baprs]-*` |
| `slack_webhook` | `https://hooks.slack.com/...` |
| `bearer_token` | `Bearer eyJ...` |
| `jwt_token` | JWT pattern |
| `private_key_pem` | `-----BEGIN RSA PRIVATE KEY-----` |
| `database_connection` | Connection string pattern |
| `basic_auth` | Basic auth credentials |
| `azure_storage_key` | Azure storage key pattern |
| `generic_api_key` | API key header pattern |

#### Shannon Entropy Analysis

- Threshold: 4.5 bits
- Minimum string length: 20 characters (max: 500)
- Uses `stackalloc` for ASCII frequency counting
- Flags high-entropy strings as `high_entropy_string`

### Result Models

```csharp
record ScanResult {
    bool HasLeaks;
    List<LeakFinding> Findings;
    static ScanResult Clean { get; }
}

record LeakFinding {
    string PatternName;
    string Description;
    int Offset;
    int Length;
}

record ScanContext {
    string WorkspaceId;
    string SourceComponent;
    ScanDirection Direction;     // Inbound or Outbound
}
```

## Secret Proxy

Middleware for placeholder substitution and leak scanning in tool I/O.

### TransparentSecretProxy

```csharp
RegisterSecret(string placeholder, SecretValue secret);
UnregisterSecret(string placeholder);
string SubstitutePlaceholders(string content);     // Replaces {secret:path} patterns
Task<ScanResult> ScanResponseAsync(string content, string workspaceId);
Task<ScanResult> ScanRequestAsync(string content, string workspaceId);
```

- Regex-based replacement of `{secret:X}` patterns with actual values.
- Delegates to `LeakScanner` for scanning.
- Unregistered placeholders are left as-is (not replaced).

### SecretProxyGrain

**Key**: `{workspaceId}` — Orleans grain for workspace-scoped secret management.

```csharp
public interface ISecretProxyGrain : IGrainWithStringKey
{
    Task<string> RegisterSecretAsync(string secretPath, CapabilityToken token);
    Task UnregisterSecretAsync(string secretPath);
    Task<string> SubstituteAsync(string content);
}
```

- Validates capability tokens before all operations.
- Coordinates with `ISecretProvider` to resolve actual secret values.

## Secret Providers

### Interface

```csharp
public interface ISecretProvider
{
    Task<SecretValue> ResolveAsync(string secretPath, CapabilityToken token, CancellationToken ct);
    Task<IReadOnlyList<string>> ListPathsAsync(string workspaceId, CancellationToken ct);
}
```

Both providers validate tokens and check grants (`secret:{path}` or `secret:*`).

### VaultSecretProvider

HTTP-based HashiCorp Vault integration (no VaultSharp SDK).

- Mount path: `/v1/weave/{workspaceId}/data/{secretPath}`
- Extracts value from `data.data.value` in JSON response
- Lists paths via `LIST` HTTP method with `?list=true`
- Activated when Vault plugin is connected

### InMemorySecretProvider

Development/testing provider using `ConcurrentDictionary`.

- `SetSecret(path, value)` for test setup
- Same token validation as `VaultSecretProvider`

### SecretProviderProxy

Plugin-aware delegation:
- Checks `PluginServiceBroker.Get<ISecretProvider>()` first
- Falls back to `InMemorySecretProvider` if no plugin active
- Transparent to consumers

## Security Flow in Tool Invocation

```
Agent calls tool
    ↓
ToolGrain.InvokeAsync()
    ├── Validate CapabilityToken (grants, expiry, signature)
    ├── SubstituteAsync() via SecretProxyGrain ({secret:path} → real values)
    ├── Scan outbound payload (LeakScanner)
    │   └── Block + publish ToolInvocationBlockedEvent if leak found
    ├── Execute tool via connector
    ├── Scan inbound response (LeakScanner)
    │   └── Redact if leak found (success → false)
    └── Publish ToolInvocationCompletedEvent
```

## Configuration

| Setting | Section | Default |
|---------|---------|---------|
| Signing key | `CapabilityTokens:SigningKey` | `weave-development-signing-key-change-me` |
| Revocation dir | `CapabilityTokens:RevocationDirectory` | System temp path |
| Vault address | `Vault:Address` | (none — auto-detected) |
| Vault token | `VAULT_TOKEN` env | (optional) |

## Testing

- **CapabilityTokenServiceTests** (16 tests): minting, validation, expiration, revocation, signature tampering, wildcard grants
- **LeakScannerTests**: all 15 patterns, Shannon entropy, byte payloads, multiple leaks
- **TransparentSecretProxyTests**: registration, substitution, unregistration, scan methods
- **VaultSecretProviderTests**: HTTP mocking, token validation, path extraction
- **InMemorySecretProviderTests**: CRUD operations, token validation
- **SecretProviderProxyTests**: plugin delegation, fallback behavior
- **SecretProxyGrainTests**: grain lifecycle, token validation
