using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal sealed class LocalCliInitializationStore(string? error) : ILocalDeploymentStore
{
    public List<(string Directory, string Documents, string? Host, string Workspace, int Port)> Preparations { get; } = [];
    public List<(string Directory, LocalDeployment Deployment)> Completions { get; } = [];
    public LocalSetupResult Prepare(string directory, string documents, string? host, string workspace, int port)
    {
        Preparations.Add((directory, documents, host, workspace, port));
        if (error is not null)
            return new(null, error);
        Directory.CreateDirectory(Path.Join(directory, "private"));
        return new(new LocalDeployment(host.ShouldNotBeNull(), documents, workspace, port), null);
    }
    public bool CompleteInitialization(string directory, LocalDeployment deployment)
    {
        Completions.Add((directory, deployment));
        return true;
    }
    public string OperatorKey(string directory) => LocalHostRecordingStore.OperatorSecret;
    public string SigningKey(string directory) => LocalHostRecordingStore.SigningSecret;
    public LocalSetupResult Load(string directory) => throw new InvalidOperationException("Initialization must not load a retained deployment.");
}
