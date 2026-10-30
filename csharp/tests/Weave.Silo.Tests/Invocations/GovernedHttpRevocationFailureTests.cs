using System.Net;
using Weave.Invocations;

namespace Weave.Silo.Tests.Invocations;

public sealed partial class GovernedHttpEntryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_RevocationStoreUnavailable_RejectsBeforeFileEffectOrAttempt(bool replacedByFile)
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var token = fx.Token();
        var request = Request();
        var store = Path.Combine(Path.GetDirectoryName(fx.DatabasePath)!, "revocations");
        var savedStore = store + "-retained";
        Directory.Move(store, savedStore);
        if (replacedByFile)
            File.WriteAllText(store, "not a directory");
        try
        {
            using var denied = await fx.SendAsync(HttpMethod.Post, fx.Route, request, token);
            denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            File.ReadAllText(fx.Target).ShouldBe("original");
            Directory.Exists(store).ShouldBeFalse();
        }
        finally
        {
            if (replacedByFile)
                File.Delete(store);
            Directory.Move(savedStore, store);
        }
        (await fx.Tool.GetInvocationAsync(request.InvocationId!.Value, token)).ShouldBeNull();
        using var recovered = await fx.SendAsync(HttpMethod.Post, fx.Route, request, token);
        recovered.StatusCode.ShouldBe(HttpStatusCode.OK);
        File.ReadAllText(fx.Target).ShouldBe("reviewed text");
    }

    [Fact]
    public async Task Post_DirectoryAtRevocationMarker_RejectsBeforeFileEffectOrAttempt()
    {
        await using var fx = new Fixture();
        await fx.ConnectAsync();
        var token = fx.Token();
        var request = Request();
        var marker = Path.Combine(Path.GetDirectoryName(fx.DatabasePath)!, "revocations", token.TokenId + ".revoked");
        Directory.CreateDirectory(marker);

        using var denied = await fx.SendAsync(HttpMethod.Post, fx.Route, request, token);

        denied.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        File.ReadAllText(fx.Target).ShouldBe("original");
        Directory.Delete(marker);
        (await fx.Tool.GetInvocationAsync(request.InvocationId!.Value, fx.Token())).ShouldBeNull();
    }
}
