using Microsoft.Data.Sqlite;

namespace Weave.Security.Tests;

public sealed class SqliteNativeSecurityTests
{
    [Fact]
    public void Open_LoadedNativeLibrary_MeetsSecurityBaseline()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        // CVE-2025-6965 is fixed in the native engine, not just the managed wrapper.
        Version.Parse(connection.ServerVersion).ShouldBeGreaterThanOrEqualTo(new Version(3, 50, 2));
    }
}
