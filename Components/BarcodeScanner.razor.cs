using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CalorieTracker.Components;

public partial class BarcodeScanner
{
    [Inject] private IJSRuntime JS { get; set; } = default!;

    [Parameter] public EventCallback<string> OnDetected { get; set; }
    [Parameter] public EventCallback OnClose { get; set; }

    private readonly string _videoId = $"scanner-{Guid.NewGuid():N}";
    private string? _error;
    private string _manualCode = "";
    private string? _cameraInfo;
    private string? _status;
    private string? _selectedCameraId;
    private List<CameraDevice> _cameras = new();
    private DotNetObjectReference<BarcodeScanner>? _selfRef;

    public sealed class CameraDevice
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _selfRef = DotNetObjectReference.Create(this);
        // Desktops often expose virtual cameras (OBS etc.) the browser may grab by
        // default; a remembered explicit choice wins, with JS falling back if gone.
        _selectedCameraId = await JS.InvokeAsync<string?>("calTracker.storage.get", "caltrack-scanner-camera");
        if (string.IsNullOrEmpty(_selectedCameraId)) _selectedCameraId = null;
        _error = await JS.InvokeAsync<string?>("calTracker.scanner.start", _videoId, _selfRef, _selectedCameraId);
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnBarcodeDetected(string code) => OnDetected.InvokeAsync(code);

    [JSInvokable]
    public void OnScanStatus(string stage, int n)
    {
        // The deep decode runs for tens of seconds; without a live stage line,
        // "working on it" and "seeing nothing" are indistinguishable to the user.
        _status = stage switch
        {
            "locked" => n < 4
                ? $"Barcode found — hold still… capturing frames ({n}/4)"
                : $"Barcode found — {n} frames captured, hold still…",
            "reading" => "Reading… hold still.",
            "analyzing" => $"Deep read: analyzing {n} frames — up to ~20 s. Keep the code in view.",
            "noread" => "That pass wasn't certain enough — still trying. Adjust distance or angle slightly.",
            _ => null, // "searching" falls back to the default guidance line
        };
        StateHasChanged();
    }

    [JSInvokable]
    public async Task OnCameraReady(int width, int height)
    {
        // Surface the negotiated capture resolution and the device list, so a wrong
        // camera or a low-res mode is visible instead of silently hurting decoding.
        _cameraInfo = width > 0 ? $"{width}×{height}" : null;
        var cams = await JS.InvokeAsync<CameraDevice[]>("calTracker.scanner.listCameras");
        _cameras = cams.ToList();
        StateHasChanged();
    }

    private async Task OnCameraChangedAsync(ChangeEventArgs e)
    {
        var id = e.Value?.ToString();
        _selectedCameraId = string.IsNullOrEmpty(id) ? null : id;
        await JS.InvokeVoidAsync("calTracker.storage.set", "caltrack-scanner-camera", _selectedCameraId ?? "");
        _cameraInfo = null;
        _status = null; // the old session's "hold still/analyzing" must not outlive it
        _error = await JS.InvokeAsync<string?>("calTracker.scanner.start", _videoId, _selfRef, _selectedCameraId);
        StateHasChanged();
    }

    private async Task SaveFrameAsync()
    {
        // Downloads the current camera frame locally, so a frame the scanner fails on
        // can be shared for decoder tuning.
        await JS.InvokeVoidAsync("calTracker.scanner.saveFrame");
    }

    private async Task SubmitManualAsync()
    {
        var code = _manualCode.Trim();
        if (code.Length == 0) return;
        await JS.InvokeVoidAsync("calTracker.scanner.stop");
        await OnDetected.InvokeAsync(code);
    }

    private async Task CloseAsync()
    {
        await JS.InvokeVoidAsync("calTracker.scanner.stop");
        await OnClose.InvokeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        try { await JS.InvokeVoidAsync("calTracker.scanner.stop"); } catch { /* page teardown */ }
        _selfRef?.Dispose();
    }
}
