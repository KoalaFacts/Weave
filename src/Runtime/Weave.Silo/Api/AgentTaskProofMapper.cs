using Weave.Agents.Models;

namespace Weave.Silo.Api;

internal sealed class AgentTaskProofMapper
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Instance collaborator is kept testable and replaceable from AgentTaskEndpoints.")]
    public ProofOfWork FromRequest(CompleteTaskRequest request)
    {
        return new ProofOfWork
        {
            Items = request.Proof.Select(p => new ProofItem
            {
                Type = p.Type,
                Label = p.Label,
                Value = p.Value,
                Uri = p.Uri
            }).ToList()
        };
    }
}
