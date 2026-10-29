using System.Text.Json.Serialization;

namespace Weave.Security.Tokens;

/// <summary>
/// Who a capability token is issued to. The kind is part of the signed payload,
/// so a holder cannot change it. Only <see cref="Human"/> principals may decide
/// approvals; no grant can substitute for that.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrincipalKind>))]
public enum PrincipalKind
{
    Agent = 0,
    Human = 1
}
