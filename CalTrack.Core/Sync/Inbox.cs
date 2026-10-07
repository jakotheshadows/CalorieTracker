using System.Globalization;
using System.Text.Json;
using CalorieTracker.Data;
using CalorieTracker.Models;

namespace CalorieTracker.Sync;

/// <summary>An inbox file as listed by whichever side is reading the folder.</summary>
public sealed record InboxFile(string Name, string Text);

/// <summary>Outcome of folding the inbox into the data (app side).</summary>
public sealed class IngestResult
{
    /// <summary>Human-readable lines for ops applied this round (for the "added by Claude" notice).</summary>
    public List<string> Applied { get; } = new();

    /// <summary>Ops that were processed but could not be applied as asked (and why).</summary>
    public List<string> Rejected { get; } = new();

    /// <summary>Processed op files — delete them, but only AFTER the main file is saved.</summary>
    public List<string> FilesToDelete { get; } = new();

    /// <summary>Files that are not valid ops; left in place, never deleted.</summary>
    public List<string> Unreadable { get; } = new();

    /// <summary>Ops of a kind this app version doesn't know; left in place for a newer version.</summary>
    public List<string> Unsupported { get; } = new();

    /// <summary>The data changed (ops applied or the processed-id list pruned) and must be saved.</summary>
    public bool DataChanged { get; set; }
}

/// <summary>
/// The data-folder protocol shared by the app and the MCP server.
///
/// Layout: <c>caltrack-data.json</c> (written ONLY by the app) + <c>inbox/*.json</c>, one
/// file per op, created atomically by outside writers (temp name starting with '.', then
/// renamed). The app ingests by: apply unprocessed ops → record their ids → save main →
/// delete the op files. A crash between save and delete leaves files whose ids are already
/// recorded, so the next round deletes them without applying twice. Ids are forgotten once
/// their files are gone, keeping the list bounded.
///
/// Readers that want the current truth (the MCP server) use <see cref="View"/>: main plus
/// the still-pending ops, applied by the very same code the app will run.
/// </summary>
public static class Inbox
{
    public const string MainFileName = "caltrack-data.json";
    public const string DirName = "inbox";

    private static readonly JsonSerializerOptions OpJson = new() { WriteIndented = true };

    /// <summary>The op kinds this build applies. The app advertises it in <see cref="AppData.InboxKinds"/>.</summary>
    public static readonly IReadOnlyList<string> SupportedKinds = new[] { InboxOp.LogFood, InboxOp.LogAdHoc, InboxOp.AddItem };

    /// <summary>Kinds every app version that has a data folder at all can apply.</summary>
    private static readonly string[] OriginalKinds = { InboxOp.LogFood, InboxOp.LogAdHoc };

    /// <summary>
    /// Can the app that saved <paramref name="main"/> apply <paramref name="kind"/>? Writers
    /// must not queue an op the app would reject: it would be thrown away, not deferred.
    /// </summary>
    public static bool AppUnderstands(AppData main, string kind) =>
        OriginalKinds.Contains(kind) || main.InboxKinds.Contains(kind);

    /// <summary>Lexically sortable, chronological, collision-free op file name.</summary>
    public static string FileNameFor(InboxOp op) =>
        $"{op.CreatedUtc:yyyyMMdd'T'HHmmssfff}-{op.Id}.json";

    /// <summary>Only finished op files count: writers create ".{id}.tmp" first and rename.</summary>
    public static bool IsOpFileName(string name) =>
        !name.StartsWith('.') && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    public static string Serialize(InboxOp op) => JsonSerializer.Serialize(op, OpJson);

    public static InboxOp? TryParse(string text)
    {
        try
        {
            var op = JsonSerializer.Deserialize<InboxOp>(text, OpJson);
            return op is null || string.IsNullOrWhiteSpace(op.Id) || string.IsNullOrWhiteSpace(op.Kind) ? null : op;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static bool TryParseDay(string? s, out DateOnly date) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);

    /// <summary>
    /// Apply one op to <paramref name="data"/>. Returns a summary of what was logged, or an
    /// error when nothing could be (the data is then untouched).
    /// </summary>
    public static (string? Summary, string? Error) Apply(AppData data, InboxOp op)
    {
        // Logging ops target a day and an amount; adding a menu item has neither.
        var date = default(DateOnly);
        if (op.Kind is InboxOp.LogFood or InboxOp.LogAdHoc)
        {
            if (!TryParseDay(op.Date, out date)) return (null, $"bad date \"{op.Date}\"");
            if (!(op.Servings > 0) || double.IsInfinity(op.Servings)) return (null, $"bad servings {op.Servings}");
        }

        switch (op.Kind)
        {
            case InboxOp.LogFood:
            {
                var name = op.ItemName?.Trim() ?? "";
                if (name.Length == 0) return (null, "missing item name");
                var item = data.ResolveItem(name);
                if (item is not null)
                {
                    // Canonical casing from the menu, so the entry merges with existing ones.
                    data.AddEntry(date, item.Name, op.Servings);
                    return ($"{Fmt(op.Servings)} × {item.Name} on {op.Date}", null);
                }
                if (op.ItemSnapshot is { } snap && !string.IsNullOrWhiteSpace(snap.Name))
                {
                    // Renamed or deleted since the op was written: keep the food, as a one-off.
                    data.AddAdHoc(date, snap.Clone(), op.Servings);
                    return ($"{Fmt(op.Servings)} × {snap.Name} on {op.Date} (as a one-off — \"{name}\" is no longer on the menu)", null);
                }
                return (null, $"\"{name}\" is not on the menu");
            }
            case InboxOp.AddItem:
            {
                if (op.Item is not { } item || string.IsNullOrWhiteSpace(item.Name)) return (null, "missing menu item");
                var name = item.Name.Trim();
                // Names are unique across items AND recipes (case-insensitive), as in the app.
                if (data.ResolveItem(name) is { } existing) return (null, $"\"{existing.Name}\" is already on the menu");
                var copy = item.Clone();
                copy.Name = name;
                data.Items.Add(copy);
                return ($"added {name} to the menu" + (copy.Calories is { } c ? $" ({Fmt(c)} kcal per {copy.ServingSize ?? "serving"})" : ""), null);
            }
            case InboxOp.LogAdHoc:
            {
                if (op.AdHoc is not { } adhoc || string.IsNullOrWhiteSpace(adhoc.Name)) return (null, "missing one-off item");
                var copy = adhoc.Clone();
                copy.Name = copy.Name.Trim();
                data.AddAdHoc(date, copy, op.Servings);
                return ($"{Fmt(op.Servings)} × {copy.Name} on {op.Date} (one-off)", null);
            }
            default:
                return (null, $"unknown op kind \"{op.Kind}\"");
        }
    }

    /// <summary>
    /// App side: fold the inbox into <paramref name="data"/> in place. Save the data when
    /// <see cref="IngestResult.DataChanged"/>, THEN delete <see cref="IngestResult.FilesToDelete"/>.
    /// </summary>
    public static IngestResult Ingest(AppData data, IReadOnlyList<InboxFile> files)
    {
        var result = new IngestResult();
        var ops = new List<(InboxOp Op, string Name)>();
        foreach (var f in files)
        {
            if (!IsOpFileName(f.Name)) continue;
            var op = TryParse(f.Text);
            if (op is null) result.Unreadable.Add(f.Name);
            // A kind this build doesn't know stays in the inbox, unprocessed, for a newer app
            // version — consuming it as "rejected" would silently throw the change away.
            else if (!SupportedKinds.Contains(op.Kind)) result.Unsupported.Add(f.Name);
            else ops.Add((op, f.Name));
        }
        ops.Sort((a, b) => a.Op.CreatedUtc != b.Op.CreatedUtc
            ? a.Op.CreatedUtc.CompareTo(b.Op.CreatedUtc)
            : string.CompareOrdinal(a.Name, b.Name));

        var processed = new HashSet<string>(data.ProcessedOpIds, StringComparer.Ordinal);
        foreach (var (op, name) in ops)
        {
            if (!processed.Contains(op.Id))
            {
                var (summary, error) = Apply(data, op);
                if (summary is not null) result.Applied.Add(summary);
                else result.Rejected.Add($"{name}: {error}");
                processed.Add(op.Id);
                result.DataChanged = true;
            }
            result.FilesToDelete.Add(name);
        }

        // Remember only ids whose files still exist; the rest were deleted after a save
        // that already recorded them, so nothing can re-deliver them.
        var present = ops.Select(o => o.Op.Id).Where(processed.Contains).Distinct().ToList();
        if (!present.ToHashSet().SetEquals(data.ProcessedOpIds))
        {
            data.ProcessedOpIds = present;
            result.DataChanged = true;
        }
        return result;
    }

    /// <summary>
    /// Reader side (MCP): the data as it will look once the app ingests everything pending.
    /// <paramref name="pending"/> receives the ops not yet folded into the main file.
    /// </summary>
    public static AppData View(AppData main, IReadOnlyList<InboxFile> files, out List<InboxOp> pending)
    {
        var view = AppDataJson.Clone(main);
        var processed = new HashSet<string>(main.ProcessedOpIds, StringComparer.Ordinal);
        pending = files
            .Where(f => IsOpFileName(f.Name))
            .Select(f => (Op: TryParse(f.Text), f.Name))
            .Where(x => x.Op is not null && !processed.Contains(x.Op.Id))
            .OrderBy(x => x.Op!.CreatedUtc).ThenBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => x.Op!)
            .ToList();
        foreach (var op in pending) Apply(view, op);
        return view;
    }

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
