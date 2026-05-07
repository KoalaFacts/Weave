namespace Weave.Cli.Shell;

internal sealed record UpdateCheckResult(
    string Current,
    string? Latest,
    DateTimeOffset CheckedAt,
    bool UpdateAvailable,
    string? Note);
