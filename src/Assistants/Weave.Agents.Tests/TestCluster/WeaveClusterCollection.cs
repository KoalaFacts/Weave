namespace Weave.Agents.Tests.TestCluster;

// xUnit requires the CollectionDefinition class to expose ICollectionFixture<T>;
// the "Collection" suffix is the xUnit convention, not a naming smell — suppress CA1711.
[CollectionDefinition(nameof(WeaveClusterCollection))]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "xUnit collection-fixture convention")]
public sealed class WeaveClusterCollection : ICollectionFixture<WeaveTestCluster>;
