using System.Globalization;
using System.Text.Json;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

internal sealed class LocalCliBoundaryFixture : IDisposable
{
    public const string OperatorKey = "local-cli-fixture-operator";
    public const string Capability = "local-cli-fixture-capability";
    private readonly TextWriter _originalOut = Console.Out;
    private readonly TextWriter _originalError = Console.Error;
    public StringWriter Output { get; } = new(CultureInfo.InvariantCulture);
    public StringWriter Error { get; } = new(CultureInfo.InvariantCulture);
    public LocalCliRecordingLauncher Launcher { get; } = new();
    public LocalCliNoninteractiveReview Review { get; } = new();
    public LocalCliCommand Command { get; }
    public LocalDeployment Deployment { get; }
    public string DirectoryPath { get; }
    public string ConfigurationPath { get; }
    public string MarkerPath { get; }
    public string ReceiptPath { get; }
    private readonly string _configuration;
    private readonly string _marker;

    public LocalCliBoundaryFixture(string root, int port = 9527)
    {
        DirectoryPath = Path.Join(root, "retained local deployment");
        Directory.CreateDirectory(Path.Join(DirectoryPath, "private"));
        Directory.CreateDirectory(Path.Join(DirectoryPath, "receipts"));
        var documents = Directory.CreateDirectory(Path.Join(root, "documents")).FullName;
        var host = Path.Join(root, "unexecuted fixture host");
        File.WriteAllText(host, "fixture only; never executed");
        Deployment = new LocalDeployment(host, documents, "onboarding", port);
        ConfigurationPath = LocalDeploymentStore.HostConfigurationPath(DirectoryPath);
        MarkerPath = Path.Join(DirectoryPath, "local.json");
        ReceiptPath = Path.Join(DirectoryPath, "receipts", "original.sha256");
        _configuration = """
            {"CapabilityTokens":{"RequireExistingStorage":true,"SigningKey":"unused-fixture-signing"},
             "Weave":{"Operator":{"Enabled":true,"Key":"local-cli-fixture-operator"},
              "Invocations":{"RequireExistingStorage":true,"Http":{"DecisionsEnabled":true},
               "ApprovalRequiredGrants":["tool:files:invoke:write_file"]}}}
            """;
        _marker = JsonSerializer.Serialize(Deployment, LocalJsonContext.Default.LocalDeployment);
        File.WriteAllText(ConfigurationPath, _configuration);
        File.WriteAllText(MarkerPath, _marker);
        File.WriteAllText(ReceiptPath, "retained-original-fingerprint");
        var store = new LocalDeploymentStore();
        Command = new LocalCliCommand(store, new LocalHostRunner(store, TimeProvider.System), Launcher,
            new LocalReviewWorkflow(new LocalReview(Review), Launcher), TimeProvider.System);
        Console.SetOut(TextWriter.Synchronized(Output));
        Console.SetError(TextWriter.Synchronized(Error));
    }

    public void AssertPreserved()
    {
        AssertFilesPreserved();
        Launcher.Calls.ShouldBeEmpty();
    }

    public void AssertFilesPreserved()
    {
        File.ReadAllText(ConfigurationPath).ShouldBe(_configuration);
        File.ReadAllText(MarkerPath).ShouldBe(_marker);
        File.ReadAllText(ReceiptPath).ShouldBe("retained-original-fingerprint");
    }

    public void Dispose()
    {
        Console.SetOut(_originalOut);
        Console.SetError(_originalError);
        Output.Dispose();
        Error.Dispose();
    }
}
