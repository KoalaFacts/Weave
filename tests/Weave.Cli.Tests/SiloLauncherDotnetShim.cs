namespace Weave.Cli.Tests;

internal sealed class SiloLauncherDotnetShim : IDisposable
{
    private readonly string? _previousPath;

    public SiloLauncherDotnetShim(string root, string workerExecutable)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The owned dotnet shim requires Linux executable scripts.");
        var directory = Directory.CreateDirectory(Path.Join(root, "owned-dotnet-shim")).FullName;
        var shim = Path.Join(directory, "dotnet");
        File.Copy(workerExecutable, shim);
        File.SetUnixFileMode(shim, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _previousPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + _previousPath);
    }

    public void Dispose() => Environment.SetEnvironmentVariable("PATH", _previousPath);
}
