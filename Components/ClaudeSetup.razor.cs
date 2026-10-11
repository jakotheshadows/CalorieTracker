using CalorieTracker.Sync;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace CalorieTracker.Components;

public partial class ClaudeSetup
{
    /// <summary>Served next to the app (see the deploy workflow); relative to the base href.</summary>
    private const string DownloadPath = "downloads/caltrack-windows.mcpb";

    /// <summary>The connected folder's name: what to pick in Claude's install dialog.</summary>
    [Parameter, EditorRequired] public string? FolderName { get; set; }

    private bool? _isWindows;
    private ServerStatus? _status;
    private bool _checking;

    protected override async Task OnInitializedAsync()
    {
        _isWindows = await JS.InvokeAsync<bool>("calTracker.install.isWindows");
        if (_isWindows == true) _status = await State.ReadServerStatusAsync();
    }

    /// <summary>After installing: Claude starts the server right away, and it writes its status.</summary>
    private async Task CheckAgainAsync()
    {
        _checking = true;
        _status = await State.ReadServerStatusAsync();
        _checking = false;
    }
}
