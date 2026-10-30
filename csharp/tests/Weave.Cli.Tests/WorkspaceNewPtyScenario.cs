namespace Weave.Cli.Tests;

internal static class WorkspaceNewPtyScenario
{
    public const string Script = """
        def arguments(executable, root, scenario):
            command = [executable, "workspace", "new"]
            if scenario == "workspace-new-no-authority":
                return command
            if scenario == "workspace-new-explicit-authority":
                return command + ["team with spaces", "--path", str(root / "destination with spaces")]
            if scenario == "workspace-new-no-tools":
                return command + ["no-tools"]
            if scenario == "workspace-new-preset":
                return command + ["preset-team"]
            raise AssertionError("Unknown workspace-new scenario: " + scenario)

        def run(scenario, wait_for, answer, choose, output, stages):
            if scenario == "workspace-new-no-authority":
                wait_for("Workspace name:")
                answer(b"\r", "Choose a preset:", "default-name")
            else:
                wait_for("Choose a preset:")
            if scenario == "workspace-new-preset":
                choose(0, 'Workspace "preset-team" created.', "preset-created")
                if "Select a model" in output():
                    raise AssertionError("Preset selection unexpectedly entered custom configuration")
                return

            choose(5, "Select a model for your assistant:", "custom-preset")
            tools_prompt = "Which tools should the assistant have access to?"
            if scenario == "workspace-new-no-authority":
                choose(2, "Enter the model name:", "custom-model-selected")
                answer(b"fixture-model\r", tools_prompt, "custom-model-entered")
            elif scenario == "workspace-new-explicit-authority":
                choose(1, tools_prompt, "gpt-model")
            else:
                choose(0, tools_prompt, "default-model")

            if scenario == "workspace-new-no-tools":
                answer(b"\r", "Workspace isolation level:", "no-tools")
                choose(2, 'Workspace "no-tools" created.', "none-created")
                if "Allow ALL operations" in output():
                    raise AssertionError("No tools must not prompt for invocation authority")
                return

            answer(b" \x1b[B \r", "Workspace isolation level:", "git-file-available")
            grant_prompt = "Allow ALL operations for which tools?"
            if scenario == "workspace-new-no-authority":
                choose(0, grant_prompt, "full-isolation")
                answer(b"\r", 'Workspace "my-workspace" created.', "no-grants-created")
            else:
                choose(1, grant_prompt, "shared-isolation")
                answer(b"\x1b[B \r", 'Workspace "team with spaces" created.', "file-grant-created")
        """;
}
