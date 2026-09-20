using System.Text;
using System.Text.Json;

namespace Weave.Dashboard.Approvals;

internal sealed record ApprovalReviewInput(string Id, string Method, Dictionary<string, string> Parameters, string? RawInput)
{
    public const int MaxBytes = 1_048_576;

    public static ApprovalReviewInput? Parse(string workspace, string tool, string json)
    {
        if (!Segment(workspace) || !Segment(tool) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            return null;
        using var document = TryParseDocument(json);
        if (document is null)
            return null;
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!names.Add(field.Name) || field.Name is not ("invocationId" or "toolName" or "method" or "parameters" or "rawInput"))
                return null;
        if (!root.TryGetProperty("invocationId", out var id) || id.ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(id.GetString(), "N", out var guid) || guid == Guid.Empty
            || !root.TryGetProperty("toolName", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != tool
            || !root.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(method.GetString())
            || !root.TryGetProperty("parameters", out var parameters) || parameters.ValueKind != JsonValueKind.Object)
            return null;
        var owned = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in parameters.EnumerateObject())
            if (entry.Value.ValueKind != JsonValueKind.String || !owned.TryAdd(entry.Name, entry.Value.GetString()!))
                return null;
        string? raw = null;
        if (root.TryGetProperty("rawInput", out var input))
        {
            if (input.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                return null;
            raw = input.GetString();
        }
        return new(guid.ToString("N"), method.GetString()!, owned, raw);
    }

    public bool Matches(ApprovalReviewSnapshot result, string workspace, string tool) =>
        result.InvocationId == Id && result.WorkspaceId == workspace && result.ToolName == tool
        && string.Equals(result.Operation, Method, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(result.Subject) && !string.IsNullOrWhiteSpace(result.TargetDescription)
        && !string.IsNullOrWhiteSpace(result.PlanDigest) && result.PlanDigest.Length <= 512
        && result.RawInput == RawInput && result.Parameters is not null && result.Parameters.Count == Parameters.Count
        && Parameters.All(pair => result.Parameters.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static JsonDocument? TryParseDocument(string json)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool Segment(string value) => value.Length is > 0 and <= 128
        && value is not "." and not ".."
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}
