using System.Net;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalHttpTests
{
    [Fact]
    public async Task CallAsync_BodyStallsAfterHeaders_DeadlineCancelsBodyRead()
    {
        using var stream = new CancellationBoundStream();
        using var handler = new LocalHttpFixture((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:9401") };
        var clock = new FakeTimeProvider();
        var pending = new LocalHttp(client, clock).CallAsync(HttpMethod.Get, "/api/workspaces/onboarding/tools/files/invocations/82c07b3d2a3646e88f2f0b8db07c452b",
            "agent", null, null, TestContext.Current.CancellationToken);
        await stream.Reading.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(15));
        await Should.ThrowAsync<OperationCanceledException>(() => pending);
        handler.Requests.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("http://localhost:9401")]
    [InlineData("http://127.0.0.1:9401/private")]
    [InlineData("http://user:password@127.0.0.1:9401")]
    [InlineData("http://127.0.0.1:9401?redirect=1")]
    public void CreateClient_NoncanonicalLocalOrigin_Rejects(string origin) =>
        Should.Throw<ArgumentException>(() => LocalHttp.CreateClient(origin));
}
