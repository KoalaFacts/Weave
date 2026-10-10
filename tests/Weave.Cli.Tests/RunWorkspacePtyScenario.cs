namespace Weave.Cli.Tests;

internal static class RunWorkspacePtyScenario
{
    public const string Script = """
        def arguments(executable, root, scenario):
            if scenario == "run-workspace-create":
                return [executable, "run", "unknown-team"]
            if scenario == "run-workspace-selected":
                return [executable, "run", "unknown-team", "--capability-file", str(root / "empty capability.txt")]
            return [executable, "run"]

        def run(scenario, wait_for, answer, choose, output, stages):
            if scenario == "run-workspace-empty":
                wait_for("Workspace name:")
                answer(b"suggested-team\r", "Choose a preset:", "name-entered")
                choose(1, "Then: weave run suggested-team", "suggestion-complete")
                return
            wait_for("Which workspace would you like to run?")
            if scenario == "run-workspace-create":
                choose(1, "Run: weave workspace new <name>", "create-guidance")
            elif scenario == "run-workspace-broken":
                choose(0, "exists but has no workspace.json", "broken-rejected")
            elif scenario == "run-workspace-selected":
                choose(0, "The capability file is empty.", "selected-capability-rejected")
            else:
                raise AssertionError("Unknown run selection scenario: " + scenario)
        """;
}
