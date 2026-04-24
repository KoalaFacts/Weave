using System.Globalization;
using System.Xml.Linq;

/// <summary>
/// Parses Cobertura XML from coverlet, aggregates per-assembly line coverage,
/// and exits non-zero when the overall rate is below the threshold.
/// </summary>
internal static class CoverageCommand
{
    public static int Run(string[] args)
    {
        var threshold = 90.0;
        var searchRoot = "src";

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
            }
        }

        var reports = Directory.GetFiles(searchRoot, "*.cobertura.xml", SearchOption.AllDirectories);

        if (reports.Length == 0)
        {
            WriteColored(ConsoleColor.Yellow,
                $"No cobertura.xml files found under '{searchRoot}'. Did the test run collect coverage?");
            Console.WriteLine(
                "  dotnet test --solution Weave.slnx -c Release --collect:\"XPlat Code Coverage\" --settings coverage.runsettings");
            return 2;
        }

        // Each production package is counted ONCE — from the cobertura whose owning
        // test project matches (Weave.X.Tests → Weave.X). This avoids inflating the
        // denominator when a package is transitively exercised by another project's tests.
        var perPackage = new Dictionary<string, (int Covered, int Valid)>(StringComparer.Ordinal);

        foreach (var reportPath in reports)
        {
            var doc = XDocument.Load(reportPath);

            // .../Weave.X.Tests/bin/Release/net10.0/TestResults/*.cobertura.xml
            var testResultsDir = Path.GetDirectoryName(reportPath)!;
            var tfmDir = Path.GetDirectoryName(testResultsDir)!;
            var configDir = Path.GetDirectoryName(tfmDir)!;
            var binDir = Path.GetDirectoryName(configDir)!;
            var testProjectDir = Path.GetDirectoryName(binDir)!;
            var owningTestProject = Path.GetFileName(testProjectDir);

            string? ownedPackage = owningTestProject.EndsWith(".Tests", StringComparison.Ordinal)
                ? owningTestProject[..^6]
                : null;

            foreach (var pkg in doc.Descendants("package"))
            {
                var name = pkg.Attribute("name")?.Value;
                if (string.IsNullOrEmpty(name))
                    continue;

                // Skip test assemblies.
                if (name.Contains(".Tests", StringComparison.Ordinal))
                    continue;

                // Only count a package from its owning test project's cobertura.
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

        // Report.
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

        return false;
    }

    private static void WriteColored(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
