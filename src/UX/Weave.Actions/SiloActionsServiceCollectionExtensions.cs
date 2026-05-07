using Microsoft.Extensions.DependencyInjection;
using Weave.Actions.Agent;
using Weave.Actions.AgentTask;
using Weave.Actions.Channel;
using Weave.Actions.Config;
using Weave.Actions.Dashboard;
using Weave.Actions.Marketplace;
using Weave.Actions.Skill;
using Weave.Actions.SystemInfo;
using Weave.Actions.Template;
using Weave.Actions.Tool;
using Weave.Actions.Workspace;

namespace Weave.Actions;

/// <summary>
/// Composition seam for the Shape C action layer. Frontends call
/// <see cref="AddSiloActions"/> once, supplying the silo's base-URL
/// configurator; the registration registers a typed <see cref="HttpClient"/>
/// per action class plus the action services themselves. Frontends still
/// register their own <see cref="Context.IActionPrompter"/> /
/// <see cref="Context.IActionReporter"/> separately — those impls are
/// frontend-shaped (Spectre vs. browser vs. test fake) and don't belong here.
/// </summary>
public static class SiloActionsServiceCollectionExtensions
{
    public static IServiceCollection AddSiloActions(
        this IServiceCollection services,
        Action<HttpClient> configureSiloClient)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configureSiloClient);

        services.AddHttpClient<GetSystemInfoAction>(configureSiloClient);
        services.AddHttpClient<ListAgentsAction>(configureSiloClient);
        services.AddHttpClient<SendMessageAction>(configureSiloClient);
        services.AddHttpClient<ListToolsAction>(configureSiloClient);
        services.AddHttpClient<ListTasksAction>(configureSiloClient);
        services.AddHttpClient<GetWorkspaceStatusAction>(configureSiloClient);
        services.AddHttpClient<ValidateWorkspaceAction>(configureSiloClient);
        services.AddHttpClient<StartWorkspaceAction>(configureSiloClient);
        services.AddHttpClient<StopWorkspaceAction>(configureSiloClient);

        // Phase 4b — workspace data-roundtrip verbs. Return opaque JsonElement
        // for wire fidelity; the only callers today are workspace export/import.
        services.AddHttpClient<ListSkillsAction>(configureSiloClient);
        services.AddHttpClient<PostSkillAction>(configureSiloClient);
        services.AddHttpClient<ListChannelsAction>(configureSiloClient);
        services.AddHttpClient<PostChannelAction>(configureSiloClient);
        services.AddHttpClient<ListTemplatesAction>(configureSiloClient);
        services.AddHttpClient<ListMarketplaceItemsAction>(configureSiloClient);

        // The dashboard URL is not the silo URL — the action passes absolute
        // URIs to GetAsync so the BaseAddress here is unused but harmless.
        services.AddHttpClient<GetDashboardStatusAction>(configureSiloClient);

        // Config-only actions don't talk to the silo; registered as transient
        // because they depend only on frontend-supplied snapshots / writers.
        services.AddTransient<GetConfigAction>();
        services.AddTransient<SetConfigAction>();

        // Local-only verbs that compose other actions / use frontend-supplied
        // seams (locator + prompter). No HTTP client of their own.
        services.AddTransient<OpenWorkspaceAction>();
        services.AddTransient<SelectAgentAction>();
        services.AddTransient<WatchWorkspaceAction>();

        return services;
    }
}
