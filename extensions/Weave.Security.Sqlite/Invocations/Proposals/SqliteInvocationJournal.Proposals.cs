using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Weave.Invocations;
using Weave.Tools.Tool;

namespace Weave.Security.Sqlite;

public sealed partial class SqliteInvocationJournal
{
    private const int MaximumProposalBytes = 8_388_608;

    private static void EnsureProposalSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        if (version == 0)
        {
            // A one-time additive upgrade. Do not fabricate contents for historical rows.
            command.CommandText = """
                CREATE TABLE invocation_proposals (
                    workspace_id TEXT NOT NULL, invocation_id TEXT NOT NULL,
                    subject TEXT NOT NULL, tool_name TEXT NOT NULL, operation TEXT NOT NULL,
                    input_digest TEXT NOT NULL, connector_kind TEXT NOT NULL,
                    request_json BLOB NOT NULL CHECK(length(request_json) <= 8388608),
                    content_sha256 TEXT NOT NULL,
                    PRIMARY KEY(workspace_id, invocation_id)
                );
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        else if (version != 1)
        {
            throw new InvalidOperationException("Unsupported invocation proposal schema version.");
        }
        // Once the version is recorded, a missing/corrupt table is a fault, not a new empty store.
        command.CommandText = """
            SELECT workspace_id, invocation_id, subject, tool_name, operation, input_digest,
                connector_kind, request_json, content_sha256 FROM invocation_proposals LIMIT 0;
            """;
        using (var reader = command.ExecuteReader())
            _ = reader.Read();
        transaction.Commit();
    }

    private static void InsertProposal(SqliteConnection connection, SqliteTransaction transaction,
        InvocationRecord candidate, ToolInvocation proposal, string connectorKind)
    {
        if (proposal.InvocationId != candidate.InvocationId || proposal.ToolName != candidate.ToolName
            || proposal.Method != candidate.Operation
            || InvocationFingerprint.ComputeInputDigest(proposal, candidate.WorkspaceId, candidate.Subject, connectorKind) != candidate.InputDigest)
            throw new InvalidOperationException("Proposal does not match its authorized intent.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(StoredProposalInput.FromInvocation(proposal),
            ProposalStorageJsonContext.Default.StoredProposalInput);
        if (bytes.Length > MaximumProposalBytes)
            throw new InvalidOperationException("Proposal storage byte limit exceeded.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO invocation_proposals(workspace_id, invocation_id, subject, tool_name, operation,
                input_digest, connector_kind, request_json, content_sha256)
            VALUES($workspace, $id, $subject, $tool, $operation, $digest, $kind, $request, $hash);
            """;
        AddIdentity(command, candidate.WorkspaceId, candidate.InvocationId);
        command.Parameters.AddWithValue("$subject", candidate.Subject);
        command.Parameters.AddWithValue("$tool", candidate.ToolName);
        command.Parameters.AddWithValue("$operation", candidate.Operation);
        command.Parameters.AddWithValue("$digest", candidate.InputDigest);
        command.Parameters.AddWithValue("$kind", connectorKind);
        command.Parameters.AddWithValue("$request", bytes);
        command.Parameters.AddWithValue("$hash", Convert.ToHexString(SHA256.HashData(bytes)));
        command.ExecuteNonQuery();
    }

    public ToolInvocation? FindProposal(string workspaceId, InvocationId invocationId, string subject,
        string toolName, string operation, string inputDigest, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var connection = Open();
        return ReadProposal(connection, null, workspaceId, invocationId, subject, toolName, operation, inputDigest);
    }

    private static ToolInvocation? ReadProposal(SqliteConnection connection, SqliteTransaction? transaction,
        string workspaceId, InvocationId invocationId, string subject, string toolName, string operation, string inputDigest)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT connector_kind, request_json, content_sha256, length(request_json)
            FROM invocation_proposals WHERE workspace_id=$workspace AND invocation_id=$id
                AND subject=$subject AND tool_name=$tool AND operation=$operation AND input_digest=$digest;
            """;
        AddIdentity(command, workspaceId, invocationId);
        command.Parameters.AddWithValue("$subject", subject);
        command.Parameters.AddWithValue("$tool", toolName);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$digest", inputDigest);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;
        if (reader.GetInt64(3) is < 1 or > MaximumProposalBytes)
            throw new IOException("Stored proposal exceeds supported bounds.");
        var bytes = (byte[])reader.GetValue(1);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != reader.GetString(2))
            throw new IOException("Stored proposal integrity check failed.");
        ToolInvocation request;
        try
        {
            var stored = JsonSerializer.Deserialize(bytes, ProposalStorageJsonContext.Default.StoredProposalInput);
            if (stored is null || stored.Parameters is null)
                throw new IOException("Stored proposal is invalid.");
            request = stored.ToInvocation();
        }
        catch (JsonException)
        {
            throw new IOException("Stored proposal cannot be decoded.");
        }
        if (request.InvocationId != invocationId || request.ToolName != toolName || request.Method != operation
            || InvocationFingerprint.ComputeInputDigest(request, workspaceId, subject, reader.GetString(0)) != inputDigest)
            throw new IOException("Stored proposal does not match retained intent.");
        return request;
    }
}
