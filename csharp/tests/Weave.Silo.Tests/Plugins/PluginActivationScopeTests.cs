using Weave.Silo.Plugins;

namespace Weave.Silo.Tests.Plugins;

public sealed class PluginActivationScopeTests
{
    [Fact]
    public void Dispose_ActivatedEffects_RevertsInReverseOrderOnce()
    {
        var calls = new List<string>();
        var scope = new PluginActivationScope();
        scope.Add(() => calls.Add("first on"), () => calls.Add("first off"));
        scope.Add(() => calls.Add("second on"), () => calls.Add("second off"));

        scope.Activate();
        scope.Dispose();
        scope.Dispose();

        calls.ShouldBe(["first on", "second on", "second off", "first off"]);
    }

    [Fact]
    public void Activate_PartialRegistrationThrows_RevertsAttemptedEffectAndEarlierEffects()
    {
        var calls = new List<string>();
        var scope = new PluginActivationScope();
        scope.Add(() => calls.Add("first on"), () => calls.Add("first off"));
        scope.Add(() =>
        {
            calls.Add("second started");
            throw new InvalidOperationException("registration failed");
        }, () => calls.Add("second off"));

        Should.Throw<InvalidOperationException>(() => scope.Activate());

        calls.ShouldBe(["first on", "second started", "second off", "first off"]);
    }
}
