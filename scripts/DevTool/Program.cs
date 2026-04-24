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
