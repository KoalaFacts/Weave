using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Weave.Security.Scanning;
using Weave.Shared.Secrets;

namespace Weave.Security.Proxy;

/// <summary>
/// Middleware that intercepts HTTP traffic and:
/// 1. Replaces {secret:path} placeholders with real secret values at the network boundary
/// 2. Scans responses for leaked secrets
/// </summary>
public sealed partial class TransparentSecretProxy
{
    private readonly ConcurrentDictionary<string, SecretValue> _secretMapping = new();
    private readonly ILeakScanner _leakScanner;
    private readonly ILogger<TransparentSecretProxy> _logger;

    public TransparentSecretProxy(ILeakScanner leakScanner, ILogger<TransparentSecretProxy> logger)
    {
        _leakScanner = leakScanner;
        _logger = logger;
    }

    public void RegisterSecret(string placeholder, SecretValue secret)
    {
        _secretMapping[placeholder] = secret;
    }

    public void UnregisterSecret(string placeholder)
    {
        _secretMapping.TryRemove(placeholder, out _);
    }

    /// <summary>
    /// Substitute {secret:X} placeholders with real secret values in outbound content.
    /// </summary>
    public string SubstitutePlaceholders(string content)
    {
        return SecretPlaceholderParser.Substitute(content, path =>
        {
            if (_secretMapping.TryGetValue(path, out var secret))
                return secret.DecryptToString();

            LogPlaceholderNotRegistered(path);
            return null;
        });
    }

    /// <summary>
    /// Scan response content for potential secret leaks.
    /// </summary>
    public async Task<ScanResult> ScanResponseAsync(string content, string workspaceId, CancellationToken ct = default)
    {
        var context = new ScanContext
        {
            WorkspaceId = workspaceId,
            SourceComponent = "TransparentSecretProxy",
            Direction = ScanDirection.Inbound
        };

        return await _leakScanner.ScanStringAsync(content, context, ct);
    }

    /// <summary>
    /// Scan outbound request content for potential secret leaks.
    /// </summary>
    public async Task<ScanResult> ScanRequestAsync(string content, string workspaceId, CancellationToken ct = default)
    {
        var context = new ScanContext
        {
            WorkspaceId = workspaceId,
            SourceComponent = "TransparentSecretProxy",
            Direction = ScanDirection.Outbound
        };

        return await _leakScanner.ScanStringAsync(content, context, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Secret placeholder '{Path}' referenced but not registered")]
    private partial void LogPlaceholderNotRegistered(string path);
}
