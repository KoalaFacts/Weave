using System.Text.Json;
using System.Xml.Linq;

internal sealed class CoverageInventory
{
    private static readonly string[] RuntimeRoots = ["src", "hosts", "extensions"];
    public Dictionary<string, string> TestProjects { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> ProjectDirectories { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, HashSet<string>> Contributors { get; } = new(StringComparer.Ordinal);
    public HashSet<string> RequiredAssemblies { get; } = new(StringComparer.Ordinal);

    public static CoverageInventory Load(string searchRoot)
    {
        var inventory = new CoverageInventory();
        var allTests = FindProjects("tests", "*.Tests.csproj")
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);
        foreach (var project in FindProjects(searchRoot, "*.Tests.csproj"))
            inventory.TestProjects.Add(Path.GetFileNameWithoutExtension(project), project);
        if (inventory.TestProjects.Count == 0)
            throw new InvalidDataException($"No test projects found under '{searchRoot}'.");

        using var manifest = JsonDocument.Parse(File.ReadAllText("scripts/coverage-ownership.json"));
        var runtimeProjects = RuntimeRoots
            .SelectMany(root => FindProjects(root, "*.csproj")).ToHashSet(StringComparer.Ordinal);
        var mappedProjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.RootElement.EnumerateObject())
        {
            if (!mappedProjects.Add(entry.Name) || !runtimeProjects.Contains(entry.Name))
                throw new InvalidDataException($"Coverage manifest project is duplicate or absent: {entry.Name}");
            var assembly = entry.Value.GetProperty("assembly").GetString();
            var project = XDocument.Load(entry.Name);
            var declared = project.Descendants("AssemblyName").Select(node => node.Value).Distinct().ToArray();
            var actual = declared.Length == 0 ? Path.GetFileNameWithoutExtension(entry.Name) : declared.Single();
            if (string.IsNullOrWhiteSpace(assembly) || assembly != actual || actual.Contains("$(", StringComparison.Ordinal))
                throw new InvalidDataException($"Coverage assembly mismatch for {entry.Name}: expected {assembly}, project declares {actual}.");
            inventory.ProjectDirectories.Add(assembly, Normalize(Path.GetDirectoryName(entry.Name)!));
            var contributors = entry.Value.GetProperty("contributors").EnumerateArray()
                .Select(node => node.GetString() ?? throw new InvalidDataException($"Null contributor for {assembly}."))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var suite in contributors)
                if (!allTests.ContainsKey(suite))
                    throw new InvalidDataException($"Unknown contributor {suite} for {assembly}.");
            inventory.Contributors.Add(assembly, contributors);
            var owners = entry.Value.GetProperty("owners").EnumerateArray()
                .Select(node => node.GetString() ?? throw new InvalidDataException($"Null owner for {assembly}."))
                .ToHashSet(StringComparer.Ordinal);
            if (!owners.IsSubsetOf(contributors))
                throw new InvalidDataException($"Coverage owners must also be contributors for {assembly}.");
            if (owners.Overlaps(inventory.TestProjects.Keys))
                inventory.RequiredAssemblies.Add(assembly);
        }
        var missing = runtimeProjects.Except(mappedProjects).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
            throw new InvalidDataException($"Runtime projects missing from coverage ownership: {string.Join(", ", missing)}");
        foreach (var suite in inventory.TestProjects.Keys)
            if (!allTests.ContainsKey(suite) || !inventory.Contributors.Values.Any(owners => owners.Contains(suite)))
                throw new InvalidDataException($"Unknown coverage suite: {suite}; declare its actual assembly contributors.");

        return inventory;
    }

    private static IEnumerable<string> FindProjects(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
            throw new InvalidDataException($"Project directory is missing: {directory}");
        return Directory.GetFiles(directory, pattern, SearchOption.AllDirectories)
            .Select(path => Normalize(Path.GetRelativePath(Environment.CurrentDirectory, path)))
            .Where(path => !path.Split('/').Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
    }

    public static string Normalize(string path) => path.Replace('\\', '/');
}
