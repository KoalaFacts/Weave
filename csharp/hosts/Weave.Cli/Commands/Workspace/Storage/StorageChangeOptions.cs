namespace Weave.Cli.Commands;

internal sealed record StorageChangeOptions(string? Backend, string? ConnectionString, bool Migrate);
