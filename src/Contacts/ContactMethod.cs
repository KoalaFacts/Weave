namespace Weave.Contacts;

public sealed record ContactMethod(string MethodId, int Version, string Transport, string Endpoint, string Instructions);
