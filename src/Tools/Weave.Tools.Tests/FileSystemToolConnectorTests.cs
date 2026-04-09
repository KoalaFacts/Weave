using Microsoft.Extensions.Logging;
using Weave.Security.Tokens;
using Weave.Tools.Connectors;
using Weave.Tools.Models;

namespace Weave.Tools.Tests;

public sealed class FileSystemToolConnectorTests : IDisposable
{
    private static readonly CapabilityToken _testToken = new()
    {
        TokenId = "test-token",
        WorkspaceId = "ws-1",
        Grants = ["tool:fs-tool"]
    };

    private readonly string _tempRoot;

    public FileSystemToolConnectorTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"weave-fs-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose() => Directory.Delete(_tempRoot, recursive: true);

    private static FileSystemToolConnector CreateConnector() =>
        new(Substitute.For<ILogger<FileSystemToolConnector>>());

    private ToolSpec CreateSpec(bool readOnly = false, long maxReadBytes = 1_048_576) =>
        new()
        {
            Name = "fs-tool",
            Type = ToolType.FileSystem,
            FileSystem = new FileSystemToolConfig { Root = _tempRoot, ReadOnly = readOnly, MaxReadBytes = maxReadBytes }
        };

    // --- Lifecycle ---

    [Fact]
    public async Task ConnectAsync_ValidConfig_ReturnsConnectedHandle()
    {
        var connector = CreateConnector();

        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        handle.IsConnected.ShouldBeTrue();
        handle.ToolName.ShouldBe("fs-tool");
        handle.Type.ShouldBe(ToolType.FileSystem);
        handle.ConnectionId.ShouldStartWith("fs:fs-tool:");
    }

    [Fact]
    public async Task ConnectAsync_NullFileSystemConfig_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec { Name = "bad", Type = ToolType.FileSystem };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    [Fact]
    public async Task ConnectAsync_EmptyRoot_Throws()
    {
        var connector = CreateConnector();
        var spec = new ToolSpec
        {
            Name = "bad",
            Type = ToolType.FileSystem,
            FileSystem = new FileSystemToolConfig { Root = "" }
        };

        await Should.ThrowAsync<InvalidOperationException>(
            () => connector.ConnectAsync(spec, _testToken));
    }

    [Fact]
    public async Task ConnectAsync_TwoCalls_GenerateUniqueConnectionIds()
    {
        var connector = CreateConnector();

        var handle1 = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var handle2 = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        handle1.ConnectionId.ShouldNotBe(handle2.ConnectionId);
    }

    [Fact]
    public async Task DisconnectAsync_AfterConnect_Completes()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ToolType_IsFileSystem()
    {
        CreateConnector().ToolType.ShouldBe(ToolType.FileSystem);
    }

    // --- DiscoverSchemaAsync ---

    [Fact]
    public async Task DiscoverSchemaAsync_ReturnsSchemaWithParameters()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var schema = await connector.DiscoverSchemaAsync(handle, TestContext.Current.CancellationToken);

        schema.ToolName.ShouldBe("fs-tool");
        schema.Description.ShouldNotBeNullOrEmpty();
        schema.Parameters.ShouldNotBeEmpty();
        schema.Parameters.ShouldContain(p => p.Name == "path");
    }

    // --- read_file ---

    [Fact]
    public async Task ReadFile_ExistingFile_ReturnsContent()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "hello.txt"), "hello world");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "hello.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("hello world");
    }

    [Fact]
    public async Task ReadFile_NonexistentFile_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "missing.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadFile_ExceedsMaxSize_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(maxReadBytes: 10), _testToken, TestContext.Current.CancellationToken);
        var bigContent = new string('x', 100);
        File.WriteAllText(Path.Combine(_tempRoot, "big.txt"), bigContent);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "big.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("exceeds");
    }

    [Fact]
    public async Task ReadFile_BinaryFile_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var binaryPath = Path.Combine(_tempRoot, "binary.bin");
        File.WriteAllBytes(binaryPath, [0x00, 0x01, 0x02, 0x03]);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "binary.bin" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("binary");
    }

    [Fact]
    public async Task ReadFile_EmptyFile_ReturnsEmptyOutput()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "empty.txt"), "");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "empty.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("");
    }

    // --- write_file ---

    [Fact]
    public async Task WriteFile_NewFile_CreatesFile()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation
            {
                ToolName = "fs-tool",
                Method = "write_file",
                Parameters = new Dictionary<string, string> { ["path"] = "new.txt" },
                RawInput = "written content"
            },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_tempRoot, "new.txt")).ShouldBe("written content");
    }

    [Fact]
    public async Task WriteFile_ExistingFile_OverwritesContent()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "existing.txt"), "old content");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation
            {
                ToolName = "fs-tool",
                Method = "write_file",
                Parameters = new Dictionary<string, string> { ["path"] = "existing.txt" },
                RawInput = "new content"
            },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.ReadAllText(Path.Combine(_tempRoot, "existing.txt")).ShouldBe("new content");
    }

    [Fact]
    public async Task WriteFile_CreatesParentDirectories()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation
            {
                ToolName = "fs-tool",
                Method = "write_file",
                Parameters = new Dictionary<string, string> { ["path"] = "sub/dir/file.txt" },
                RawInput = "nested"
            },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        File.Exists(Path.Combine(_tempRoot, "sub", "dir", "file.txt")).ShouldBeTrue();
    }

    [Fact]
    public async Task WriteFile_ReadOnlyMode_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(readOnly: true), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation
            {
                ToolName = "fs-tool",
                Method = "write_file",
                Parameters = new Dictionary<string, string> { ["path"] = "blocked.txt" },
                RawInput = "data"
            },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("read-only");
    }

    [Fact]
    public async Task WriteFile_NullRawInput_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation
            {
                ToolName = "fs-tool",
                Method = "write_file",
                Parameters = new Dictionary<string, string> { ["path"] = "file.txt" }
            },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    // --- list_directory ---

    [Fact]
    public async Task ListDirectory_RootDefault_ListsContents()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "a.txt"), "");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "subdir"));

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "list_directory", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("a.txt");
        result.Output.ShouldContain("subdir");
    }

    [Fact]
    public async Task ListDirectory_Subdirectory_ListsContents()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var subdir = Path.Combine(_tempRoot, "mydir");
        Directory.CreateDirectory(subdir);
        File.WriteAllText(Path.Combine(subdir, "child.txt"), "");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "list_directory", Parameters = new Dictionary<string, string> { ["path"] = "mydir" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("child.txt");
    }

    [Fact]
    public async Task ListDirectory_EmptyDirectory_ReturnsSuccess()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "emptydir"));

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "list_directory", Parameters = new Dictionary<string, string> { ["path"] = "emptydir" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task ListDirectory_NonexistentPath_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "list_directory", Parameters = new Dictionary<string, string> { ["path"] = "doesnotexist" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    // --- search_files ---

    [Fact]
    public async Task SearchFiles_MatchingPattern_ReturnsResults()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "alpha.txt"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "beta.txt"), "");
        File.WriteAllText(Path.Combine(_tempRoot, "gamma.log"), "");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "search_files", Parameters = new Dictionary<string, string> { ["pattern"] = "*.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("alpha.txt");
        result.Output.ShouldContain("beta.txt");
        result.Output.ShouldNotContain("gamma.log");
    }

    [Fact]
    public async Task SearchFiles_NoMatches_ReturnsEmpty()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "search_files", Parameters = new Dictionary<string, string> { ["pattern"] = "*.nonexistent" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("");
    }

    [Fact]
    public async Task SearchFiles_RecursiveSearch_FindsNestedFiles()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var nested = Path.Combine(_tempRoot, "deep", "nested");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "inner.txt"), "");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "search_files", Parameters = new Dictionary<string, string> { ["pattern"] = "*.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("inner.txt");
    }

    // --- file_info ---

    [Fact]
    public async Task FileInfo_ExistingFile_ReturnsMetadata()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "meta.txt"), "some data");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "file_info", Parameters = new Dictionary<string, string> { ["path"] = "meta.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("Size:");
        result.Output.ShouldContain("LastModifiedUtc:");
    }

    [Fact]
    public async Task FileInfo_NonexistentFile_ReportsNotExists()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "file_info", Parameters = new Dictionary<string, string> { ["path"] = "ghost.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("Exists: False");
    }

    [Fact]
    public async Task FileInfo_Directory_ReportsIsDirectory()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "adir"));

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "file_info", Parameters = new Dictionary<string, string> { ["path"] = "adir" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("IsDirectory: True");
    }

    // --- Path traversal (security) ---

    [Fact]
    public async Task ReadFile_PathTraversal_DotDot_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "../../../etc/passwd" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadFile_PathTraversal_BackslashDotDot_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = @"..\..\secret" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadFile_AbsolutePathUnix_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "/etc/passwd" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadFile_AbsolutePathWindows_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = @"C:\Windows\system32" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task WriteFile_PathTraversal_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation
            {
                ToolName = "fs-tool",
                Method = "write_file",
                Parameters = new Dictionary<string, string> { ["path"] = "../escape.txt" },
                RawInput = "evil"
            },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task ListDirectory_PathTraversal_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "list_directory", Parameters = new Dictionary<string, string> { ["path"] = "../../tmp" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task ReadFile_NullByteInPath_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "file\0.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task SearchFiles_PatternWithDotDot_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "search_files", Parameters = new Dictionary<string, string> { ["pattern"] = "../*.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("..");
    }

    [Fact]
    public async Task FileInfo_PathTraversal_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "file_info", Parameters = new Dictionary<string, string> { ["path"] = "../../etc/passwd" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task SearchFiles_AbsolutePathPattern_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "search_files", Parameters = new Dictionary<string, string> { ["pattern"] = "../*" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
    }

    // --- Missing parameter tests ---

    [Fact]
    public async Task ReadFile_MissingPathParam_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("path");
    }

    [Fact]
    public async Task WriteFile_MissingPathParam_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "write_file", Parameters = [], RawInput = "data" },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("path");
    }

    [Fact]
    public async Task FileInfo_MissingPathParam_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "file_info", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("path");
    }

    [Fact]
    public async Task SearchFiles_MissingPatternParam_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "search_files", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("pattern");
    }

    // --- Operational edge cases ---

    [Fact]
    public async Task ReadFile_InSubdirectory_ReturnsContent()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        var subdir = Path.Combine(_tempRoot, "deep", "path");
        Directory.CreateDirectory(subdir);
        File.WriteAllText(Path.Combine(subdir, "nested.txt"), "nested content");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "deep/path/nested.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("nested content");
    }

    [Fact]
    public async Task WriteFile_ThenReadFile_Roundtrip()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "write_file", Parameters = new Dictionary<string, string> { ["path"] = "roundtrip.txt" }, RawInput = "round-trip content" },
            TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "roundtrip.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("round-trip content");
    }

    [Fact]
    public async Task ListDirectory_ShowsTypeMarkers()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "afile.txt"), "data");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "afolder"));

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "list_directory", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldContain("[file]");
        result.Output.ShouldContain("[dir]");
        result.Output.ShouldContain("afolder/");
    }

    [Fact]
    public async Task ReadFile_MaxReadBytesZero_DefaultsTo1MB()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(maxReadBytes: 0), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "small.txt"), "hello");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "small.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("hello");
    }

    [Fact]
    public async Task InvokeAsync_MethodCaseInsensitive_Works()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "case.txt"), "content");

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "READ_FILE", Parameters = new Dictionary<string, string> { ["path"] = "case.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeTrue();
        result.Output.ShouldBe("content");
    }

    // --- ResolveSafePath direct tests (internal static) ---

    [Fact]
    public void ResolveSafePath_ValidRelativePath_ReturnsFullPath()
    {
        var result = FileSystemToolConnector.ResolveSafePath(_tempRoot, "subdir/file.txt");

        result.ShouldBe(Path.Combine(_tempRoot, "subdir", "file.txt"));
    }

    [Fact]
    public void ResolveSafePath_DotDotTraversal_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            FileSystemToolConnector.ResolveSafePath(_tempRoot, "../../../etc/passwd"));
    }

    [Fact]
    public void ResolveSafePath_AbsolutePathUnix_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            FileSystemToolConnector.ResolveSafePath(_tempRoot, "/etc/passwd"));
    }

    [Fact]
    public void ResolveSafePath_AbsolutePathWindows_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            FileSystemToolConnector.ResolveSafePath(_tempRoot, @"C:\Windows"));
    }

    [Fact]
    public void ResolveSafePath_EmptyPath_ReturnsRoot()
    {
        var result = FileSystemToolConnector.ResolveSafePath(_tempRoot, "");

        result.ShouldBe(_tempRoot);
    }

    [Fact]
    public void ResolveSafePath_UrlScheme_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            FileSystemToolConnector.ResolveSafePath(_tempRoot, "http://evil.com/payload"));
    }

    [Fact]
    public void ResolveSafePath_NullByte_Throws()
    {
        Should.Throw<ArgumentException>(() =>
            FileSystemToolConnector.ResolveSafePath(_tempRoot, "file\0.txt"));
    }

    [Fact]
    public void ResolveSafePath_NestedSubdirectory_ReturnsCorrectPath()
    {
        var result = FileSystemToolConnector.ResolveSafePath(_tempRoot, "a/b/c/file.txt");

        result.ShouldBe(Path.Combine(_tempRoot, "a", "b", "c", "file.txt"));
    }

    // --- Edge cases ---

    [Fact]
    public async Task InvokeAsync_DisconnectedTool_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        await connector.DisconnectAsync(handle, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "file.txt" } },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("not connected");
    }

    [Fact]
    public async Task InvokeAsync_UnknownMethod_ReturnsFailure()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);

        var result = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "delete_everything", Parameters = [] },
            TestContext.Current.CancellationToken);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull();
        result.Error!.ShouldContain("Unknown method");
    }

    [Fact]
    public async Task InvokeAsync_AllMethods_IncludeDuration()
    {
        var connector = CreateConnector();
        var handle = await connector.ConnectAsync(CreateSpec(), _testToken, TestContext.Current.CancellationToken);
        File.WriteAllText(Path.Combine(_tempRoot, "dur.txt"), "data");

        var readResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "read_file", Parameters = new Dictionary<string, string> { ["path"] = "dur.txt" } },
            TestContext.Current.CancellationToken);

        var listResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "list_directory", Parameters = [] },
            TestContext.Current.CancellationToken);

        var searchResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "search_files", Parameters = new Dictionary<string, string> { ["pattern"] = "*.txt" } },
            TestContext.Current.CancellationToken);

        var infoResult = await connector.InvokeAsync(handle,
            new ToolInvocation { ToolName = "fs-tool", Method = "file_info", Parameters = new Dictionary<string, string> { ["path"] = "dur.txt" } },
            TestContext.Current.CancellationToken);

        readResult.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        listResult.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        searchResult.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
        infoResult.Duration.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }
}
