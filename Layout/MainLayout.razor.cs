using Microsoft.JSInterop;

namespace CalorieTracker.Layout;

public partial class MainLayout
{
    private bool _updateAvailable;
    private bool _updating;
    private DotNetObjectReference<MainLayout>? _selfRef;

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnChanged;
        await State.EnsureLoadedAsync();
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
        _selfRef?.Dispose();
    }
}
