namespace Weave.Cli.Tests;

public sealed class SiloLauncherFixtureEvidenceTests
{
    [Fact]
    public void Save_ArtifactDirectoryIsAFile_PreservesFailureTextWithoutThrowing()
    {
        using var files = new LocalTestDirectory();
        var blocked = Path.Join(files.Root, "blocked");
        File.WriteAllText(blocked, "owned fixture");
        string? report = null;

        var failure = Record.Exception(() => report = SiloLauncherFixtureEvidence.Save(files.Root, blocked, 1, 23,
            "original-observation-failure", "original-stderr", stopped: true));

        failure.ShouldBeNull();
        report.ShouldNotBeNull();
        report.ShouldContain("Evidence persistence failed:");
        report.ShouldContain("original-observation-failure");
        report.ShouldContain("original-stderr");
        File.ReadAllText(blocked).ShouldBe("owned fixture");
    }
}
