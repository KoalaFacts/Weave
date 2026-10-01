namespace Weave.Cli.Tests;

internal sealed class LocalTestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "weave-local-tests-" + Guid.NewGuid().ToString("N"));
    public string Private => Path.Combine(Root, ".weave");
    public string Documents => Path.Combine(Root, "documents");
    public string Host => Path.Combine(Root, OperatingSystem.IsWindows() ? "Weave.Silo.exe" : "Weave.Silo");

    public LocalTestDirectory()
    {
        Directory.CreateDirectory(Documents);
        File.WriteAllText(Host, "fixture only; never executed");
    }

    public void Dispose()
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(Path.GetTempPath()), comparison))
            throw new InvalidOperationException("Test cleanup escaped its temporary root.");
        Directory.Delete(Root, recursive: true);
    }
}
