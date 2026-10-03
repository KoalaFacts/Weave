using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Weave.Cli.Commands.Local;

internal static class LocalHostConfiguration
{
    public static JsonObject Create(string directory, LocalDeployment deployment)
    {
        var state = Path.Join(directory, "state");
        var profiles = new JsonObject
        {
            ["agent"] = Profile(deployment.Workspace, "document-agent",
                "tool:files:invoke:read_file", "tool:files:invoke:write_file", "invocation:read"),
            ["reviewer"] = Profile(deployment.Workspace, "human-reviewer",
                "invocation:read", "approval:decide", "tool:files:approve:write_file")
        };
        return new JsonObject
        {
            ["Urls"] = deployment.Origin,
            ["Logging"] = new JsonObject { ["LogLevel"] = new JsonObject { ["Default"] = "Warning" } },
            ["CapabilityTokens"] = new JsonObject
            {
                ["SigningKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["RevocationDirectory"] = Path.Join(state, "revocations"),
                ["RequireExistingStorage"] = false
            },
            ["Weave"] = new JsonObject
            {
                ["LocalMode"] = true,
                ["RequireHttps"] = false,
                ["Auth"] = new JsonObject { ["Mode"] = "none" },
                ["Invocations"] = new JsonObject
                {
                    ["DatabasePath"] = Path.Join(state, "invocations.db"),
                    ["RequireExistingStorage"] = false,
                    ["ApprovalRequiredGrants"] = new JsonArray("tool:files:invoke:write_file"),
                    ["ApprovalLifetime"] = "1.00:00:00",
                    ["Http"] = new JsonObject { ["Enabled"] = true, ["AgentOnly"] = false, ["DecisionsEnabled"] = true }
                },
                ["Operator"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["Key"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                    ["Credentials"] = profiles,
                    ["Tools"] = new JsonObject
                    {
                        ["files"] = new JsonObject
                        {
                            ["WorkspaceId"] = deployment.Workspace,
                            ["Tool"] = new JsonObject
                            {
                                ["Name"] = "files",
                                ["Type"] = "FileSystem",
                                ["FileSystem"] = new JsonObject { ["Root"] = deployment.DocumentsPath }
                            }
                        }
                    }
                }
            }
        };
    }

    private static JsonObject Profile(string workspace, string subject, params string[] grants) => new()
    {
        ["WorkspaceId"] = workspace,
        ["IssuedTo"] = subject,
        ["Lifetime"] = "00:30:00",
        ["Grants"] = new JsonArray(grants.Select(grant => (JsonNode?)JsonValue.Create(grant)).ToArray())
    };
}
