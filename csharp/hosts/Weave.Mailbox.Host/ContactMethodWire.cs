namespace Weave.Mailbox.Host;

public sealed record ContactMethodWire(string MethodId, int Version, string Transport, string Endpoint, string Instructions);
