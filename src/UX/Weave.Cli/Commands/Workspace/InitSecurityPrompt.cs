using Spectre.Console;

namespace Weave.Cli.Commands;

[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is injected for CLI testability.")]
internal sealed class InitSecurityPrompt
{
    public InitSecuritySelection Prompt()
    {
        AnsiConsole.WriteLine();
        CliTheme.WriteSection("Step 4 · Security");
        AnsiConsole.MarkupLine("API authentication protects your agents and data from unauthorized access.");
        AnsiConsole.WriteLine();

        var authChoice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("API authentication:")
                .Styled()
                .AddChoices(
                    "none     — no auth, open access (local dev only)",
                    "apikey   — require X-Api-Key header on every request",
                    "bearer   — require Authorization: Bearer token on every request"));

        var authMode = authChoice.Split(' ')[0].Trim();
        var authSecret = authMode is "apikey" or "bearer" ? PromptSecret() : null;
        var requireHttps = authMode is not "none" && AnsiConsole.Confirm("Require HTTPS?", defaultValue: false);

        return new InitSecuritySelection(authMode, authSecret, requireHttps);
    }

    private static string PromptSecret()
    {
        var authSecretMethod = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Where should the API secret come from?")
                .Styled()
                .AddChoices(
                    "env      — environment variable",
                    "file     — protected file on disk",
                    "vault    — HashiCorp Vault",
                    "generate — generate a random key now"));

        var authMethod = authSecretMethod.Split(' ')[0].Trim();
        return authMethod switch
        {
            "env" => ConfigureEnvSecret(),
            "file" => ConfigureFileSecret(),
            "vault" => ConfigureVaultSecret(),
            _ => ConfigureGeneratedSecret()
        };
    }

    private static string ConfigureEnvSecret()
    {
        var exists = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WEAVE_API_SECRET"));
        if (exists)
        {
            CliTheme.WriteSuccess("WEAVE_API_SECRET is set.");
        }
        else
        {
            CliTheme.WriteInfo("Set before starting:");
            CliTheme.WriteMuted("  export WEAVE_API_SECRET=\"your-secret-key\"");
        }

        return "env:WEAVE_API_SECRET";
    }

    private static string ConfigureFileSecret()
    {
        var defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave", "api.secret");
        var secretPath = AnsiConsole.Prompt(new TextPrompt<string>("Path to secret file:").Styled().DefaultValue(defaultPath));

        if (!File.Exists(secretPath))
        {
            CliTheme.WriteInfo("Create the file with your API secret:");
            CliTheme.WriteMuted($"  openssl rand -hex 32 > {secretPath}");
            CliTheme.WriteMuted($"  chmod 600 {secretPath}");
        }

        return $"file:{secretPath}";
    }

    private static string ConfigureVaultSecret()
    {
        var vaultPath = AnsiConsole.Prompt(
            new TextPrompt<string>("Vault secret path:").Styled().DefaultValue("secret/data/weave/api-key"));
        return $"vault:{vaultPath}";
    }

    private static string ConfigureGeneratedSecret()
    {
        var generated = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        CliTheme.WriteSuccess("Generated API key. Set it before starting:");
        CliTheme.WriteMuted($"  export WEAVE_API_SECRET=\"{generated}\"");
        return "env:WEAVE_API_SECRET";
    }
}
