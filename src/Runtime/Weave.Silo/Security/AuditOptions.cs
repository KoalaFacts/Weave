namespace Weave.Silo.Security;

public sealed class AuditOptions
{
    public const string ConfigSection = "Weave:Audit";

    public bool Enabled { get; set; }

    public static AuditOptions FromConfiguration(IConfiguration configuration)
    {
        var disabled = configuration.GetValue<bool>($"{ConfigSection}:Disabled");
        return new AuditOptions { Enabled = !disabled };
    }
}
