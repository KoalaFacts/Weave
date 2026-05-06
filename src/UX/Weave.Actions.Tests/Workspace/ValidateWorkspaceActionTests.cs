using NSubstitute;
using Weave.Actions.Context;
using Weave.Actions.Workspace;
using Weave.Workspaces.Manifest;

namespace Weave.Actions.Tests.Workspace;

public sealed class ValidateWorkspaceActionTests
{
    [Fact]
    public async Task ExecuteAsync_ValidManifest_ReturnsSummaryWithEmptyErrors()
    {
        const string json = """
        {
          "version": "1.0",
          "name": "demo",
          "agents": {
            "alpha": {
              "model": "gpt",
              "system_prompt_file": "./prompts/alpha.md",
              "tools": ["git"]
            }
          },
          "tools": {
            "git": { "type": "cli", "cli": { "shell": "/bin/bash" } }
          },
          "targets": { "local": { "runtime": "podman" } }
        }
        """;
        using var temp = TempFile.With(json);
        var action = new ValidateWorkspaceAction(new ManifestParser());

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Name.ShouldBe("demo");
        result.Value.AgentCount.ShouldBe(1);
        result.Value.ToolCount.ShouldBe(1);
        result.Value.TargetCount.ShouldBe(1);
        result.Value.IsValid.ShouldBeTrue();
        result.Value.Errors.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_StructurallyInvalidManifest_ReturnsErrorsInResult()
    {
        var parser = Substitute.For<IManifestParser>();
        var manifest = new WorkspaceManifest { Name = "bad", Version = "1" };
        parser.Parse(Arg.Any<string>()).Returns(manifest);
        parser.Validate(manifest).Returns(["agent 'alpha' references unknown tool 'git'"]);

        using var temp = TempFile.With("{ }");
        var action = new ValidateWorkspaceAction(parser);

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsValid.ShouldBeFalse();
        result.Value.Errors.Count.ShouldBe(1);
        result.Value.Errors[0].ShouldContain("unknown tool");
    }

    [Fact]
    public async Task ExecuteAsync_MissingFile_ReturnsNotFound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"weave-missing-{Guid.NewGuid():N}.json");
        var action = new ValidateWorkspaceAction(new ManifestParser());

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(path), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.NotFound);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJson_ReturnsValidationFailed()
    {
        using var temp = TempFile.With("{ this is not json");
        var action = new ValidateWorkspaceAction(new ManifestParser());

        var result = await action.ExecuteAsync(new ValidateWorkspaceInput(temp.Path), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Failure.ShouldNotBeNull();
        result.Failure.Reason.ShouldBe(ActionFailureReason.ValidationFailed);
    }

    [Fact]
    public async Task ExecuteAsync_NullInput_Throws()
    {
        var action = new ValidateWorkspaceAction(new ManifestParser());

        await Should.ThrowAsync<ArgumentNullException>(
            () => action.ExecuteAsync(null!, CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ExecuteAsync_BlankPath_Throws(string path)
    {
        var action = new ValidateWorkspaceAction(new ManifestParser());

        await Should.ThrowAsync<ArgumentException>(
            () => action.ExecuteAsync(new ValidateWorkspaceInput(path), CancellationToken.None));
    }

    /// <summary>
    /// Disposable temp file for tests that need a real on-disk manifest. The
    /// action reads from disk; tests that drive parse/validate happy paths
    /// avoid mocking <see cref="IManifestParser"/> and instead exercise the
    /// real parser against a known-good or known-bad JSON document.
    /// </summary>
    private sealed class TempFile : IDisposable
    {
        public string Path { get; }

        private TempFile(string path) { Path = path; }

        public static TempFile With(string content)
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"weave-test-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, content);
            return new TempFile(path);
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
