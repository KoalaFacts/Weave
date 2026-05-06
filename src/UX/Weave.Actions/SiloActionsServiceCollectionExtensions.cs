using Microsoft.Extensions.DependencyInjection;
using Weave.Actions.Agent;
using Weave.Actions.AgentTask;
using Weave.Actions.SystemInfo;
using Weave.Actions.Tool;

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
        services.AddHttpClient<ListToolsAction>(configureSiloClient);
        services.AddHttpClient<ListTasksAction>(configureSiloClient);

        return services;
    }
}
