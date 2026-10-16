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
    public static readonly IReadOnlyList<string> SupportedKinds = new[]
    {
        InboxOp.LogFood, InboxOp.LogAdHoc, InboxOp.AddItem, InboxOp.AddRecipe,
        InboxOp.UpdateEntry, InboxOp.RemoveEntry, InboxOp.ClearDay,
        InboxOp.UpdateItem, InboxOp.DeleteItem, InboxOp.UpdateRecipe, InboxOp.DeleteRecipe,
        InboxOp.SaveTemplate, InboxOp.ApplyTemplate, InboxOp.DeleteTemplate,
        InboxOp.LogWeight, InboxOp.RemoveWeight, InboxOp.SetGoals, InboxOp.SetDays,
    };

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

    /// <summary>When an op file's op was created (to the millisecond, from its name), or null for other files.</summary>
    public static DateTime? CreatedOf(string fileName) =>
        IsOpFileName(fileName) && fileName.Length > 18
        && DateTime.TryParseExact(fileName[..18], "yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t
            : null;

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
        // Ops about a day carry its date; logging and changing servings carry an amount.
        var date = default(DateOnly);
        if (op.Kind is InboxOp.LogFood or InboxOp.LogAdHoc or InboxOp.UpdateEntry or InboxOp.RemoveEntry or InboxOp.ClearDay
            or InboxOp.SaveTemplate or InboxOp.ApplyTemplate or InboxOp.LogWeight or InboxOp.RemoveWeight)
        {
            if (!TryParseDay(op.Date, out date)) return (null, $"bad date \"{op.Date}\"");
        }
        if (op.Kind is InboxOp.LogFood or InboxOp.LogAdHoc or InboxOp.UpdateEntry)
        {
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
            case InboxOp.AddRecipe:
            {
                // The same rules as the app's recipe editor (AppState.UpsertRecipeAsync / SaveIngredientAsync).
                if (op.Recipe is not { } recipe || string.IsNullOrWhiteSpace(recipe.Name)) return (null, "missing recipe");
                var name = recipe.Name.Trim();
                if (!(recipe.Servings > 0) || double.IsInfinity(recipe.Servings)) return (null, $"bad servings {recipe.Servings}");
                if (recipe.Ingredients.Count == 0) return (null, "a recipe needs at least one ingredient");
                if (recipe.Ingredients.FirstOrDefault(i => string.IsNullOrWhiteSpace(i.Name) || i.AmountInBase is null) is { } bad)
                    return (null, $"bad ingredient \"{bad.Name}\" ({bad.Amount} {bad.Unit})");
                if (data.ResolveItem(name) is { } existing) return (null, $"\"{existing.Name}\" is already on the menu");
                var copy = recipe.Clone();
                copy.Name = name;
                foreach (var ing in copy.Ingredients) ing.Name = ing.Name.Trim();
                data.Recipes.Add(copy);
                data.Recipes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                var perServing = copy.ToMenuItem().Calories;
                return ($"added recipe {name} ({copy.Ingredients.Count} ingredient{(copy.Ingredients.Count == 1 ? "" : "s")}" +
                        (perServing is { } k ? $", {Fmt(k)} kcal per serving" : "") + ")", null);
            }
            case InboxOp.LogAdHoc:
            {
                if (op.AdHoc is not { } adhoc || string.IsNullOrWhiteSpace(adhoc.Name)) return (null, "missing one-off item");
                var copy = adhoc.Clone();
                copy.Name = copy.Name.Trim();
                data.AddAdHoc(date, copy, op.Servings);
                return ($"{Fmt(op.Servings)} × {copy.Name} on {op.Date} (one-off)", null);
            }
            case InboxOp.UpdateEntry:
            case InboxOp.RemoveEntry:
            {
                var name = op.ItemName?.Trim() ?? "";
                var label = $"{name}{(op.OneOff ? " (one-off)" : "")} on {op.Date}";
                // Addressed by name, not position: the user may have changed the day in the app since.
                if (data.FindEntry(date, name, op.OneOff, op.Occurrence) is not { } entry)
                    return (null, $"no entry {label}{(op.Occurrence > 1 ? $" #{op.Occurrence}" : "")} any more");
                if (op.Kind == InboxOp.RemoveEntry)
                {
                    data.RemoveEntry(date, entry);
                    return ($"removed {Fmt(entry.Servings)} × {label}", null);
                }
                var before = entry.Servings;
                data.SetServings(date, entry, op.Servings);
                return ($"{label}: {Fmt(before)} → {Fmt(op.Servings)} servings", null);
            }
            case InboxOp.ClearDay:
            {
                var removed = data.ClearDay(date);
                return ($"cleared {op.Date} ({removed} entr{(removed == 1 ? "y" : "ies")})", null);
            }
            case InboxOp.UpdateItem:
            {
                if (op.ItemPatch is not { } patch) return (null, "missing changes");
                var name = op.ItemName?.Trim() ?? "";
                if (data.UpdateItem(name, patch) is { } error) return (null, error);
                return ($"updated {name}" + (patch.NewName is { } n && !string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase) ? $" (now {n.Trim()})" : ""), null);
            }
            case InboxOp.DeleteItem:
            case InboxOp.DeleteRecipe:
            {
                var name = op.ItemName?.Trim() ?? "";
                var kept = op.Kind == InboxOp.DeleteItem ? data.DeleteItem(name) : data.DeleteRecipe(name);
                if (kept is not { } n) return (null, $"\"{name}\" is not {(op.Kind == InboxOp.DeleteItem ? "a menu item" : "a recipe")}");
                return ($"deleted {(op.Kind == InboxOp.DeleteItem ? "" : "recipe ")}{name}" + (n > 0 ? $" ({n} log entr{(n == 1 ? "y keeps its" : "ies keep their")} numbers as one-offs)" : ""), null);
            }
            case InboxOp.UpdateRecipe:
            {
                if (op.RecipePatch is not { } patch) return (null, "missing changes");
                var name = op.ItemName?.Trim() ?? "";
                if (data.UpdateRecipe(name, patch) is { } error) return (null, error);
                return ($"updated recipe {name}" + (patch.NewName is { } n && !string.Equals(n.Trim(), name, StringComparison.OrdinalIgnoreCase) ? $" (now {n.Trim()})" : ""), null);
            }
            case InboxOp.SaveTemplate:
            {
                var name = op.TemplateName?.Trim() ?? "";
                if (data.SaveTemplate(name, data.GetDay(date), op.Overwrite) is { } error) return (null, error);
                return ($"saved {op.Date} as template {name}", null);
            }
            case InboxOp.ApplyTemplate:
            {
                if (data.ApplyTemplate(date, op.TemplateName ?? "") is { } error) return (null, $"{error} (\"{op.TemplateName}\")");
                return ($"template {data.FindTemplate(op.TemplateName)} added to {op.Date}", null);
            }
            case InboxOp.DeleteTemplate:
            {
                var name = data.FindTemplate(op.TemplateName);
                if (name is null || !data.DeleteTemplate(name)) return (null, $"no template named \"{op.TemplateName}\"");
                return ($"deleted template {name}", null);
            }
            case InboxOp.LogWeight:
            {
                if (op.WeightKg is not { } kg || !data.LogWeight(date, kg)) return (null, $"bad weight {op.WeightKg}");
                return ($"weight {Fmt(Math.Round(kg, 2))} kg on {op.Date}", null);
            }
            case InboxOp.RemoveWeight:
                return data.RemoveWeight(date) ? ($"removed the weight logged on {op.Date}", null) : (null, $"no weight logged on {op.Date}");
            case InboxOp.SetGoals:
            {
                if (op.GoalsPatch is not { } patch) return (null, "missing changes");
                if (data.UpdateGoals(patch) is { } error) return (null, error);
                return ("updated goals", null);
            }
            case InboxOp.SetDays:
            {
                if (op.Days is not { Count: > 0 } days) return (null, "no days to set");
                if (days.Keys.FirstOrDefault(k => !TryParseDay(k, out _)) is { } bad) return (null, $"bad date \"{bad}\"");
                var set = data.SetDays(days, op.Overwrite);
                return ($"planned {set} day{(set == 1 ? "" : "s")}" + (set < days.Count ? $" ({days.Count - set} already had entries and were left alone)" : ""), null);
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

    /// <summary>A few words saying what a queued op will do, for listing pending changes.</summary>
    public static string Describe(InboxOp op) => op.Kind switch
    {
        InboxOp.LogFood => $"{Fmt(op.Servings)} × {op.ItemName}",
        InboxOp.LogAdHoc => $"{Fmt(op.Servings)} × {op.AdHoc?.Name} (one-off)",
        InboxOp.AddItem => $"add {op.Item?.Name} to the menu",
        InboxOp.AddRecipe => $"add recipe {op.Recipe?.Name}",
        InboxOp.UpdateEntry => $"{op.ItemName}{(op.OneOff ? " (one-off)" : "")} → {Fmt(op.Servings)} servings",
        InboxOp.RemoveEntry => $"remove {op.ItemName}{(op.OneOff ? " (one-off)" : "")}",
        InboxOp.ClearDay => "clear the day",
        InboxOp.UpdateItem => $"update {op.ItemName}",
        InboxOp.DeleteItem => $"delete {op.ItemName}",
        InboxOp.UpdateRecipe => $"update recipe {op.ItemName}",
        InboxOp.DeleteRecipe => $"delete recipe {op.ItemName}",
        InboxOp.SaveTemplate => $"save as template {op.TemplateName}",
        InboxOp.ApplyTemplate => $"add template {op.TemplateName}",
        InboxOp.DeleteTemplate => $"delete template {op.TemplateName}",
        InboxOp.LogWeight => $"weight {Fmt(Math.Round(op.WeightKg ?? 0, 2))} kg",
        InboxOp.RemoveWeight => "remove the weight",
        InboxOp.SetGoals => "update goals",
        InboxOp.SetDays => "random plan",
        _ => op.Kind,
    };

    /// <summary>Whether a queued op changes the log entries of the day <paramref name="dayKey"/>.</summary>
    public static bool ChangesDay(InboxOp op, string dayKey) => op.Kind switch
    {
        InboxOp.LogFood or InboxOp.LogAdHoc or InboxOp.UpdateEntry or InboxOp.RemoveEntry or InboxOp.ClearDay or InboxOp.ApplyTemplate => op.Date == dayKey,
        InboxOp.SetDays => op.Days?.ContainsKey(dayKey) == true,
        _ => false,
    };

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
