namespace Weave.Cli.Shell;

internal interface ISecretResolver
{
    string? ResolveReference(string? reference);

    string ToEnvReference(string storageBackend);
}
