namespace Weave.Cli.Shell;

internal static class WorkspaceSiloPaths
{
    public static string GetSiloLogPath()
    {
        var weaveHome = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".weave");
        return Path.Combine(weaveHome, "silo.log");
    }
}
