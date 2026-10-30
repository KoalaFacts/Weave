namespace Weave.Cli.Shell;

internal interface IConfigStore
{
    CliConfig Load();

    void Save(CliConfig config);

    bool Exists();
}
