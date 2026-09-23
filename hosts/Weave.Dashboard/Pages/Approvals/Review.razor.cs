using System.Text;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Weave.Dashboard.Approvals;

namespace Weave.Dashboard.Pages.Approvals;

public sealed partial class Review : ComponentBase, IAsyncDisposable
{
    [Inject] private ApprovalReviewOptions Options { get; set; } = default!;
    [Inject] private ApprovalReviewSession Session { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _upload;
    private Task? _ticks;
    private string _workspace = string.Empty;
    private string _tool = string.Empty;
    private string _capability = string.Empty;
    private string _globalBearer = string.Empty;
    private string? _originalJson;
    private string? _fileError;
    private string _fileStatus = "No file loaded. The journal cannot recover a lost request body.";
    private int _fileVersion;
    private int _pickerVersion;
    private bool _uploading;

    private string Workspace { get => _workspace; set { _workspace = value; Session.Reset(); } }
    private string Tool { get => _tool; set { _tool = value; Session.Reset(); } }
    private string Capability { get => _capability; set { _capability = value; Session.Reset(); } }
    private string GlobalBearer { get => _globalBearer; set { _globalBearer = value; Session.Reset(); } }

    protected override void OnInitialized() => _ticks = RefreshClockAsync();

    private async Task RefreshClockAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), Clock);
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token))
                if (Session.Snapshot is not null)
                    await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Navigation/disposal ends the local clock; no background polling is performed.
        }
    }

    private async Task ReadFileAsync(InputFileChangeEventArgs args)
    {
        // Preserve this InputFile element while its selected stream is being read.
        Reset(resetPicker: false);
        var version = _fileVersion;
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _upload = pending;
        _uploading = true;
        try
        {
            var file = args.File;
            await using var stream = file.OpenReadStream(ApprovalReviewInput.MaxBytes, pending.Token);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, pending.Token);
            if (version == _fileVersion && !pending.IsCancellationRequested)
            {
                _originalJson = new UTF8Encoding(false, true).GetString(buffer.ToArray()).TrimStart('\uFEFF');
                _fileStatus = $"Loaded {file.Name} ({buffer.Length} bytes). Not yet verified; no file is saved by the Dashboard.";
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested)
        {
            // A later selection or Clear invalidates this upload.
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException or InvalidOperationException)
        {
            if (version == _fileVersion)
                _fileError = "Unable to read the file. Select a UTF-8 invocation JSON file no larger than 1 MiB.";
        }
        finally
        {
            if (ReferenceEquals(_upload, pending))
                _upload = null;
            if (version == _fileVersion)
                _uploading = false;
        }
    }

    private async Task VerifyAsync()
    {
        if (_originalJson is null || _uploading)
            return;
        var capability = _capability;
        var bearer = _globalBearer;
        _capability = string.Empty;
        _globalBearer = string.Empty;
        _fileError = null;
        await Session.LoadAsync(_workspace, _tool, _originalJson, capability, bearer, _lifetime.Token);
    }

    private void Clear() => Reset(resetPicker: true);

    private void Reset(bool resetPicker)
    {
        _fileVersion++;
        if (resetPicker)
            _pickerVersion++;
        _upload?.Cancel();
        _uploading = false;
        _originalJson = null;
        _capability = string.Empty;
        _globalBearer = string.Empty;
        _fileError = null;
        _fileStatus = "No file loaded. Select the retained original JSON request.";
        Session.Reset();
    }

    public async ValueTask DisposeAsync()
    {
        Clear();
        await _lifetime.CancelAsync();
        if (_ticks is not null)
            await _ticks;
        _lifetime.Dispose();
    }
}
