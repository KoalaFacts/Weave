using Microsoft.Extensions.Logging.Abstractions;
using Weave.Agents.Pipeline.Providers;

namespace Weave.Agents.Tests.Pipeline.Providers;

public class AgentSecretResolverTests
{
    [Fact]
    public async Task ResolveAsync_NullPlaceholder_ReturnsNull()
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync(null, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_EmptyPlaceholder_ReturnsNull()
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync("", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_LiteralValue_ReturnsNull_BecauseHardcodedKeysNotAllowed()
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync("sk-literal-key", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_EnvBackend_ReadsEnvironmentVariable()
    {
        const string varName = "WEAVE_TEST_AGENTSECRET_<<MARKER>>";
        const string varValue = "<<MARKER-RESOLVED-VALUE>>";
        Environment.SetEnvironmentVariable(varName, varValue);
        try
        {
            var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

            var result = await resolver.ResolveAsync("{secret:env/" + varName + "}", TestContext.Current.CancellationToken);

            result.ShouldBe(varValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(varName, null);
        }
    }

    [Fact]
    public async Task ResolveAsync_EnvBackend_MissingVariable_ReturnsNull()
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync("{secret:env/WEAVE_TEST_NEVER_SET_VAR_<<UNIQUE>>}", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_VaultBackend_DefersAndReturnsNull()
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync("{secret:vault/anthropic/api-key}", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_UnknownBackend_ReturnsNull()
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync("{secret:keyvault/some/path}", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Theory]
    [InlineData("{secret:}")]
    [InlineData("{secret:env/}")]
    [InlineData("{secret:/X}")]
    [InlineData("{secret:noSlashAtAll}")]
    public async Task ResolveAsync_MalformedPlaceholder_ReturnsNull(string placeholder)
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync(placeholder, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveAsync_PlaceholderMissingClosingBrace_ReturnsNull()
    {
        var resolver = new AgentSecretResolver(NullLogger<AgentSecretResolver>.Instance);

        var result = await resolver.ResolveAsync("{secret:env/MY_VAR", TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }
}
