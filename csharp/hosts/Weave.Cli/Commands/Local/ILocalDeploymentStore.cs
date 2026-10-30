namespace Weave.Cli.Commands.Local;

internal interface ILocalDeploymentStore
{
    LocalSetupResult Prepare(string directory, string documents, string? host, string workspace, int port);
    bool CompleteInitialization(string directory, LocalDeployment deployment);
    LocalSetupResult Load(string directory);
    string OperatorKey(string directory);
    string SigningKey(string directory);
}
