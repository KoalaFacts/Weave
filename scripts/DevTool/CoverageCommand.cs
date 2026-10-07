using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml;

internal static class CoverageCommand
{
    private const string OutputDir = "TestResults";

    public static int Run(string[] args)
    {
        try
        {
            var threshold = 90m;
            var searchRoot = "tests";
            var skipCollect = false;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "-t" or "--threshold" when i + 1 < args.Length:
                        if (!decimal.TryParse(args[++i], NumberStyles.Number, CultureInfo.InvariantCulture, out threshold)
                            || threshold < 0 || threshold > 100)
                            throw new InvalidDataException("Coverage threshold must be a number from 0 through 100.");
                        break;
                    case "-r" or "--root" when i + 1 < args.Length:
                        searchRoot = args[++i];
                        break;
                    case "--skip-collect":
                        skipCollect = true;
                        break;
                    default:
                        throw new InvalidDataException($"Unknown or incomplete coverage option: {args[i]}");
                }
            }
            var inventory = CoverageInventory.Load(searchRoot);
            var collected = skipCollect || CollectCoverage(inventory);
            var result = new CoverageAnalyzer(inventory).Analyze(OutputDir, threshold);
            return result != 0 ? result : collected ? 0 : 1;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or XmlException
            or JsonException or InvalidOperationException or ArgumentException or KeyNotFoundException)
        {
            Console.Error.WriteLine($"Coverage evidence error: {exception.Message}");
            return 2;
        }
    }

    private static bool CollectCoverage(CoverageInventory inventory)
    {
        Directory.CreateDirectory(OutputDir);
        var successful = true;
        foreach (var (suite, project) in inventory.TestProjects.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var outputFile = Path.Combine(OutputDir, $"{suite}.cobertura.xml");
            File.Delete(outputFile); // Never accept an earlier successful run after a failed collection.
            Console.WriteLine($"Collecting {suite}...");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false };
            foreach (var argument in new[] { "dotnet-coverage", "collect", "-f", "cobertura", "-o", outputFile,
                "-l", Path.Combine(OutputDir, $"{suite}.collector.log"), "--", "dotnet", "test", "--project", project,
                "--no-build", "--no-restore", "-c", "Release" })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start collector for {suite}.");
            process.WaitForExit(); // Inherit stdout/stderr: no unread redirected pipes or lost test failures.
            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"Coverage collection failed for {suite}: exit {process.ExitCode}.");
                successful = false;
            }
        }
        return successful;
    }
}
