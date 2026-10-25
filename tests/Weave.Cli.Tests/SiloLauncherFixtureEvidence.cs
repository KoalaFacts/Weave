using System.Globalization;
using System.Text;

namespace Weave.Cli.Tests;

internal static class SiloLauncherFixtureEvidence
{
    public static string DirectoryFor(Type testClass)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "Weave.slnx")))
            repository = repository.Parent;
        repository.ShouldNotBeNull("Launcher evidence must be retained under the checkout's existing TestResults artifact path.");
        return Path.Join(repository.FullName, "TestResults", "silo-launcher", testClass.Name);
    }

    public static string Save(string root, string directory, int? group, int? exitCode, string stdout, string stderr, bool stopped)
    {
        var evidence = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Root: {root}")
            .AppendLine(CultureInfo.InvariantCulture, $"Group: {group}")
            .AppendLine(CultureInfo.InvariantCulture, $"Test host exit: {exitCode}")
            .AppendLine(CultureInfo.InvariantCulture, $"Owned group stopped: {stopped}")
            .AppendLine("=== test host stdout ===").AppendLine(stdout)
            .AppendLine("=== test host stderr ===").AppendLine(stderr);
        try
        {
            foreach (var name in new[] { "worker.pid", "worker.args", "worker.lifecycle", "health-requests", ".weave/silo.log" })
            {
                evidence.AppendLine(CultureInfo.InvariantCulture, $"=== {name} (last 32768 bytes) ===");
                var path = Path.Join(root, name);
                if (!File.Exists(path))
                {
                    evidence.AppendLine("not produced");
                    continue;
                }
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var length = (int)Math.Min(stream.Length, 32768);
                stream.Seek(-length, SeekOrigin.End);
                var bytes = new byte[length];
                var count = stream.ReadAtLeast(bytes, length, throwOnEndOfStream: false);
                evidence.AppendLine(Encoding.UTF8.GetString(bytes, 0, count));
            }
            Directory.CreateDirectory(directory);
            var destination = Path.Join(directory, Path.GetFileName(root) + ".log");
            File.WriteAllText(destination + ".pending", evidence.ToString());
            File.Move(destination + ".pending", destination);
            return $"Launcher fixture evidence: {destination}{Environment.NewLine}{evidence}";
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return $"Evidence persistence failed: {failure.GetType().Name}: {failure.Message}{Environment.NewLine}{evidence}";
        }
    }
}
