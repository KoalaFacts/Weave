using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

/// <summary>
/// Collects coverage via dotnet-coverage (local tool), parses Cobertura XML,
/// and exits non-zero when any project is below the threshold.
/// </summary>
internal static class CoverageCommand
{
    private const string OutputDir = "TestResults";

    public static int Run(string[] args)
    {
        var threshold = 90.0;
        var searchRoot = "src";
        var skipCollect = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-t" or "--threshold" when i + 1 < args.Length:
                    threshold = double.Parse(args[++i], CultureInfo.InvariantCulture);
                    break;
                case "-r" or "--root" when i + 1 < args.Length:
                    searchRoot = args[++i];
                    break;
                case "--skip-collect":
                    skipCollect = true;
                    break;
            }
        }

        if (!skipCollect)
        {
            var exitCode = CollectCoverage(searchRoot);
            if (exitCode != 0)
                return exitCode;
        }

        return AnalyzeCoverage(threshold);
    }

    private static int CollectCoverage(string searchRoot)
    {
        var testProjects = Directory.GetFiles(searchRoot, "*.Tests.csproj", SearchOption.AllDirectories);
        if (testProjects.Length == 0)
        {
            WriteColored(ConsoleColor.Yellow, $"No test projects found under '{searchRoot}'.");
            return 2;
        }

        if (Directory.Exists(OutputDir))
            Directory.Delete(OutputDir, recursive: true);
        Directory.CreateDirectory(OutputDir);

        WriteColored(ConsoleColor.Cyan, $"Collecting coverage for {testProjects.Length} test project(s)...");
        Console.WriteLine();

        foreach (var project in testProjects)
        {
            var projectName = Path.GetFileNameWithoutExtension(project);
            var outputFile = Path.Combine(OutputDir, $"{projectName}.cobertura.xml");

            Console.Write($"  {projectName,-40} ");

            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = $"dotnet-coverage collect -f cobertura -o \"{outputFile}\" -- dotnet test --project \"{project}\" -c Release",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };

            using var proc = Process.Start(psi)!;
            proc.WaitForExit();

            if (proc.ExitCode != 0)
            {
                WriteColored(ConsoleColor.Red, "FAILED");
                var stderr = proc.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(stderr))
                    Console.Error.WriteLine(stderr);
                return 1;
            }

            WriteColored(ConsoleColor.Green, "OK");
        }

        Console.WriteLine();
        return 0;
    }

    private static int AnalyzeCoverage(double threshold)
    {
        var reports = Directory.GetFiles(OutputDir, "*.cobertura.xml", SearchOption.TopDirectoryOnly);

        if (reports.Length == 0)
        {
            WriteColored(ConsoleColor.Yellow,
                $"No cobertura.xml files found in '{OutputDir}'. Run without --skip-collect first.");
            return 2;
        }

        // Each report is named Weave.X.Tests.cobertura.xml — match to Weave.X package.
        var perPackage = new Dictionary<string, (int Covered, int Valid)>(StringComparer.Ordinal);

        foreach (var reportPath in reports)
        {
            var reportName = Path.GetFileNameWithoutExtension(
                Path.GetFileNameWithoutExtension(reportPath)); // strip .cobertura.xml
            string? ownedPackage = reportName.EndsWith(".Tests", StringComparison.Ordinal)
                ? reportName[..^6]
                : null;

            var doc = XDocument.Load(reportPath);

            foreach (var pkg in doc.Descendants("package"))
            {
                var name = pkg.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name))
                    continue;

                if (name.Contains(".Tests", StringComparison.Ordinal))
                    continue;

                if (ownedPackage is not null && !string.Equals(name, ownedPackage, StringComparison.Ordinal))
                    continue;

                var covered = 0;
                var valid = 0;

                foreach (var cls in pkg.Descendants("class"))
                {
                    var filename = cls.Attribute("filename")?.Value ?? "";

                    if (IsExcluded(filename))
                        continue;

                    foreach (var line in cls.Descendants("line"))
                    {
                        valid++;
                        if (int.TryParse(line.Attribute("hits")?.Value, out var hits) && hits > 0)
                            covered++;
                    }
                }

                perPackage[name] = (covered, valid);
            }
        }

        var totalCovered = 0;
        var totalValid = 0;
        var failing = new List<(string Name, double Rate)>();

        var assemblies = perPackage
            .OrderBy(p => p.Value.Valid > 0 ? (double)p.Value.Covered / p.Value.Valid : 0)
            .ToList();

        Console.WriteLine();
        WriteColored(ConsoleColor.Cyan, "Coverage per assembly:");
        Console.WriteLine($"  {"Assembly",-35} {"Covered",8} {"Valid",8} {"Rate",8}");
        Console.WriteLine($"  {new string('-', 35)} {new string('-', 8)} {new string('-', 8)} {new string('-', 8)}");

        foreach (var (name, (covered, valid)) in assemblies)
        {
            var rate = valid > 0 ? (double)covered / valid * 100 : 0;
            totalCovered += covered;
            totalValid += valid;

            var color = rate < threshold ? ConsoleColor.Red : ConsoleColor.Gray;
            Console.ForegroundColor = color;
            Console.WriteLine($"  {name,-35} {covered,8:N0} {valid,8:N0} {rate,7:F1}%");
            Console.ResetColor();

            if (rate < threshold)
                failing.Add((name, rate));
        }

        var overallRate = totalValid > 0 ? (double)totalCovered / totalValid * 100 : 0;

        Console.WriteLine();
        WriteColored(ConsoleColor.Cyan,
            $"Overall: {overallRate:F1}%  ({totalCovered:N0} / {totalValid:N0} lines)  threshold {threshold:F1}%");

        if (failing.Count == 0)
            return 0;

        Console.WriteLine();
        WriteColored(ConsoleColor.Red,
            $"{failing.Count} project(s) below {threshold:F1}% threshold:");
        foreach (var (name, rate) in failing)
            WriteColored(ConsoleColor.Red, $"  {name}: {rate:F1}%");
        WriteColored(ConsoleColor.Red,
            "Each project must individually meet the coverage threshold.");
        return 1;
    }

    private static bool IsExcluded(string filename)
    {
        if (filename.Contains("/obj/", StringComparison.Ordinal) ||
            filename.Contains("\\obj\\", StringComparison.Ordinal))
            return true;

        if (filename.EndsWith(".g.cs", StringComparison.Ordinal))
            return true;

        if (filename.Contains("/Models/", StringComparison.Ordinal) ||
            filename.Contains("\\Models\\", StringComparison.Ordinal))
            return true;

        if (filename.Contains("Surrogate", StringComparison.Ordinal))
            return true;

        if (filename.EndsWith("Contracts.cs", StringComparison.Ordinal))
            return true;

        var name = Path.GetFileName(filename);
        if (name.Equals("Program.cs", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
