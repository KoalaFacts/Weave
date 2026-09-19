namespace Weave.Invocations;

public sealed class InvocationApprovalOptions
{
    public const string ConfigurationSectionName = "Weave:Approvals";
    public bool RequireFileWriteApproval { get; set; }
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(30);
}
