using Spectre.Console;
using Weave.Shared;

namespace Weave.Cli.Commands;

internal sealed class StorageShowCliCommand(StorageBackendService? storage = null) : ICliCommand<NoCliOptions>
{
    private readonly StorageBackendService _storage = storage ?? new StorageBackendService();

    public string Name => "show";

    public IReadOnlyList<string> Aliases => [];

    public string Description => "Show the current storage configuration";

    public Task<int> ExecuteAsync(NoCliOptions options, CancellationToken ct)
    {
        var config = CliConfigStore.Load();

        CliTheme.WriteSection("Storage Configuration");
        CliTheme.WriteKeyValue("Backend", config.Storage);

        if (!string.IsNullOrWhiteSpace(config.ConnectionString))
        {
            if (config.ConnectionString.StartsWith("env:", StringComparison.OrdinalIgnoreCase))
                CliTheme.WriteKeyValue("Connection", $"{config.ConnectionString} (from environment variable)");
            else if (config.ConnectionString.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                CliTheme.WriteKeyValue("Connection", $"{config.ConnectionString} (from secret file)");
            else if (config.ConnectionString.StartsWith("vault:", StringComparison.OrdinalIgnoreCase))
                CliTheme.WriteKeyValue("Connection", $"{config.ConnectionString} (from HashiCorp Vault)");
            else
                CliTheme.WriteKeyValue("Connection", _storage.MaskConnectionString(config.ConnectionString) + " [yellow](inline — not recommended)[/]");
        }
        else
        {
            CliTheme.WriteKeyValue("Connection", config.Storage == "memory" ? "(none — in-memory)" : "(not configured)");
        }

        if (config.Storage == "sqlite" && string.IsNullOrWhiteSpace(config.ConnectionString))
            CliTheme.WriteKeyValue("Default DB", _storage.DefaultSqlitePath());

        AnsiConsole.WriteLine();
        CliTheme.WriteMuted("  Change with: weave storage change <backend>");
        return Task.FromResult(0);
    }
}
