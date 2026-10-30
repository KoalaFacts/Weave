namespace Weave.Cli.Commands;

internal sealed record InitSecuritySelection(string AuthMode, string? AuthSecret, bool RequireHttps);
