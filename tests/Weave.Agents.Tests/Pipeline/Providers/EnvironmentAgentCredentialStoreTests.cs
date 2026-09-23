using Weave.Agents.Pipeline.Providers;

namespace Weave.Agents.Tests.Pipeline.Providers;

public class EnvironmentAgentCredentialStoreTests
{
    [Fact]
    public void GetApiKey_OpenAi_ReadsOpenAiEnvVar()
    {
        var store = new EnvironmentAgentCredentialStore(StubEnv("OPENAI_API_KEY", "sk-test-openai"));

        store.GetApiKey("openai").ShouldBe("sk-test-openai");
    }

    [Fact]
    public void GetApiKey_Anthropic_ReadsAnthropicEnvVar()
    {
        var store = new EnvironmentAgentCredentialStore(StubEnv("ANTHROPIC_API_KEY", "sk-ant-test"));

        store.GetApiKey("anthropic").ShouldBe("sk-ant-test");
    }

    [Fact]
    public void GetApiKey_OpenAi_DoesNotReadAnthropicEnvVar()
    {
        var store = new EnvironmentAgentCredentialStore(StubEnv("ANTHROPIC_API_KEY", "sk-ant-test"));

        store.GetApiKey("openai").ShouldBeNull();
    }

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public void GetApiKey_ReturnsNull_WhenEnvReaderReturnsNull(string provider)
    {
        var store = new EnvironmentAgentCredentialStore(_ => null);

        store.GetApiKey(provider).ShouldBeNull();
    }

    [Theory]
    [InlineData("openai", "OPENAI_API_KEY")]
    [InlineData("anthropic", "ANTHROPIC_API_KEY")]
    public void GetApiKey_ReturnsNull_WhenValueIsWhitespace(string provider, string envVar)
    {
        var store = new EnvironmentAgentCredentialStore(StubEnv(envVar, "   "));

        store.GetApiKey(provider).ShouldBeNull();
    }

    [Fact]
    public void GetApiKey_UnknownProvider_ReturnsNull()
    {
        var store = new EnvironmentAgentCredentialStore(_ => "anything");

        store.GetApiKey("hypothetical-provider").ShouldBeNull();
    }

    private static Func<string, string?> StubEnv(string envVar, string? value) =>
        name => name == envVar ? value : null;
}
