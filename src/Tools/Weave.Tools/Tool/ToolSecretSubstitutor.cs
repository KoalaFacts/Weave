using Weave.Security.Actors;
namespace Weave.Tools.Tool;

internal sealed class ToolSecretSubstitutor
{
    private readonly IVirtualActorProvider _actors;

    public ToolSecretSubstitutor(IVirtualActorProvider actors) => _actors = actors;

    public async Task<ToolInvocation> SubstituteAsync(string workspaceId, ToolInvocation invocation)
    {
        var proxy = _actors.GetActor<ISecretProxyActor>(VirtualActorId.From(workspaceId));
        var parameters = new Dictionary<string, string>(invocation.Parameters.Count, StringComparer.Ordinal);
        foreach (var (key, value) in invocation.Parameters)
            parameters[key] = await proxy.SubstituteAsync(value);

        return invocation with
        {
            Parameters = parameters,
            RawInput = invocation.RawInput is null ? null : await proxy.SubstituteAsync(invocation.RawInput)
        };
    }
}
