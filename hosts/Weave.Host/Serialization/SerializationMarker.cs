namespace Weave.Silo.Serialization;

/// <summary>
/// Marker used with <c>AddSerializer(s =&gt; s.AddAssembly(typeof(SerializationMarker).Assembly))</c>
/// so Orleans definitively scans this assembly for <c>[RegisterConverter]</c>
/// types. Not strictly required once <c>Microsoft.Orleans.Sdk</c> emits the
/// <c>[ApplicationPart]</c> attribute on the host assembly, but it's cheap
/// insurance.
/// </summary>
public static class SerializationMarker { }
