using Microsoft.AspNetCore.Components.Forms;
using Microsoft.JSInterop;

namespace CalorieTracker.Pages;

public partial class Settings
{
    private string? _importError;
    private bool _importOk;
    private bool _confirmClear;
    private bool _checkingUpdate;
    private string? _updateStatus;
    private string? _version;
    private string _usdaKey = "";
    private string? _usdaKeyStatus;
    private bool _testingKey;
    private bool _folderBusy;
    private string? _folderError;
    private bool _standalone = true;

    protected override async Task OnInitializedAsync()
    {
        State.Changed += OnStateChanged; // folder state can change from the layout banner or a sync tick
        _version = await JS.InvokeAsync<string>("calTracker.getVersion");
        _usdaKey = await Usda.GetApiKeyAsync() ?? "";
        _standalone = await JS.InvokeAsync<bool>("calTracker.install.isStandalone");
    }

    private void OnStateChanged() => InvokeAsync(StateHasChanged);

    public void Dispose() => State.Changed -= OnStateChanged;

    private async Task ConnectFolderAsync()
    {
        _folderBusy = true;
        _folderError = await State.ConnectFolderAsync();
        _folderBusy = false;
    }

    private async Task ReconnectFolderAsync()
    {
        _folderBusy = true;
        _folderError = await State.ReconnectFolderAsync();
        _folderBusy = false;
    }

    private async Task DisconnectFolderAsync()
    {
        _folderError = null;
        await State.DisconnectFolderAsync();
    }

    private async Task SaveUsdaKeyAsync()
    {
        await Usda.SetApiKeyAsync(_usdaKey);
        _usdaKeyStatus = string.IsNullOrWhiteSpace(_usdaKey) ? "Key removed." : "Key saved.";
    }

    private async Task TestUsdaKeyAsync()
    {
        _testingKey = true;
        _usdaKeyStatus = "Testing…";
        var (ok, message) = await Usda.TestKeyAsync(_usdaKey.Trim());
        _usdaKeyStatus = message;
        if (ok) await Usda.SetApiKeyAsync(_usdaKey);
        _testingKey = false;
    }

    private async Task CheckUpdatesAsync()
    {
        _checkingUpdate = true;
        _updateStatus = "Checking…";
        var found = await JS.InvokeAsync<bool?>("calTracker.updates.checkNow");
        _updateStatus = found switch
        {
            true => "Update found — it's downloading; the update banner will appear shortly.",
            false => "You're on the latest version.",
            _ => "Couldn't check for updates (offline?).",
        };
        _checkingUpdate = false;
    }

    private async Task ExportAsync() =>
        await Store.DownloadFileAsync($"caltrack-export-{DateTime.Now:yyyyMMdd}.json", State.ExportJson());

    private async Task ImportAsync(InputFileChangeEventArgs e)
    {
        _importError = null;
        _importOk = false;
        try
        {
            using var reader = new StreamReader(e.File.OpenReadStream(maxAllowedSize: 20 * 1024 * 1024));
            var json = await reader.ReadToEndAsync();
            _importError = await State.ImportJsonAsync(json);
            _importOk = _importError is null;
        }
        catch (Exception ex)
        {
            _importError = "Import failed: " + ex.Message;
        }
    }

    private async Task ClearAllAsync()
    {
        _confirmClear = false;
        await State.ClearAllAsync();
    }
}
