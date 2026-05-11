using Weave.Shared;

namespace Weave.Cli.Shell;

internal static class CliApiHttp
{
    public static string ResolveBaseUrl() =>
        Environment.GetEnvironmentVariable("WEAVE_API_URL") ?? $"http://localhost:{WeavePorts.SiloHttp}";
}
