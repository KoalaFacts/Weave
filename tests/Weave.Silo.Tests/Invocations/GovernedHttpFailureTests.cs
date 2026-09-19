using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Fact]
    public async Task Post_JournalAdmissionFails_ReturnsUnavailableWithoutWriting()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        ExecuteSql(fx.DatabasePath, "CREATE TRIGGER reject_http_attempt BEFORE INSERT ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private admission detail'); END;");
        using var response = await fx.SendAsync(HttpMethod.Post, fx.Route, Request(), fx.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        var body = await JsonAsync(response);
        body.GetProperty("errorCode").GetString().ShouldBe("journal-write-failed");
        body.GetProperty("outcome").GetString().ShouldBe("NotDispatched");
        body.GetRawText().ShouldNotContain("private admission detail");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    [Fact]
    public async Task Post_CompletionCannotBeRecorded_ReturnsUnknownAndNeverRepeatsWrite()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        ExecuteSql(fx.DatabasePath, "CREATE TRIGGER reject_http_completion BEFORE UPDATE ON invocation_attempts BEGIN SELECT RAISE(ABORT, 'private completion detail'); END;");
        var request = Request();
        using var response = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await JsonAsync(response);
        body.GetProperty("outcome").GetString().ShouldBe("OutcomeUnknown");
        body.GetProperty("outcomeRecorded").GetBoolean().ShouldBeFalse();
        body.GetProperty("errorCode").GetString().ShouldBe("outcome-not-recorded");
        body.GetRawText().ShouldNotContain("private completion detail");
        File.ReadAllText(fx.Target).ShouldBe("reviewed text");
        File.WriteAllText(fx.Target, "subsequent external change");
        using var duplicate = await fx.SendAsync(HttpMethod.Post, fx.Route, request, fx.Token());
        duplicate.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await JsonAsync(duplicate)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("subsequent external change");
        using var query = await fx.SendAsync(HttpMethod.Get, fx.Route + "/" + request.InvocationId, null, fx.Token());
        query.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(query)).GetProperty("outcome").GetString().ShouldBe("OutcomeUnknown");
    }

    [Fact]
    public async Task Post_GlobalAuthenticationEnabled_RequiresBothCredentialBoundaries()
    {
        await using var fx = new Fixture(globalAuthentication: true);
        await fx.ConnectAsync();
        using var missingGlobal = await fx.SendAsync(HttpMethod.Post, fx.Route, Request("read_file"), fx.Token());
        missingGlobal.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fx.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", fx.GlobalApiSecret);
        using var authorized = await fx.SendAsync(HttpMethod.Post, fx.Route, Request("read_file"), fx.Token());
        authorized.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var missingCapability = new HttpRequestMessage(HttpMethod.Post, fx.Route);
        missingCapability.Content = JsonContent.Create(Request(), options: JsonOptions);
        using var rejected = await fx.Client.SendAsync(missingCapability, TestContext.Current.CancellationToken);
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await JsonAsync(rejected)).GetProperty("errorCode").GetString().ShouldBe("invalid-capability");
        File.ReadAllText(fx.Target).ShouldBe("original");
    }

    private static void ExecuteSql(string database, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
