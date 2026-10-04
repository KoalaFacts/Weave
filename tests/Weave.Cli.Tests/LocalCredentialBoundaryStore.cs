using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal sealed class LocalCredentialBoundaryStore : ILocalDeploymentStore
{
    public int OperatorReads { get; private set; }
    public int SigningReads { get; private set; }

    public string OperatorKey(string directory)
    {
        OperatorReads++;
        throw new InvalidOperationException("credential boundary sentinel");
    }

    public string SigningKey(string directory)
    {
        SigningReads++;
        throw new InvalidOperationException("signing boundary sentinel");
    }

    public LocalSetupResult Prepare(string directory, string documents, string? host, string workspace, int port) =>
        throw new NotSupportedException("Deployment setup is not part of a launcher boundary test.");

    public bool CompleteInitialization(string directory, LocalDeployment deployment) =>
        throw new NotSupportedException("Deployment initialization is not part of a launcher boundary test.");

    public LocalSetupResult Load(string directory) =>
        throw new NotSupportedException("Deployment loading is not part of a launcher boundary test.");
}
