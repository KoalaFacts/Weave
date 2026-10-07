using System.Globalization;
using System.Xml;
using System.Xml.Linq;

internal sealed class CoverageAnalyzer(CoverageInventory inventory)
{
    private readonly Dictionary<string, Dictionary<(string File, int Line), bool>> _lines =
        inventory.Contributors.Keys.ToDictionary(name => name, _ => new Dictionary<(string, int), bool>(), StringComparer.Ordinal);
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, CoverageSourcePaths> _sources = inventory.ProjectDirectories
        .ToDictionary(entry => entry.Key, entry => new CoverageSourcePaths(entry.Value), StringComparer.Ordinal);

    public int Analyze(string outputDirectory, decimal threshold)
    {
        foreach (var (suite, _) in inventory.TestProjects.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var path = Path.Combine(outputDirectory, $"{suite}.cobertura.xml");
            try
            {
                if (!File.Exists(path))
                    throw new InvalidDataException($"Required report is missing: {path}");
                ReadReport(suite, XDocument.Load(path));
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or XmlException or UnauthorizedAccessException)
            {
                _errors.Add($"{suite}: {exception.Message}");
            }
        }

        Console.WriteLine($"Scope: directly exercised runtime owners of {string.Join(", ", inventory.TestProjects.Keys.Order(StringComparer.Ordinal))}.");
        Console.WriteLine("This tested-owner gate does not establish every-runtime-project coverage compliance.");
        Console.WriteLine("Coverage per assembly (union of source lines across selected reports):");
        Console.WriteLine($"  {"Assembly",-35} {"Covered",8} {"Valid",8} {"Rate",8}");
        var belowThreshold = new List<string>();
        foreach (var (assembly, lines) in _lines.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var required = inventory.RequiredAssemblies.Contains(assembly);
            var label = required ? "required" : "diagnostic only";
            if (!required)
                Console.WriteLine($"Coverage gap outside tested-owner gate: {assembly} ({(inventory.Contributors[assembly].Count == 0 ? "no test contributor" : "no selected direct or dynamic owner")}).");
            if (lines.Count == 0)
            {
                if (required)
                    _errors.Add($"{assembly}: missing non-excluded valid lines.");
                Console.WriteLine($"  {assembly,-35}      N/A      N/A      N/A  {label}; no line evidence");
                continue;
            }
            var covered = lines.Values.Count(hit => hit);
            var rate = (decimal)covered / lines.Count * 100;
            Console.WriteLine(FormattableString.Invariant($"  {assembly,-35} {covered,8} {lines.Count,8} {rate,7:F1}%  {label}"));
            if (required && rate < threshold)
                belowThreshold.Add(FormattableString.Invariant($"{assembly}: {rate:F4}% ({covered}/{lines.Count} lines)"));
        }
        foreach (var error in _errors.Order(StringComparer.Ordinal))
            Console.Error.WriteLine($"Coverage evidence error: {error}");
        foreach (var failure in belowThreshold)
            Console.Error.WriteLine(FormattableString.Invariant($"Below {threshold:F1}% threshold: {failure}"));
        if (_errors.Count != 0)
            return 2;
        return belowThreshold.Count == 0 ? 0 : 1;
    }

    private void ReadReport(string suite, XDocument report)
    {
        var sources = report.Descendants("source").Select(node => CoverageInventory.Normalize(node.Value)).ToArray();
        var validLines = 0;
        foreach (var package in report.Descendants("package"))
        {
            var assembly = package.Attribute("name")?.Value ?? "";
            if (!inventory.Contributors.TryGetValue(assembly, out var contributors))
                continue; // Framework, vendor, test, and build-only assemblies are not runtime targets.
            if (!contributors.Contains(suite))
                throw new InvalidDataException($"{assembly} has undeclared contributor {suite}.");
            foreach (var cls in package.Descendants("class"))
            {
                var filename = cls.Attribute("filename")?.Value ?? "";
                if (CoverageSourcePaths.IsExcluded(filename))
                    continue;
                var canonical = _sources[assembly].Resolve(filename, sources);
                if (CoverageSourcePaths.IsExcluded(canonical))
                    continue;
                foreach (var line in cls.Descendants("line"))
                {
                    if (!int.TryParse(line.Attribute("number")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                        || number <= 0
                        || !long.TryParse(line.Attribute("hits")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var hits))
                        throw new InvalidDataException($"{assembly}, {filename}: invalid line number or hits.");
                    var key = (canonical, number);
                    var lines = _lines[assembly];
                    lines[key] = hits > 0 || (lines.TryGetValue(key, out var covered) && covered);
                    if (inventory.RequiredAssemblies.Contains(assembly))
                        validLines++;
                }
            }
        }
        if (validLines == 0)
            throw new InvalidDataException("Report contains no owned, non-excluded valid lines.");
    }
}
