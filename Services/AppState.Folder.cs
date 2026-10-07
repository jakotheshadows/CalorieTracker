using System.Text.Json;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using Microsoft.JSInterop;

namespace CalorieTracker.Services;

public enum FolderState
{
    /// <summary>Not determined yet (before load).</summary>
    Unknown,

    /// <summary>The browser lacks the File System Access API (Safari, Firefox): browser storage only.</summary>
    Unsupported,

    /// <summary>Supported, but no folder connected.</summary>
    None,

    /// <summary>Connected and writable: the folder's file is the shared source of truth.</summary>
    Connected,

    /// <summary>A folder is remembered, but the browser needs a click to grant access again.</summary>
    NeedsReconnect,
}

/// <summary>
/// Connected data folder. The app is the ONLY writer of the folder's caltrack-data.json;
/// outside writers (the MCP server) drop one-file-per-op changes into inbox/, which are
/// folded in by CalTrack.Core's Inbox — the same code the MCP server uses to preview them.
///
/// Invariants:
///  - Browser storage is always written first and stays a complete copy, so losing folder
///    access never loses data; edits made meanwhile are pushed on reconnect.
///  - No version is ever silently overwritten: a main file that changed under us (hand
///    edit, a second tab, a cloud-synced copy) or that can't be read is saved aside as
///    caltrack-data.&lt;reason&gt;-&lt;time&gt;.json before ours replaces it.
///  - Inbox op files are deleted only after the main file recording them is saved.
/// </summary>
public partial class AppState
{
    private const string FolderMarksKey = "caltrack-folder-sync";

    private readonly SemaphoreSlim _folderGate = new(1, 1);
    private DotNetObjectReference<AppState>? _folderRef;
    private long _lastSeen;       // main file's lastModified as of our last read/write
    private string? _lastText;    // its content then (memory only): tells real edits from touched timestamps
    private bool _dirty;          // edits not yet in the folder (access lost, or a save failed)

    public FolderState Folder { get; private set; } = FolderState.Unknown;
    public string? FolderName { get; private set; }

    /// <summary>A human-readable line whenever folder sync does something the user should know about.</summary>
    public event Action<string>? FolderActivity;

    private sealed record Marks(long LastSeen, bool Dirty);

    // ---------- Lifecycle ----------

    private async Task InitFolderAsync()
    {
        await _folderGate.WaitAsync();
        try
        {
            await LoadMarksAsync();
            var st = await folder.StatusAsync();
            FolderName = st.Name;
            if (!st.Supported) Folder = FolderState.Unsupported;
            else if (!st.Connected) Folder = FolderState.None;
            else if (st.Permission == "granted") await AttachAsync(adopting: false);
            else Folder = FolderState.NeedsReconnect;
        }
        catch
        {
            // Folder trouble must never stop the app from loading: the browser copy stands.
            if (Folder is FolderState.Unknown or FolderState.Connected) Folder = FolderState.NeedsReconnect;
        }
        finally
        {
            _folderGate.Release();
        }
    }

    /// <summary>Pick a folder (call from a click). Null on success or cancel, else an error message.</summary>
    public async Task<string?> ConnectFolderAsync()
    {
        var r = await folder.ConnectAsync();
        if (!r.Ok) return r.Error;
        await _folderGate.WaitAsync();
        try
        {
            FolderName = r.Name;
            await AttachAsync(adopting: true);
            return null;
        }
        catch (Exception ex)
        {
            await folder.DisconnectAsync();
            Folder = FolderState.None;
            return "Couldn't use that folder: " + ex.Message;
        }
        finally
        {
            _folderGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Re-grant access to the remembered folder (call from a click). Null on success.</summary>
    public async Task<string?> ReconnectFolderAsync()
    {
        var permission = await folder.ReconnectAsync();
        if (permission != "granted")
            return "Access wasn't granted — CalTrack keeps saving in this browser until it is.";
        await _folderGate.WaitAsync();
        try
        {
            await AttachAsync(adopting: false);
            return null;
        }
        catch (Exception ex)
        {
            return "Couldn't reconnect: " + ex.Message;
        }
        finally
        {
            _folderGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Stop using the folder. Its files stay where they are; this browser keeps its copy.</summary>
    public async Task DisconnectFolderAsync()
    {
        await _folderGate.WaitAsync();
        try
        {
            await folder.DisconnectAsync();
            Folder = FolderState.None;
            FolderName = null;
            _dirty = false;
            _lastSeen = 0;
            _lastText = null;
            await SaveMarksAsync();
        }
        finally
        {
            _folderGate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Bring the folder and memory into agreement (permission already granted).
    /// <paramref name="adopting"/>: freshly picked via the picker, versus resuming a
    /// remembered folder at startup or on reconnect.
    /// </summary>
    private async Task AttachAsync(bool adopting)
    {
        var main = await folder.ReadMainAsync(withText: true);
        if (!main.Exists || string.IsNullOrWhiteSpace(main.Text))
        {
            // New (or empty) folder: it starts with this browser's data.
            await WriteMainAsync(checkForOutsideChanges: false);
        }
        else if (!TryParse(main.Text, out var theirs))
        {
            var aside = await SaveAsideAsync("unreadable", main.Text!);
            Notify($"The data file in the folder couldn't be read, so it was kept as {aside} and replaced with this browser's data.");
            await WriteMainAsync(checkForOutsideChanges: false);
        }
        else if (adopting)
        {
            // An existing CalTrack folder wins (it's the shared copy) — but never at the cost
            // of what this browser had: that goes into the folder as a backup.
            if (HasContent(Data) && AppDataJson.Serialize(Data) != AppDataJson.Serialize(theirs))
            {
                var aside = await SaveAsideAsync("browser-backup", AppDataJson.Serialize(Data, indented: true));
                Notify($"This folder already had CalTrack data, so CalTrack is now using it. What was in this browser was saved there as {aside} (Settings → Import JSON restores it).");
            }
            await AdoptAsync(theirs, main);
        }
        else if (_dirty)
        {
            // Edits made while the folder was out of reach are the newest. The app is the
            // only writer of this file, so if it changed anyway, keep that version aside.
            if (main.LastModified != _lastSeen && main.Text != _lastText)
            {
                var aside = await SaveAsideAsync("conflict", main.Text!);
                Notify($"The data file had also changed elsewhere; that version was kept as {aside}.");
            }
            await WriteMainAsync(checkForOutsideChanges: false);
        }
        else
        {
            await AdoptAsync(theirs, main);
        }

        _dirty = false;
        Folder = FolderState.Connected;
        await SaveMarksAsync();
        await IngestAsync();
        _folderRef ??= DotNetObjectReference.Create(this);
        await folder.StartWatchAsync(_folderRef);
    }

    // ---------- Saving ----------

    /// <summary>Write-through from <see cref="PersistAsync"/> (browser storage is already saved).</summary>
    private async Task PersistToFolderAsync()
    {
        if (Folder == FolderState.NeedsReconnect)
        {
            if (!_dirty) { _dirty = true; await SaveMarksAsync(); }
            return;
        }
        if (Folder != FolderState.Connected) return;

        await _folderGate.WaitAsync();
        try
        {
            await WriteMainAsync();
            _dirty = false;
            await SaveMarksAsync();
        }
        catch (Exception ex)
        {
            await HandleFolderErrorAsync(ex);
        }
        finally
        {
            _folderGate.Release();
        }
    }

    /// <summary>
    /// Save <see cref="Data"/> as the folder's main file. If the file changed since our last
    /// sync (we are its only writer, so that's a hand edit, a second tab or a synced copy),
    /// its version is kept aside first rather than overwritten.
    /// </summary>
    private async Task WriteMainAsync(bool checkForOutsideChanges = true)
    {
        if (checkForOutsideChanges)
        {
            var info = await folder.ReadMainAsync(withText: false);
            if (info.Exists && info.LastModified != _lastSeen)
            {
                var theirs = await folder.ReadMainAsync(withText: true);
                if (!string.IsNullOrWhiteSpace(theirs.Text) && theirs.Text != _lastText)
                {
                    var aside = await SaveAsideAsync("conflict", theirs.Text!);
                    Notify($"The data file was changed outside CalTrack; that version was kept as {aside}.");
                }
            }
        }
        var json = AppDataJson.Serialize(Data, indented: true);
        _lastSeen = await folder.WriteFileAsync(Inbox.MainFileName, json);
        _lastText = json;
    }

    private async Task<string> SaveAsideAsync(string reason, string text)
    {
        var name = $"caltrack-data.{reason}-{DateTime.Now:yyyyMMdd-HHmmss}.json";
        await folder.WriteFileAsync(name, text);
        return name;
    }

    private async Task AdoptAsync(AppData data, DataFolder.MainFile from)
    {
        Data = data;
        _lastSeen = from.LastModified;
        _lastText = from.Text;
        await store.SetAsync(DataKey, AppDataJson.Serialize(Data));
    }

    // ---------- Watching ----------

    /// <summary>Called by the JS poller (every ~2s while visible, and on refocus).</summary>
    [JSInvokable]
    public async Task OnFolderTick()
    {
        // Busy (a save or another tick in flight): skip, the next tick catches up. The async
        // form, because synchronous waits are unsupported on the browser runtime.
        if (Folder != FolderState.Connected || !await _folderGate.WaitAsync(0)) return;
        var changed = false;
        try
        {
            var info = await folder.ReadMainAsync(withText: false);
            if (_dirty || !info.Exists)
            {
                // A save failed transiently, or the file was deleted: put ours (back).
                await WriteMainAsync(checkForOutsideChanges: info.Exists);
                _dirty = false;
                await SaveMarksAsync();
            }
            else if (info.LastModified != _lastSeen)
            {
                var theirs = await folder.ReadMainAsync(withText: true);
                if (theirs.Text == _lastText)
                {
                    // Only the timestamp moved (sync client, antivirus): nothing to do.
                    _lastSeen = theirs.LastModified;
                    await SaveMarksAsync();
                }
                else if (TryParse(theirs.Text, out var data))
                {
                    await AdoptAsync(data, theirs);
                    await SaveMarksAsync();
                    Notify("The data file was changed outside CalTrack — reloaded it.");
                    changed = true;
                }
                // Unreadable: leave the marks alone, so the next save keeps it aside.
            }
            changed |= await IngestAsync();
        }
        catch (Exception ex)
        {
            await HandleFolderErrorAsync(ex);
            changed = true;
        }
        finally
        {
            _folderGate.Release();
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Fold pending inbox ops in. Caller holds the gate. True when something visible happened.</summary>
    private async Task<bool> IngestAsync()
    {
        var entries = await folder.ListInboxAsync();
        if (entries.Length == 0 && Data.ProcessedOpIds.Count == 0) return false;

        var result = Inbox.Ingest(Data, entries.Select(e => new InboxFile(e.Name, e.Text)).ToList());
        if (result.DataChanged)
        {
            Data.LastModifiedUtc = DateTime.UtcNow;
            await WriteMainAsync();
            await store.SetAsync(DataKey, AppDataJson.Serialize(Data));
            await SaveMarksAsync();
        }
        // Only now — the saved main file records these ids, so a crash can't re-apply them.
        if (result.FilesToDelete.Count > 0) await folder.DeleteInboxAsync(result.FilesToDelete);

        if (result.Applied.Count == 1) Notify("Logged via MCP: " + result.Applied[0]);
        else if (result.Applied.Count > 1) Notify($"Logged via MCP: {result.Applied.Count} entries — " + string.Join("; ", result.Applied));
        foreach (var rejected in result.Rejected) Notify("Couldn't log an MCP request (" + rejected + ").");
        return result.Applied.Count > 0 || result.Rejected.Count > 0;
    }

    private async Task HandleFolderErrorAsync(Exception ex)
    {
        _dirty = true;
        var st = await SafeStatusAsync();
        if (st?.Permission == "granted")
        {
            // Still allowed: a transient failure (file briefly locked). The next tick retries.
            await SaveMarksAsync();
            Notify("Couldn't save to the data folder just now (" + ex.Message + ") — retrying.");
            return;
        }
        Folder = FolderState.NeedsReconnect;
        await SaveMarksAsync();
        try { await folder.StopWatchAsync(); } catch { /* page teardown */ }
        Notify("CalTrack lost access to the data folder. Changes are kept in this browser until you reconnect it.");
    }

    private async Task<DataFolder.FolderStatus?> SafeStatusAsync()
    {
        try { return await folder.StatusAsync(); } catch { return null; }
    }

    // ---------- Helpers ----------

    private void Notify(string message) => FolderActivity?.Invoke(message);

    private static bool TryParse(string? text, out AppData data)
    {
        try
        {
            data = AppDataJson.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            data = new AppData();
            return false;
        }
    }

    private static bool HasContent(AppData d) =>
        d.Items.Count > 0 || d.Recipes.Count > 0 || d.Templates.Count > 0 || d.Weights.Count > 0 ||
        d.Goals is not null || d.Days.Values.Any(day => day.Count > 0);

    private async Task LoadMarksAsync()
    {
        try
        {
            var m = JsonSerializer.Deserialize<Marks>(await store.GetAsync(FolderMarksKey) ?? "");
            if (m is not null) (_lastSeen, _dirty) = (m.LastSeen, m.Dirty);
        }
        catch (JsonException)
        {
            // No marks yet: treat as in sync.
        }
    }

    private async Task SaveMarksAsync() =>
        await store.SetAsync(FolderMarksKey, JsonSerializer.Serialize(new Marks(_lastSeen, _dirty)));
}
