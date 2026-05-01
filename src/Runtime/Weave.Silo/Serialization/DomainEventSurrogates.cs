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
