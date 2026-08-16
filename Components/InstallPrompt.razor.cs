using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CalorieTracker.Components;

public partial class InstallPrompt
{
    /// <summary>Compact mode (Settings): never dismissible, ignores the dismissed flag.</summary>
    [Parameter] public bool Compact { get; set; }

    private const string DismissedKey = "caltrack-install-dismissed";

    private bool _visible;
    private bool _howOpen;
    private string _tab = "desktop";

    private record InstallState(bool Standalone, string Platform);

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        var state = await JS.InvokeAsync<InstallState>("calTracker.install.getState");
        _tab = state.Platform is "ios" or "android" ? state.Platform : "desktop";
        var dismissed = !Compact && await Store.GetAsync(DismissedKey) == "1";
        _visible = !state.Standalone && !dismissed;
        StateHasChanged();
    }

    private async Task DismissAsync()
    {
        _visible = false;
        await Store.SetAsync(DismissedKey, "1");
    }
}
