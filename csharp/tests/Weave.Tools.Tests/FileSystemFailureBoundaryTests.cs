using Microsoft.Extensions.Logging.Abstractions;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Tool;

namespace Weave.Tools.Tests;

public sealed class FileSystemFailureBoundaryTests : IAsyncDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("weave-fs-boundary-").FullName;
    private readonly FileSystemToolConnector _connector = new(NullLogger<FileSystemToolConnector>.Instance);

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task InvokeAsync_WriteOverDirectory_ReturnsSanitizedFailureAndPreservesContents()
    {
        var handle = await ConnectAsync();
        var directory = Directory.CreateDirectory(Path.Combine(_root, "reports")).FullName;
        var existingFile = Path.Combine(directory, "retained.txt");
        await File.WriteAllTextAsync(existingFile, "retained report", TestContext.Current.CancellationToken);

        var result = await _connector.InvokeAsync(handle, new ToolInvocation
        {
            ToolName = "workspace-files",
            Method = "write_file",
            Parameters = new Dictionary<string, string> { ["path"] = "reports" },
            RawInput = "replacement report"
        }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("workspace-files");
        result.Error.ShouldNotBeNull();
        result.Error.ShouldContain("[sandbox]");
        result.Error.ShouldNotContain(_root);
        result.Output.ShouldBeEmpty();
        Directory.Exists(directory).ShouldBeTrue();
        (await File.ReadAllTextAsync(existingFile, TestContext.Current.CancellationToken)).ShouldBe("retained report");
    }

    [Fact]
    public async Task InvokeAsync_SearchValidPattern_ReturnsOnlyMatchingRelativePath()
    {
        var handle = await ConnectAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "report.txt"), "searchable report", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_root, "report.log"), "excluded log", TestContext.Current.CancellationToken);

        var result = await _connector.InvokeAsync(handle, new ToolInvocation
        {
            ToolName = "workspace-files",
            Method = "search_files",
            Parameters = new Dictionary<string, string> { ["pattern"] = "report*.txt" }
        }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("report.txt");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeAsync_SearchPatternWithNullByte_ReturnsArgumentFailure()
    {
        var handle = await ConnectAsync();
        await File.WriteAllTextAsync(Path.Combine(_root, "report.txt"), "searchable report", TestContext.Current.CancellationToken);
        const string pattern = "report\0*.txt";
        var expectedError = Should.Throw<ArgumentException>(() => Directory.GetFiles(_root, pattern, SearchOption.AllDirectories));

        var result = await _connector.InvokeAsync(handle, new ToolInvocation
        {
            ToolName = "workspace-files",
            Method = "search_files",
            Parameters = new Dictionary<string, string> { ["pattern"] = pattern }
        }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.ToolName.ShouldBe("workspace-files");
        result.Error.ShouldBe(expectedError.Message);
        result.Output.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("path")]
    [InlineData("new_string")]
    public async Task InvokeAsync_EditMissingRequiredParameter_ReturnsSpecificFailureAndPreservesFile(string missingParameter)
    {
        var handle = await ConnectAsync();
        var file = Path.Combine(_root, "report.txt");
        await File.WriteAllTextAsync(file, "original report", TestContext.Current.CancellationToken);
        var parameters = new Dictionary<string, string>
        {
            ["path"] = "report.txt",
            ["old_string"] = "original",
            ["new_string"] = "revised"
        };
        parameters.Remove(missingParameter);

        var result = await _connector.InvokeAsync(handle, new ToolInvocation
        {
            ToolName = "workspace-files",
            Method = "edit_file",
            Parameters = parameters
        }, TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe($"Parameter '{missingParameter}' is required for edit_file");
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ShouldBe("original report");
    }

    private Task<ToolHandle> ConnectAsync() => _connector.ConnectAsync(new ToolSpec
    {
        Name = "workspace-files",
        Type = ToolType.FileSystem,
        FileSystem = new FileSystemToolConfig { Root = _root }
    }, new CapabilityToken
    {
        TokenId = "filesystem-boundary-test",
        WorkspaceId = "filesystem-tests",
        Grants = ["tool:workspace-files:invoke:write_file", "tool:workspace-files:invoke:search_files", "tool:workspace-files:invoke:edit_file"]
    }, TestContext.Current.CancellationToken);
}
