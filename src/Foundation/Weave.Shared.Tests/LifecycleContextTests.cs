using Weave.Shared.Ids;
using Weave.Shared.Lifecycle;

namespace Weave.Shared.Tests;

/// <summary>
/// Record behaviour for <see cref="LifecycleContext"/>. The context
/// flows through lifecycle hooks and grain calls, so equality,
/// default values, and mutation semantics all matter.
/// </summary>
public sealed class LifecycleContextTests
{
    [Fact]
    public void LifecycleContext_requires_workspace_id()
    {
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From("ws-1")
        };

        context.WorkspaceId.Value.ShouldBe("ws-1");
        context.Phase.ShouldBe(default(LifecyclePhase));
        context.AgentName.ShouldBeNull();
        context.ToolName.ShouldBeNull();
        context.Properties.ShouldBeEmpty();
    }

    [Fact]
    public void LifecycleContext_with_all_fields_populated()
    {
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From("ws-2"),
            AgentName = "researcher",
            ToolName = "web-search",
            Phase = LifecyclePhase.AgentActivating,
            Properties = new Dictionary<string, string> { ["trace-id"] = "abc" }
        };

        context.AgentName.ShouldBe("researcher");
        context.ToolName.ShouldBe("web-search");
        context.Phase.ShouldBe(LifecyclePhase.AgentActivating);
        context.Properties["trace-id"].ShouldBe("abc");
    }

    [Fact]
    public void LifecycleContext_with_expression_updates_phase()
    {
        var initial = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From("ws-3"),
            Phase = LifecyclePhase.WorkspaceStarting
        };
        var updated = initial with { Phase = LifecyclePhase.WorkspaceStarted };

        initial.Phase.ShouldBe(LifecyclePhase.WorkspaceStarting);
        updated.Phase.ShouldBe(LifecyclePhase.WorkspaceStarted);
        updated.WorkspaceId.ShouldBe(initial.WorkspaceId);
    }

    [Fact]
    public void LifecycleContext_record_equality_compares_by_value_when_properties_shared()
    {
        // Records with a Dictionary field use EqualityComparer.Default
        // on the field, which is reference equality for Dictionary —
        // distinct empty dictionaries are NOT equal. Sharing the
        // properties dictionary between two records lets us check
        // that the other fields ARE value-compared.
        var ws = WorkspaceId.From("ws-equal");
        var sharedProps = new Dictionary<string, string>();
        var a = new LifecycleContext { WorkspaceId = ws, Phase = LifecyclePhase.WorkspaceStarted, Properties = sharedProps };
        var b = new LifecycleContext { WorkspaceId = ws, Phase = LifecyclePhase.WorkspaceStarted, Properties = sharedProps };

        a.ShouldBe(b);
        a.GetHashCode().ShouldBe(b.GetHashCode());
    }

    [Fact]
    public void LifecycleContext_record_distinct_instances_not_equal_when_properties_differ()
    {
        // Complement to the previous test: when two records have
        // different dictionary INSTANCES (even with the same content),
        // record equality returns false. Worth asserting explicitly so
        // future callers know not to rely on deep equality.
        var ws = WorkspaceId.From("ws-diff-props");
        var a = new LifecycleContext { WorkspaceId = ws, Properties = [] };
        var b = new LifecycleContext { WorkspaceId = ws, Properties = [] };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void LifecycleContext_record_distinguishes_different_phases()
    {
        var ws = WorkspaceId.From("ws-diff");
        var a = new LifecycleContext { WorkspaceId = ws, Phase = LifecyclePhase.WorkspaceStarting };
        var b = new LifecycleContext { WorkspaceId = ws, Phase = LifecyclePhase.WorkspaceStarted };

        a.ShouldNotBe(b);
    }

    [Fact]
    public void LifecycleContext_properties_are_mutable_on_same_instance()
    {
        var context = new LifecycleContext
        {
            WorkspaceId = WorkspaceId.From("ws-4")
        };

        // init-only dictionary instance, but the dictionary contents
        // are mutable — that's the shape hooks use to annotate state.
        context.Properties["trace-id"] = "xyz";
        context.Properties["attempt"] = "1";

        context.Properties.Count.ShouldBe(2);
        context.Properties["trace-id"].ShouldBe("xyz");
    }
}
