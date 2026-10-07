using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Weave.Mailboxes;

namespace Weave.Mailbox.Host;

internal static class MailboxHttp
{
    internal const int MaximumRequestBytes = 128 * 1024;
    internal static MailboxAuthority Authority(HttpContext context) => context.Features.Get<MailboxControlScope>()!.Authority;
    internal static string Route(HttpContext context, string name) => (string)context.Request.RouteValues[name]!;

    internal static bool ValidIdentity(string identity) => !string.IsNullOrWhiteSpace(identity) && identity.Length <= 128;
    internal static bool IdentityQuery(HttpContext context, string name, out string identity)
    {
        var values = context.Request.Query[name];
        identity = values.Count == 1 ? values[0]! : "";
        return ValidIdentity(identity);
    }
    internal static bool UuidRoute(HttpContext context, string name, out Guid id)
    {
        var value = Route(context, name);
        return Guid.TryParseExact(value, "D", out id) && value == id.ToString("D");
    }

    internal static async Task<T?> ReadAsync<T>(HttpContext context, JsonTypeInfo<T> type) where T : class
    {
        if (!context.Request.HasJsonContentType()) { await ErrorAsync(context, 400, "invalid"); return null; }
        if (context.Request.ContentLength > MaximumRequestBytes) { await ErrorAsync(context, 413, "tooLarge"); return null; }
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) != 0)
        {
            if (buffer.Length + read > MaximumRequestBytes) { await ErrorAsync(context, 413, "tooLarge"); return null; }
            await buffer.WriteAsync(chunk.AsMemory(0, read), context.RequestAborted);
        }
        try
        {
            var value = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), type);
            if (value is null) await ErrorAsync(context, 400, "invalid");
            return value;
        }
        catch (JsonException) { await ErrorAsync(context, 400, "invalid"); return null; }
    }

    internal static Task WriteAsync<T>(HttpContext context, T value, JsonTypeInfo<T> type) =>
        context.Response.WriteAsJsonAsync(value, type, cancellationToken: context.RequestAborted);

    internal static Task ErrorAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new MailboxProblemWire(status, code switch
        {
            "invalid" => "Invalid request", "unauthenticated" => "Mailbox control required",
            "unavailable" => "Resource unavailable", "conflict" => "State conflict", "expired" => "Expired",
            "capacity" => "Capacity reached", "tooLarge" => "Request too large", "storage" => "Storage unavailable",
            _ => throw new ArgumentOutOfRangeException(nameof(code))
        }, code), MailboxJsonContext.Default.MailboxProblemWire, contentType: "application/problem+json", cancellationToken: context.RequestAborted);
    }

    internal static Task ResultAsync<T, TWire>(HttpContext context, MailboxResult<T> result,
        Func<T, TWire> project, JsonTypeInfo<TWire> type) where T : class
    {
        if (result.IsSuccess) return WriteAsync(context, project(result.Value!), type);
        return result.Error switch
        {
            MailboxError.Invalid => ErrorAsync(context, 400, "invalid"),
            MailboxError.Forbidden or MailboxError.Unavailable => ErrorAsync(context, 404, "unavailable"),
            MailboxError.Conflict => ErrorAsync(context, 409, "conflict"),
            MailboxError.Expired => ErrorAsync(context, 410, "expired"),
            MailboxError.Capacity => ErrorAsync(context, 429, "capacity"),
            _ => throw new InvalidOperationException("Missing mailbox outcome.")
        };
    }

    internal static bool Page(HttpContext context, string kind, MailboxAuthority authority, out string? cursor, out int limit)
    {
        cursor = context.Request.Query["afterCursor"].FirstOrDefault();
        limit = 10;
        var query = context.Request.Query["limit"];
        if (query.Count > 1 || context.Request.Query["afterCursor"].Count > 1
            || query.Count == 1 && !int.TryParse(query[0], NumberStyles.None, CultureInfo.InvariantCulture, out limit)
            || limit is < 1 or > 10) return false;
        if (cursor is null) return true;
        var prefix = kind + ":" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(authority.MailboxId.Value)) + ":";
        return cursor.Length <= 1024 && cursor.StartsWith(prefix, StringComparison.Ordinal)
            && long.TryParse(cursor.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var after) && after >= 0;
    }
}
