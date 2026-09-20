using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Weave.Dashboard.Approvals;

/// <summary>One Dashboard circuit's read-only preview; credentials are never instance state.</summary>
public sealed class ApprovalReviewSession(HttpClient http, TimeProvider clock) : IDisposable
{
    private const int MaxResponseBytes = 8_388_608;
    private readonly Lock _gate = new();
    private CancellationTokenSource? _pending;
    private long _revision;
    private bool _disposed;

    public ApprovalReviewSnapshot? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public bool Loading { get; private set; }
    public bool IsExpired => Snapshot is { } value && clock.GetUtcNow() >= value.ExpiresAt;

    public void Reset()
    {
        lock (_gate)
        {
            _revision++;
            _pending?.Cancel();
            Snapshot = null;
            Error = null;
            Loading = false;
        }
    }

    public async Task LoadAsync(string workspace, string tool, string originalJson, string capability,
        string globalBearer, CancellationToken cancellationToken)
    {
        long revision;
        CancellationTokenSource pending;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Reset();
            revision = _revision;
            pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _pending = pending;
            Loading = true;
        }
        try
        {
            if (http.BaseAddress is null || !Credential(capability, 16384) || !Credential(globalBearer, 8192, optional: true))
            {
                Fail("Configure the review server and provide a valid reviewer credential.");
                return;
            }
            var input = ApprovalReviewInput.Parse(workspace, tool, originalJson);
            if (input is null)
            {
                Fail("Use the complete original invocation JSON, matching tool and a nonempty 32-hex invocation ID (maximum 1 MiB).");
                return;
            }
            var route = $"api/workspaces/{Uri.EscapeDataString(workspace)}/tools/{Uri.EscapeDataString(tool)}/invocations/{input.Id}/approval/review";
            using var request = new HttpRequestMessage(HttpMethod.Post, route);
            request.Headers.Add("X-Weave-Capability", capability);
            if (globalBearer.Length != 0)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", globalBearer);
            request.Content = new StringContent(originalJson, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, pending.Token);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                Fail(response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Authentication failed. Re-enter the reviewer credential and any required API token.",
                    HttpStatusCode.Forbidden => "Review denied. An independent reviewer needs read, decision and exact-operation approval authority.",
                    HttpStatusCode.NotFound => "The pending approval or review route is unavailable.",
                    HttpStatusCode.Conflict => "The request is changed, decided, expired, disconnected or cannot be safely previewed. Verify the original request again.",
                    _ => "The server did not return a verified review. No approval or execution was requested."
                });
                return;
            }
            if (response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentLength is > MaxResponseBytes)
            {
                Fail("The review response is invalid or too large.");
                return;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(pending.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(0,
                (int)Math.Min(chunk.Length, MaxResponseBytes + 1L - buffer.Length)), pending.Token)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes)
                {
                    Fail("The review response is invalid or too large.");
                    return;
                }
            }
            var snapshot = JsonSerializer.Deserialize(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)),
                ApprovalReviewJsonContext.Default.ApprovalReviewSnapshot);
            if (snapshot is null || !input.Matches(snapshot, workspace, tool) || clock.GetUtcNow() >= snapshot.ExpiresAt)
            {
                Fail("The returned snapshot does not match this request or has expired. Nothing is approved.");
                return;
            }
            lock (_gate)
            {
                if (revision == _revision && !_disposed && !pending.IsCancellationRequested)
                    Snapshot = snapshot;
            }
        }
        catch (OperationCanceledException)
        {
            Fail("Review cancelled or timed out. Verify again; no decision was requested.");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        {
            // Never display upstream bodies, exception details, request content or credentials.
            Fail("Unable to obtain a verified review. Check the configured server and verify again.");
        }
        finally
        {
            lock (_gate)
            {
                if (revision == _revision)
                    Loading = false;
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
                pending.Dispose();
            }
        }
        return;

        void Fail(string message)
        {
            lock (_gate)
            {
                if (revision == _revision && !_disposed)
                    Error = message;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            Reset();
            http.Dispose();
        }
    }

    private static bool Credential(string value, int limit, bool optional = false) =>
        (optional || value.Length != 0) && value.Length <= limit && value.All(c => c is >= '!' and <= '~');
}
