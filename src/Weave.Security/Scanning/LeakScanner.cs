using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Weave.Security.Scanning;

public sealed partial class LeakScanner : ILeakScanner
{
    private readonly ILogger<LeakScanner> _logger;

    private static readonly (string Name, string Description, Regex Pattern)[] Patterns =
    [
        ("aws_access_key", "AWS Access Key ID", AwsAccessKey()),
        ("aws_secret_key", "AWS Secret Access Key", AwsSecretKey()),
        ("github_token", "GitHub Token", GitHubToken()),
        ("github_pat", "GitHub Personal Access Token", GitHubPat()),
        ("openai_key", "OpenAI API Key", OpenAiKey()),
        ("anthropic_key", "Anthropic API Key", AnthropicKey()),
        ("slack_token", "Slack Token", SlackToken()),
        ("slack_webhook", "Slack Webhook URL", SlackWebhook()),
        ("generic_api_key", "Generic API Key Header", GenericApiKey()),
        ("bearer_token", "Bearer Token", BearerToken()),
        ("jwt_token", "JSON Web Token", JwtToken()),
        ("private_key_pem", "PEM Private Key Block", PrivateKeyPem()),
        ("connection_string", "Database Connection String", ConnectionString()),
        ("basic_auth", "Basic Auth Credentials", BasicAuth()),
        ("azure_storage_key", "Azure Storage Account Key", AzureStorageKey()),
    ];

    private const double EntropyThreshold = 4.5;
    private const int MinHighEntropyLength = 20;

    public LeakScanner(ILogger<LeakScanner> logger)
    {
        _logger = logger;
    }

    public Task<ScanResult> ScanAsync(ReadOnlyMemory<byte> payload, ScanContext context, CancellationToken ct = default)
    {
        var content = Encoding.UTF8.GetString(payload.Span);
        return ScanStringAsync(content, context, ct);
    }

    public Task<ScanResult> ScanStringAsync(string content, ScanContext context, CancellationToken ct = default)
    {
        var findings = new List<LeakFinding>();

        foreach (var (name, description, pattern) in Patterns)
        {
            ct.ThrowIfCancellationRequested();

            foreach (Match match in pattern.Matches(content))
            {
                findings.Add(new LeakFinding
                {
                    PatternName = name,
                    Description = description,
                    Offset = match.Index,
                    Length = match.Length
                });
            }
        }

        // Shannon entropy analysis for high-entropy strings
        ScanForHighEntropyStrings(content, findings);

        if (findings.Count > 0)
        {
            LogLeaksDetected(findings.Count, context.Direction, context.SourceComponent, context.WorkspaceId);
        }

        var result = new ScanResult
        {
            HasLeaks = findings.Count > 0,
            Findings = findings
        };

        return Task.FromResult(result);
    }

    private static void ScanForHighEntropyStrings(string content, List<LeakFinding> findings)
    {
        // Split on whitespace and common delimiters to find token-like strings
        var tokens = content.Split([' ', '\n', '\r', '\t', '"', '\'', '=', ':', ',', ';'],
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            if (token.Length < MinHighEntropyLength || token.Length > 500)
                continue;

            var entropy = CalculateShannonEntropy(token);
            if (entropy >= EntropyThreshold)
            {
                findings.Add(new LeakFinding
                {
                    PatternName = "high_entropy_string",
                    Description = $"High entropy string detected (entropy: {entropy:F2})",
                    Offset = content.IndexOf(token, StringComparison.Ordinal),
                    Length = token.Length
                });
            }
        }
    }

    public static double CalculateShannonEntropy(string input)
    {
        // Use stackalloc for ASCII frequency counting (covers all printable chars)
        Span<int> freq = stackalloc int[128];
        var nonAsciiCount = 0;

        foreach (var c in input)
        {
            if (c < 128)
                freq[c]++;
            else
                nonAsciiCount++;
        }

        double entropy = 0;
        var len = (double)input.Length;

        for (var i = 0; i < 128; i++)
        {
            if (freq[i] > 0)
            {
                var p = freq[i] / len;
                entropy -= p * Math.Log2(p);
            }
        }

        // Treat all non-ASCII chars as a single bucket (rare in secrets)
        if (nonAsciiCount > 0)
        {
            var p = nonAsciiCount / len;
            entropy -= p * Math.Log2(p);
        }

        return entropy;
    }

    // 15+ regex patterns for secret detection
    [GeneratedRegex(@"AKIA[0-9A-Z]{16}", RegexOptions.Compiled)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(@"(?:aws_secret_access_key|AWS_SECRET_ACCESS_KEY)\s*[=:]\s*[A-Za-z0-9/+=]{40}", RegexOptions.Compiled)]
    private static partial Regex AwsSecretKey();

    [GeneratedRegex(@"gh[pousr]_[A-Za-z0-9_]{36,255}", RegexOptions.Compiled)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"github_pat_[A-Za-z0-9_]{22,255}", RegexOptions.Compiled)]
    private static partial Regex GitHubPat();

    [GeneratedRegex(@"sk-[A-Za-z0-9]{20,}", RegexOptions.Compiled)]
    private static partial Regex OpenAiKey();

    [GeneratedRegex(@"sk-ant-[A-Za-z0-9\-]{20,}", RegexOptions.Compiled)]
    private static partial Regex AnthropicKey();

    [GeneratedRegex(@"xox[baprs]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled)]
    private static partial Regex SlackToken();

    [GeneratedRegex(@"https://hooks\.slack\.com/services/T[A-Z0-9]+/B[A-Z0-9]+/[A-Za-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex SlackWebhook();

    [GeneratedRegex(@"(?i)(?:api[_-]?key|apikey)\s*[=:]\s*[""']?([A-Za-z0-9\-_]{16,})[""']?", RegexOptions.Compiled)]
    private static partial Regex GenericApiKey();

    [GeneratedRegex(@"[Bb]earer\s+[A-Za-z0-9\-_\.]{20,}", RegexOptions.Compiled)]
    private static partial Regex BearerToken();

    [GeneratedRegex(@"eyJ[A-Za-z0-9\-_]+\.eyJ[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+", RegexOptions.Compiled)]
    private static partial Regex JwtToken();

    [GeneratedRegex(@"-----BEGIN (?:RSA |EC |DSA |OPENSSH )?PRIVATE KEY-----", RegexOptions.Compiled)]
    private static partial Regex PrivateKeyPem();

    [GeneratedRegex(@"(?i)(?:connection\s*string|connstr|sqlconnection)\s*[=:]\s*[""']?[^""'\s]{20,}[""']?", RegexOptions.Compiled)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(@"(?i)basic\s+[A-Za-z0-9+/=]{20,}", RegexOptions.Compiled)]
    private static partial Regex BasicAuth();

    [GeneratedRegex(@"(?i)(?:AccountKey|account_key)\s*=\s*[A-Za-z0-9+/=]{40,}", RegexOptions.Compiled)]
    private static partial Regex AzureStorageKey();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Leak scanner found {Count} potential secret(s) in {Direction} payload from {Source} in workspace {Workspace}")]
    private partial void LogLeaksDetected(int count, ScanDirection direction, string source, string workspace);
}
