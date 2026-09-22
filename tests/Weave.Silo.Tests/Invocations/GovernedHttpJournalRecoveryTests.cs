using System.Net;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_JournalDisappears_DoesNotCreateReplacementAndRecoversFromRetainedFile(bool replaceWithDirectory)
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var token = fx.Token();
        var original = Request();
        using var first = await fx.SendAsync(HttpMethod.Post, fx.Route, original, token);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.WriteAllText(fx.Target, "external edit");
        // No open transaction/connection: the fixture is quiescent and pooling is disabled.
        File.Exists(fx.DatabasePath + "-wal").ShouldBeFalse();
        var retained = fx.DatabasePath + ".retained";
        File.Move(fx.DatabasePath, retained);
        if (replaceWithDirectory)
            Directory.CreateDirectory(fx.DatabasePath);
        var another = Request();
        try
        {
            using var blocked = await fx.SendAsync(HttpMethod.Post, fx.Route, another, token);
            blocked.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            File.ReadAllText(fx.Target).ShouldBe("external edit");
            File.Exists(fx.DatabasePath).ShouldBeFalse();
        }
        finally
        {
            if (replaceWithDirectory)
                Directory.Delete(fx.DatabasePath);
            // The baseline may have recreated an empty file; only remove this test's replacement.
            if (File.Exists(fx.DatabasePath))
                File.Delete(fx.DatabasePath);
            File.Move(retained, fx.DatabasePath);
        }
        (await fx.Tool.GetInvocationAsync(another.InvocationId!.Value, token)).ShouldBeNull();
        using var duplicate = await fx.SendAsync(HttpMethod.Post, fx.Route, original, token);
        duplicate.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await JsonAsync(duplicate)).GetProperty("isReplay").GetBoolean().ShouldBeTrue();
        File.ReadAllText(fx.Target).ShouldBe("external edit");
    }
}
