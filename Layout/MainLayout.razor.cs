using Microsoft.JSInterop;

namespace CalorieTracker.Layout;

public partial class MainLayout
{
    private bool _updateAvailable;
    private bool _updating;
    private DotNetObjectReference<MainLayout>? _selfRef;

    // Data-folder activity ("Logged via MCP: …"), each shown for a few seconds.
    private sealed class Toast(string text) { public string Text { get; } = text; }
    private readonly List<Toast> _toasts = new();

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnChanged;
        State.FolderActivity += OnFolderActivity; // before loading: startup sync may report
        await State.EnsureLoadedAsync();
    }

    private void OnFolderActivity(string message) => _ = ShowToastAsync(message);

    private async Task ShowToastAsync(string message)
    {
        var toast = new Toast(message);
        await InvokeAsync(() => { _toasts.Add(toast); StateHasChanged(); });
        await Task.Delay(message.Length > 120 ? 12000 : 7000);
        await InvokeAsync(() => { _toasts.Remove(toast); StateHasChanged(); });
    }

    private async Task ReconnectFolderAsync()
    {
        var error = await State.ReconnectFolderAsync();
        if (error is not null) _ = ShowToastAsync(error);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            _selfRef = DotNetObjectReference.Create(this);
            await JS.InvokeVoidAsync("calTracker.updates.init", _selfRef);
        }
    }

    [JSInvokable]
    public void OnUpdateAvailable()
    {
        _updateAvailable = true;
        InvokeAsync(StateHasChanged);
    }

    [JSInvokable]
    public void OnUpdateFailed()
    {
        // The clicked update's worker died installing: bring the buttons back so
        // the user can retry, instead of a "reloading in a moment" that never comes.
        _updating = false;
        InvokeAsync(StateHasChanged);
    }

    private async Task ApplyUpdateAsync()
    {
        _updating = true;
        var started = await JS.InvokeAsync<bool>("calTracker.updates.applyUpdate");
        if (!started)
        {
            // Nothing to apply after all (stale banner) — hide it.
            _updating = false;
            _updateAvailable = false;
        }
    }

    private void OnChanged() => InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        State.Changed -= OnChanged;
        State.FolderActivity -= OnFolderActivity;
        _selfRef?.Dispose();
    }
}
