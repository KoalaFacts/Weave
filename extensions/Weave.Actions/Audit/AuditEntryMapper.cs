namespace Weave.Actions.Audit;

internal static class AuditEntryMapper
{
    public static CapabilityAuditEntry ToEntry(CapabilityAuditEntryWire w) =>
        new()
        {
            TokenId = w.TokenId,
            Grant = w.Grant,
            IssuedTo = w.IssuedTo,
            WorkspaceId = w.WorkspaceId,
            ActionContext = w.ActionContext,
            Outcome = w.Outcome,
            Reason = w.Reason,
            Timestamp = w.Timestamp
        };

    public static IReadOnlyList<CapabilityAuditEntry> ToEntries(IReadOnlyList<CapabilityAuditEntryWire> wires)
    {
        var entries = new CapabilityAuditEntry[wires.Count];
        for (var i = 0; i < wires.Count; i++)
            entries[i] = ToEntry(wires[i]);
        return entries;
    }
}
