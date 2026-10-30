using Weave.Cli.Shell;

namespace Weave.Cli.Tests;

public sealed class CliSecretResolverTests
{
    private readonly CliSecretResolver _resolver = new();

    [Fact]
    public void ResolveReference_PlainValue_ReturnsValue()
    {
        _resolver.ResolveReference("Host=localhost;Database=weave")
            .ShouldBe("Host=localhost;Database=weave");
    }

    [Fact]
    public void ResolveReference_EnvironmentReference_ReturnsEnvironmentValue()
    {
        const string variableName = "WEAVE_TEST_CONNECTION_STRING";
        var previous = Environment.GetEnvironmentVariable(variableName);

        try
        {
            Environment.SetEnvironmentVariable(variableName, "Host=env;Database=weave");

            _resolver.ResolveReference($"env:{variableName}")
                .ShouldBe("Host=env;Database=weave");
        }
        finally
        {
            Environment.SetEnvironmentVariable(variableName, previous);
        }
    }

    [Fact]
    public void ResolveReference_FileReference_ReturnsTrimmedFileContents()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"weave-secret-{Guid.NewGuid():N}.txt");
        File.WriteAllText(filePath, " Host=file;Database=weave \r\n");

        try
        {
            _resolver.ResolveReference($"file:{filePath}")
                .ShouldBe("Host=file;Database=weave");
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Theory]
    [InlineData("postgresql", "env:WEAVE_PG_CONNECTION")]
    [InlineData("sqlserver", "env:WEAVE_SQL_CONNECTION")]
    [InlineData("redis", "env:WEAVE_REDIS_CONNECTION")]
    [InlineData("sqlite", "env:WEAVE_SQLITE_CONNECTION")]
    [InlineData("custom", "env:WEAVE_CONNECTION_STRING")]
    public void ToEnvReference_StorageBackend_ReturnsNamespacedVariable(string backend, string expected)
    {
        _resolver.ToEnvReference(backend).ShouldBe(expected);
    }
}
