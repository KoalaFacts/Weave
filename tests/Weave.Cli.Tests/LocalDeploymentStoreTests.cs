using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using Shouldly;
using Weave.Cli.Commands.Local;

namespace Weave.Cli.Tests;

public sealed class LocalDeploymentStoreTests
{
    private static readonly string[] AgentGrants = ["tool:files:invoke:read_file", "tool:files:invoke:write_file", "invocation:read"];
    [Fact]
    public void Prepare_NewDirectory_RequiresApprovalAndSeparatesSecrets()
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        store.Prepare(files.Private, files.Documents, files.Host, "onboarding", 9401);
        var config = JsonNode.Parse(File.ReadAllText(LocalDeploymentStore.HostConfigurationPath(files.Private)))!;
        config["Urls"]!.GetValue<string>().ShouldBe("http://127.0.0.1:9401");
        config["Weave"]!["Operator"]!["Enabled"]!.GetValue<bool>().ShouldBeTrue();
        config["Weave"]!["Invocations"]!["ApprovalRequiredGrants"]![0]!.GetValue<string>().ShouldBe("tool:files:invoke:write_file");
        var approvalLifetime = config["Weave"]!["Invocations"]!["ApprovalLifetime"]?.GetValue<string>();
        approvalLifetime.ShouldBe("1.00:00:00");
        foreach (var profile in new[] { "agent", "reviewer" })
            config["Weave"]!["Operator"]!["Credentials"]![profile]!["Lifetime"]!.GetValue<string>().ShouldBe("00:30:00");
        store.SigningKey(files.Private).Length.ShouldBe(64);
        store.OperatorKey(files.Private).ShouldNotBe(store.SigningKey(files.Private));
        File.ReadAllText(Path.Join(files.Private, ".gitignore")).ShouldBe("*\n");
        var agent = config["Weave"]!["Operator"]!["Credentials"]!["agent"]!["Grants"]!.AsArray().Select(grant => grant!.GetValue<string>()).ToArray();
        agent.ShouldBe(AgentGrants);
        store.Load(files.Private).Deployment.ShouldBeNull();
        Directory.GetFiles(files.Documents).ShouldBeEmpty();
        if (OperatingSystem.IsWindows())
        {
            var rules = new DirectoryInfo(files.Private).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                rule.IdentityReference.Value.ShouldNotBe("S-1-1-0");
                rule.IdentityReference.Value.ShouldNotBe("S-1-5-11");
            }
            new DirectoryInfo(files.Private).GetAccessControl().AreAccessRulesProtected.ShouldBeTrue();
        }
        else
            File.GetUnixFileMode(files.Private).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public void Prepare_ExistingDirectory_PreservesKeysAndRejectsReinitialization()
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        store.Prepare(files.Private, files.Documents, files.Host, "onboarding", 9401);
        var key = store.SigningKey(files.Private);
        store.Prepare(files.Private, files.Documents, files.Host, "onboarding", 9401).Error!.ShouldContain("already exists");
        store.SigningKey(files.Private).ShouldBe(key);
    }

    [Fact]
    public void Prepare_DocumentsContainPrivateDirectory_RejectsBeforeWriting()
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        store.Prepare(Path.Join(files.Documents, ".weave"), files.Documents, files.Host, "onboarding", 9401).Deployment.ShouldBeNull();
        Directory.GetFileSystemEntries(files.Documents).ShouldBeEmpty();
    }

    [Fact]
    public void CompleteInitialization_StoresMissing_DoesNotCreateReadyMarker()
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        var deployment = store.Prepare(files.Private, files.Documents, files.Host, "onboarding", 9401).Deployment!;
        store.CompleteInitialization(files.Private, deployment).ShouldBeFalse();
        File.Exists(Path.Join(files.Private, "local.json")).ShouldBeFalse();
    }

    [Fact]
    public void Load_InitializedThenStorageRequirementDisabled_DeniesWithoutChangingFiles()
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        var deployment = store.Prepare(files.Private, files.Documents, files.Host, "onboarding", 9401).Deployment!;
        File.WriteAllText(Path.Join(files.Private, "state", "invocations.db"), "existence fixture only");
        Directory.CreateDirectory(Path.Join(files.Private, "state", "revocations"));
        store.CompleteInitialization(files.Private, deployment);
        store.Load(files.Private).Deployment.ShouldBe(deployment);
        var path = LocalDeploymentStore.HostConfigurationPath(files.Private);
        var config = JsonNode.Parse(File.ReadAllText(path))!;
        config["CapabilityTokens"]!["RequireExistingStorage"] = false;
        File.WriteAllText(path, config.ToJsonString());
        store.Load(files.Private).Error!.ShouldContain("Retained storage must be required");
        JsonNode.Parse(File.ReadAllText(path))!["CapabilityTokens"]!["RequireExistingStorage"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void Prepare_FilesystemRootSelectedAsDocuments_DeniesPrivateStateExposure()
    {
        using var files = new LocalTestDirectory();
        var store = new LocalDeploymentStore();
        var result = store.Prepare(files.Private, Path.GetPathRoot(files.Root)!, files.Host, "onboarding", 9401);
        result.Deployment.ShouldBeNull();
        result.Error!.ShouldContain("separate directories");
        Directory.Exists(files.Private).ShouldBeFalse();
    }
}
