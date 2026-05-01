using Weave.Cli.Commands;

namespace Weave.Cli.Tests;

public sealed class StorageConnectionStringsTests
{
    private readonly StorageConnectionStrings _connectionStrings = new();

    [Theory]
    [InlineData("localhost:6379", "localhost", 6379)]
    [InlineData("cache.example.com", "cache.example.com", 6379)]
    [InlineData("cache.example.com:6380,ssl=true", "cache.example.com", 6380)]
    public void ParseHostPort_ConnectionString_ReturnsHostAndPort(string connectionString, string expectedHost, int expectedPort)
    {
        var (host, port) = _connectionStrings.ParseHostPort(connectionString, 6379);

        host.ShouldBe(expectedHost);
        port.ShouldBe(expectedPort);
    }

    [Theory]
    [InlineData("Host=db.example.com;Port=5433;Database=weave", "db.example.com", 5433)]
    [InlineData("Server=db.example.com;Database=weave", "db.example.com", 5432)]
    public void ParseKeyValueHostPort_RelationalConnectionString_ReturnsHostAndPort(string connectionString, string expectedHost, int expectedPort)
    {
        var (host, port) = _connectionStrings.ParseKeyValueHostPort(connectionString, "Host", 5432);

        host.ShouldBe(expectedHost);
        port.ShouldBe(expectedPort);
    }

    [Theory]
    [InlineData("Server=sql.example.com,1434;Database=weave", "sql.example.com", 1434)]
    [InlineData("Data Source=sql.example.com;Initial Catalog=weave", "sql.example.com", 1433)]
    public void ParseSqlServerHostPort_SqlServerConnectionString_ReturnsHostAndPort(string connectionString, string expectedHost, int expectedPort)
    {
        var (host, port) = _connectionStrings.ParseSqlServerHostPort(connectionString);

        host.ShouldBe(expectedHost);
        port.ShouldBe(expectedPort);
    }

    [Fact]
    public void MaskSecret_PasswordConnectionString_RedactsPassword()
    {
        var masked = _connectionStrings.MaskSecret("Host=localhost;Password=secret;Database=weave");

        masked.ShouldBe("Host=localhost;Password=***;Database=weave");
    }

    [Fact]
    public void TryGetSqliteDataSource_DataSourceConnectionString_ReturnsPath()
    {
        var dataSource = _connectionStrings.TryGetSqliteDataSource("Data Source=.weave/workspace.db;Mode=ReadWrite");

        dataSource.ShouldBe(".weave/workspace.db");
    }

    [Fact]
    public void ReplaceSqliteDataSource_DataSourceConnectionString_ReplacesPath()
    {
        var replaced = _connectionStrings.ReplaceSqliteDataSource("Data Source=old.db;Mode=ReadWrite", "new.db");

        replaced.ShouldBe("Data Source=new.db;Mode=ReadWrite");
    }

    [Fact]
    public void ReplaceDatabaseName_RelationalConnectionString_ReplacesDatabaseValues()
    {
        var replaced = _connectionStrings.ReplaceDatabaseName("Server=.;Database=old;Initial Catalog=legacy;User Id=weave", "newdb");

        replaced.ShouldBe("Server=.;Database=newdb;Initial Catalog=newdb;User Id=weave");
    }
}
