using System.Text;
using System.Text.Json;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using ModelContextProtocol;

namespace CalTrack.Mcp;

/// <summary>A consistent read of the data folder: the main file plus pending inbox ops.</summary>
/// <param name="View">The data as the app will show it once it ingests everything pending.</param>
/// <param name="Pending">Ops the app hasn't folded into the main file yet.</param>
public sealed record FolderSnapshot(AppData View, IReadOnlyList<InboxOp> Pending, bool MainExists);

/// <summary>
/// The MCP side of the data folder. It READS caltrack-data.json but never writes it — the
/// app is that file's only writer. Changes go out as one op file each in inbox/, created
/// under a dot-prefixed temp name and renamed into place so the app never sees a partial
/// file. That single-writer split is what rules out lost updates: two processes rewriting
/// one JSON file lose whichever write lands between the other's read and write, silently.
/// </summary>
public sealed class DataFolderStore(string? root)
{
    public string? Root { get; } = root;

    public string RequireRoot()
    {
        if (string.IsNullOrWhiteSpace(Root))
            throw new McpException(
                "No CalTrack data folder is configured. Set CALTRACK_DATA_DIR (or pass --data-dir) to the " +
                "folder connected in CalTrack → Settings → Data folder.");
        if (!Directory.Exists(Root))
            throw new McpException(
                $"The CalTrack data folder \"{Root}\" doesn't exist. Point CALTRACK_DATA_DIR at the folder " +
                "connected in CalTrack → Settings → Data folder.");
        return Root;
    }

    /// <summary>
    /// Read main + inbox as one consistent snapshot. If the app saves main mid-read (it may
    /// have just folded in and deleted ops we'd otherwise miss), the read is retried.
    /// </summary>
    public FolderSnapshot Read()
    {
        var root = RequireRoot();
        var mainPath = Path.Combine(root, Inbox.MainFileName);
        for (var attempt = 0; ; attempt++)
        {
            var before = Stamp(mainPath);
            var mainText = ReadShared(mainPath);
            var files = ListInbox(root);
            if (Stamp(mainPath) == before || attempt >= 5)
            {
                AppData main;
                try
                {
                    main = AppDataJson.Parse(mainText);
                }
                catch (JsonException ex)
                {
                    throw new McpException($"CalTrack's data file can't be read ({ex.Message}). Open the CalTrack app to repair it.");
                }
                var view = Inbox.View(main, files, out var pending);
                return new FolderSnapshot(view, pending, mainText is not null);
            }
            Thread.Sleep(40);
        }
    }

    /// <summary>Deliver an op to the app: atomic create-then-rename into inbox/.</summary>
    public string WriteOp(InboxOp op)
    {
        var dir = Path.Combine(RequireRoot(), Inbox.DirName);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{op.Id}.tmp");
        var final = Path.Combine(dir, Inbox.FileNameFor(op));
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = new UTF8Encoding(false).GetBytes(Inbox.Serialize(op));
            fs.Write(bytes);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, final);
        return final;
    }

    // ---------- requests to the app (its API; see AppRequest) ----------

    /// <summary>Ask the app something: atomic create-then-rename into requests/.</summary>
    public void WriteRequest(AppRequest request)
    {
        var dir = Path.Combine(RequireRoot(), AppRequests.RequestsDir);
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{request.Id}.tmp");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            fs.Write(new UTF8Encoding(false).GetBytes(AppRequests.Serialize(request)));
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, Path.Combine(dir, AppRequests.FileNameFor(request.Id)));
    }

    /// <summary>The app's answer if it has arrived (and is complete), removing it; else null.</summary>
    public AppResponse? TryTakeResponse(string id)
    {
        var path = Path.Combine(RequireRoot(), AppRequests.ResponsesDir, AppRequests.FileNameFor(id));
        var text = ReadShared(path);
        if (text is null || AppRequests.TryParseResponse(text) is not { } response) return null;
        try { File.Delete(path); } catch (IOException) { /* app still closing it; orphan cleanup gets it */ }
        return response;
    }

    /// <summary>Withdraw an unanswered request, so the app doesn't answer into the void later.</summary>
    public void WithdrawRequest(string id)
    {
        try { File.Delete(Path.Combine(RequireRoot(), AppRequests.RequestsDir, AppRequests.FileNameFor(id))); }
        catch (IOException) { /* being answered right now; the orphan sweep removes the answer */ }
    }

    /// <summary>Remove answers nobody collected (their asker timed out) once they're old.</summary>
    public void SweepOrphanResponses(TimeSpan olderThan)
    {
        var dir = Path.Combine(RequireRoot(), AppRequests.ResponsesDir);
        if (!Directory.Exists(dir)) return;
        foreach (var path in Directory.EnumerateFiles(dir))
            try
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > olderThan) File.Delete(path);
            }
            catch (IOException) { /* in use: next sweep */ }
    }

    private static (long Ticks, long Length)? Stamp(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : null;
    }

    /// <summary>
    /// Read without blocking the browser: share everything, so Chromium can swap its
    /// freshly written file into place while we hold ours open. Null when absent.
    /// </summary>
    private static string? ReadShared(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(40); // mid-swap; try again
            }
        }
    }

    private static List<InboxFile> ListInbox(string root)
    {
        var dir = Path.Combine(root, Inbox.DirName);
        var files = new List<InboxFile>();
        if (!Directory.Exists(dir)) return files;
        foreach (var path in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(path);
            if (!Inbox.IsOpFileName(name)) continue;
            var text = ReadShared(path); // null: the app ingested and deleted it meanwhile
            if (text is not null) files.Add(new InboxFile(name, text));
        }
        return files;
    }
}
