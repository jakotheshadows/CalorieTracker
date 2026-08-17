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
    private DotNetObjectReference<BarcodeScanner>? _selfRef;

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        _selfRef = DotNetObjectReference.Create(this);
        _error = await JS.InvokeAsync<string?>("calTracker.scanner.start", _videoId, _selfRef);
        StateHasChanged();
    }

    [JSInvokable]
    public Task OnBarcodeDetected(string code) => OnDetected.InvokeAsync(code);

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
