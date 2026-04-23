using Weave.Shared;

namespace Weave.Shared.Tests;

/// <summary>
/// Invariants around the Weave port assignments. The constants drive
/// Silo/Dashboard/Orleans deployment output across every publisher
/// in src/Deployment — changing one here ripples into generated
/// manifests. These tests lock the values in.
/// </summary>
public sealed class WeavePortsTests
{
    [Fact]
    public void WeavePorts_All_contains_every_defined_port()
    {
        WeavePorts.All.Count.ShouldBe(7);
        WeavePorts.All.ShouldContain(p => p.Port == WeavePorts.SiloHttps);
        WeavePorts.All.ShouldContain(p => p.Port == WeavePorts.SiloHttp);
        WeavePorts.All.ShouldContain(p => p.Port == WeavePorts.DashboardHttps);
        WeavePorts.All.ShouldContain(p => p.Port == WeavePorts.DashboardHttp);
        WeavePorts.All.ShouldContain(p => p.Port == WeavePorts.OrleansSilo);
        WeavePorts.All.ShouldContain(p => p.Port == WeavePorts.OrleansGateway);
        WeavePorts.All.ShouldContain(p => p.Port == WeavePorts.Redis);
    }

    [Fact]
    public void WeavePorts_local_dev_all_in_94xx_range()
    {
        // The 94xx range is documented as the collision-free window in
        // CLAUDE.md. Any Silo/Dashboard port outside it is a bug.
        int[] localPorts = [
            WeavePorts.SiloHttps, WeavePorts.SiloHttp,
            WeavePorts.DashboardHttps, WeavePorts.DashboardHttp,
            WeavePorts.OrleansSilo, WeavePorts.OrleansGateway
        ];
        foreach (var port in localPorts)
        {
            port.ShouldBeInRange(9400, 9499);
        }
    }

    [Fact]
    public void WeavePorts_all_port_numbers_are_unique()
    {
        var ports = WeavePorts.All.Select(p => p.Port).ToList();
        ports.Distinct().Count().ShouldBe(ports.Count);
    }

    [Fact]
    public void WeavePorts_all_entries_have_name_and_description()
    {
        foreach (var (name, _, description) in WeavePorts.All)
        {
            name.ShouldNotBeNullOrWhiteSpace();
            description.ShouldNotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void WeavePorts_redis_uses_standard_default()
    {
        // Deviating from 6379 would surprise anyone running a local
        // redis-cli against a Weave workspace.
        WeavePorts.Redis.ShouldBe(6379);
    }
}
