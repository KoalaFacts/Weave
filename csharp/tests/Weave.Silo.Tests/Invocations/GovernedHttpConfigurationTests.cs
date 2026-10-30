using Microsoft.AspNetCore.Hosting;

namespace Weave.Silo.Tests.Invocations;

public sealed class GovernedHttpConfigurationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Startup_EnabledWithPublicCurrentOrPreviousKey_Rejects(bool previous)
    {
        await using var parent = new SiloFactory();
        await using var host = parent.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Weave:Invocations:Http:Enabled", "true");
            builder.UseSetting("CapabilityTokens:SigningKey", previous
                ? "isolated-test-key-" + Guid.NewGuid().ToString("N")
                : "weave-development-signing-key-change-me");
            if (previous)
                builder.UseSetting("CapabilityTokens:PreviousSigningKey", "weave-development-signing-key-change-me");
        });
        var error = Should.Throw<InvalidOperationException>(() => host.CreateClient());
        error.Message.ShouldContain("development signing key");
    }
}
