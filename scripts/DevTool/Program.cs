// Weave developer tool — run with:
//   dotnet run --project scripts/DevTool -- <command> [options]
//
// Commands:
//   coverage   Enforce minimum line-coverage threshold from Cobertura XML.

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

var command = args[0];
var remaining = args[1..];

return command switch
{
    "coverage" => CoverageCommand.Run(remaining),
    "-h" or "--help" => PrintUsage(),
    _ => PrintUnknown(command),
};

static int PrintUsage()
{
    Console.WriteLine("""
        Weave DevTool

        Usage: dotnet run --project scripts/DevTool -- <command> [options]

        Commands:
          coverage   Enforce minimum line-coverage threshold from Cobertura XML.

        Options:
          -h, --help   Show this help message.

        Coverage options (run from the repository root after a Release solution build):
          -t, --threshold N   Minimum line coverage per required assembly; default 90.
          -r, --root PATH     Select test projects under PATH; default tests.
          --skip-collect      Analyze the exact selected reports already in TestResults.

        Coverage scope: direct runtime owners of selected test projects. Transitive
        and unowned runtime assemblies are printed as diagnostic scope gaps.
        Exit codes: 0 = tested-owner threshold met, 1 = threshold/collection failure,
        2 = invalid or missing evidence. A scoped pass is not every-project compliance.
        """);
    return 0;
}

static int PrintUnknown(string command)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"Unknown command: {command}");
    Console.ResetColor();
    PrintUsage();
    return 1;
}
