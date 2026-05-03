using System.Collections.Frozen;
using System.Reflection;
using Orleans;
using Weave.Silo.VirtualActors;

namespace Weave.Silo.Tests;

/// <summary>
/// Guards <see cref="OrleansVirtualActorProvider.GrainMap"/> against drift:
/// every grain interface in the Silo must be reachable through the domain
/// interface its production callers use. Missing entries cause runtime
/// <see cref="ArgumentException"/> ("Could not find an implementation for
/// interface ...") only when a code path actually exercises the actor —
/// the kind of bug that escapes unit tests and surfaces in integration.
/// </summary>
public sealed class OrleansVirtualActorProviderMapTests
{
    [Fact]
    public void GrainMap_HasEntryForEveryGrainInterface()
    {
        var providerType = typeof(OrleansVirtualActorProvider);
        var mapField = providerType.GetField("GrainMap", BindingFlags.NonPublic | BindingFlags.Static);
        mapField.ShouldNotBeNull();
        var map = (FrozenDictionary<Type, Type>?)mapField.GetValue(null);
        map.ShouldNotBeNull();

        var grainInterfaces = providerType.Assembly.GetTypes()
            .Where(t => t.IsInterface
                && t.Namespace == "Weave.Silo.VirtualActors"
                && typeof(IGrainWithStringKey).IsAssignableFrom(t)
                && t != typeof(IGrainWithStringKey))
            .ToList();

        grainInterfaces.ShouldNotBeEmpty();

        foreach (var grain in grainInterfaces)
        {
            var domainInterfaces = grain.GetInterfaces()
                .Where(i => i.Namespace?.StartsWith("Weave.", StringComparison.Ordinal) == true)
                .ToList();
            domainInterfaces.Count.ShouldBe(1,
                $"Grain {grain.Name} should inherit exactly one Weave domain interface; found: {string.Join(", ", domainInterfaces.Select(d => d.Name))}");

            var domain = domainInterfaces[0];
            map.ShouldContainKey(domain,
                $"OrleansVirtualActorProvider.GrainMap is missing an entry for {domain.Name} -> {grain.Name}. Add it next to the other entries.");
            map[domain].ShouldBe(grain,
                $"OrleansVirtualActorProvider.GrainMap[{domain.Name}] should map to {grain.Name}, not {map[domain].Name}.");
        }
    }
}
