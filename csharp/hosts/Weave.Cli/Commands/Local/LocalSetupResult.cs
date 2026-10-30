namespace Weave.Cli.Commands.Local;

internal sealed record LocalSetupResult(LocalDeployment? Deployment, string? Error);
