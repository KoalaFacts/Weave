using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal sealed class LocalHostRecordingStore(bool initializationResult = true) : ILocalDeploymentStore
{
    public const string OperatorSecret = "fixture-local-operator-key";
    public const string SigningSecret = "fixture-local-signing-key";
    public List<(string Directory, LocalDeployment Deployment)> Completions { get; } = [];

    public bool CompleteInitialization(string directory, LocalDeployment deployment)
    {
        Completions.Add((directory, deployment));
        return initializationResult;
    }

    public string OperatorKey(string directory) => OperatorSecret;
    public string SigningKey(string directory) => SigningSecret;
    public LocalSetupResult Prepare(string directory, string documents, string? host, string workspace, int port) =>
        throw new InvalidOperationException("Running a prepared deployment must not prepare it again.");
    public LocalSetupResult Load(string directory) =>
        throw new InvalidOperationException("Running a supplied deployment must not reload it.");
}
