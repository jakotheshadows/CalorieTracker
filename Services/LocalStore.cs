using Microsoft.JSInterop;

namespace CalorieTracker.Services;

/// <summary>Thin wrapper over browser localStorage.</summary>
public class LocalStore(IJSRuntime js)
{
    public ValueTask<string?> GetAsync(string key) =>
        js.InvokeAsync<string?>("calTracker.storage.get", key);

    public ValueTask SetAsync(string key, string value) =>
        js.InvokeVoidAsync("calTracker.storage.set", key, value);

    public ValueTask RemoveAsync(string key) =>
        js.InvokeVoidAsync("calTracker.storage.remove", key);

    public ValueTask DownloadFileAsync(string fileName, string content) =>
        js.InvokeVoidAsync("calTracker.downloadFile", fileName, content);
}
