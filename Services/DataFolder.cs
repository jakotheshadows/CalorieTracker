using Microsoft.JSInterop;

namespace CalorieTracker.Services;

/// <summary>
/// Thin wrapper over <c>calTracker.dataFolder</c> (File System Access API) — I/O only.
/// The sync logic that decides what to read and write lives in AppState.Folder.cs.
/// </summary>
public class DataFolder(IJSRuntime js)
{
    public sealed record FolderStatus(bool Supported, bool Connected, string? Permission, string? Name);
    public sealed record ConnectResult(bool Ok, string? Name, string? Error);
    public sealed record MainFile(bool Exists, string? Text, long LastModified);
    public sealed record InboxEntry(string Name, string Text);

    public ValueTask<FolderStatus> StatusAsync() =>
        js.InvokeAsync<FolderStatus>("calTracker.dataFolder.status");

    /// <summary>Opens the folder picker; call from a click handler. Error is null when cancelled.</summary>
    public ValueTask<ConnectResult> ConnectAsync() =>
        js.InvokeAsync<ConnectResult>("calTracker.dataFolder.connect");

    /// <summary>Re-requests access to the remembered folder; call from a click handler. Returns the permission state.</summary>
    public ValueTask<string> ReconnectAsync() =>
        js.InvokeAsync<string>("calTracker.dataFolder.reconnect");

    public ValueTask DisconnectAsync() =>
        js.InvokeVoidAsync("calTracker.dataFolder.disconnect");

    public ValueTask<MainFile> ReadMainAsync(bool withText) =>
        js.InvokeAsync<MainFile>("calTracker.dataFolder.readMain", withText);

    /// <summary>Writes a file in the folder root and returns its new last-modified time.</summary>
    public ValueTask<long> WriteFileAsync(string name, string text) =>
        js.InvokeAsync<long>("calTracker.dataFolder.writeFile", name, text);

    public ValueTask<InboxEntry[]> ListInboxAsync() =>
        js.InvokeAsync<InboxEntry[]>("calTracker.dataFolder.listInbox");

    public ValueTask<int> DeleteInboxAsync(IEnumerable<string> names) =>
        js.InvokeAsync<int>("calTracker.dataFolder.deleteInbox", names);

    public ValueTask StartWatchAsync<T>(DotNetObjectReference<T> target) where T : class =>
        js.InvokeVoidAsync("calTracker.dataFolder.startWatch", target);

    public ValueTask StopWatchAsync() =>
        js.InvokeVoidAsync("calTracker.dataFolder.stopWatch");
}
