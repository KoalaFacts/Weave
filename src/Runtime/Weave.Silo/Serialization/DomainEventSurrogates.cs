using Weave.Shared.Events;

namespace Weave.Silo.Serialization;

// ── DomainEvent (polymorphic base) ──────────────────────────────
//
// DomainEvent is abstract and is the base of every domain event in
// the system. The surrogate + IConverter + IPopulator trio lets
// Orleans serialize the base fields of any derived event record
// without DomainEvent itself needing [GenerateSerializer].
//
// On serialize:  IConverter.ConvertToSurrogate copies base fields.
// On deserialize: Orleans instantiates the derived type via its own
//                 generated activator, then calls IPopulator.Populate
//                 to fill in the base fields.

[GenerateSerializer]
public struct DomainEventSurrogate
{
    [Id(0)] public string EventId { get; set; }
    [Id(1)] public DateTimeOffset Timestamp { get; set; }
    [Id(2)] public string SourceId { get; set; }
}

[RegisterConverter]
public sealed class DomainEventSurrogateConverter
    : IConverter<DomainEvent, DomainEventSurrogate>,
      IPopulator<DomainEvent, DomainEventSurrogate>
{
    // DomainEvent is abstract — deserialization always lands on a
    // derived instance that Orleans already instantiated. This
    // method is never called at runtime but must exist to satisfy
    // the interface.
    public DomainEvent ConvertFromSurrogate(in DomainEventSurrogate surrogate) =>
        throw new NotSupportedException(
            "DomainEvent is abstract; deserialization goes through IPopulator on the derived instance.");

    public DomainEventSurrogate ConvertToSurrogate(in DomainEvent value) => new()
    {
        EventId = value.EventId,
        Timestamp = value.Timestamp,
        SourceId = value.SourceId
    };

    // Populates the inherited init-only base fields on an
    // already-constructed derived instance. We use UnsafeAccessor
    // (AOT-safe, resolved at compile time) to write the compiler-
    // generated backing fields without boxing or reflection.
    public void Populate(in DomainEventSurrogate surrogate, DomainEvent value)
    {
        EventIdField(value) = surrogate.EventId;
        TimestampField(value) = surrogate.Timestamp;
        SourceIdField(value) = surrogate.SourceId;
    }

    [System.Runtime.CompilerServices.UnsafeAccessor(
        System.Runtime.CompilerServices.UnsafeAccessorKind.Field,
        Name = "<EventId>k__BackingField")]
    private static extern ref string EventIdField(DomainEvent target);

    [System.Runtime.CompilerServices.UnsafeAccessor(
        System.Runtime.CompilerServices.UnsafeAccessorKind.Field,
        Name = "<Timestamp>k__BackingField")]
    private static extern ref DateTimeOffset TimestampField(DomainEvent target);

    [System.Runtime.CompilerServices.UnsafeAccessor(
        System.Runtime.CompilerServices.UnsafeAccessorKind.Field,
        Name = "<SourceId>k__BackingField")]
    private static extern ref string SourceIdField(DomainEvent target);
}
