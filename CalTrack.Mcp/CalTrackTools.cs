using System.ComponentModel;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>
/// CalTrack's MCP tools. Design rules, because the caller is a language model:
///  - Ground, don't guess. log_food accepts only names that exist on the user's menu; an
///    unknown name is an error that lists the closest real names, so the model must pick
///    one or ask — it can never "log" a food the app doesn't know with made-up numbers.
///  - Verify, don't assume. Every write is read back through the same view the app will
///    produce before success is reported; a write that can't be seen is an error.
///  - Say what's true. Ops are QUEUED for the app (it folds them in when open); responses
///    say so rather than implying the app already shows them.
/// </summary>
[McpServerToolType]
public static class CalTrackTools
{
    private static readonly JsonSerializerOptions Out = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Read by a model, not embedded in HTML: keep quotes and symbols literal.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private const double MaxServings = 100;
    private const double MaxCaloriesPerServing = 5000;

    [McpServerTool(Name = "list_menu_items", ReadOnly = true, Idempotent = true)]
    [Description(
        "List the foods on the user's CalTrack menu (saved items and recipes) with per-serving nutrition. " +
        "These are the ONLY names log_food accepts. Call this before logging and match what the user said " +
        "to these names; if nothing fits, ask the user rather than guessing.")]
    public static string ListMenuItems(
        DataFolderStore store,
        [Description("Optional: only return items whose name contains this text (case-insensitive).")] string? search = null)
    {
        var view = store.Read().View;
        var items = view.Items.Select(i => Describe(i, "item"))
            .Concat(view.Recipes.Select(r => Describe(r.ToMenuItem(), "recipe")))
            .Where(i => string.IsNullOrWhiteSpace(search) || i.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Json(new
        {
            count = items.Count,
            items,
            note = items.Count == 0
                ? (string.IsNullOrWhiteSpace(search) ? "The menu is empty — the user adds foods in the CalTrack app." : $"Nothing on the menu matches \"{search}\".")
                : null,
        });
    }

    [McpServerTool(Name = "get_day", ReadOnly = true, Idempotent = true)]
    [Description(
        "Show what's logged for one day: each entry with servings and nutrition, plus day totals. Includes " +
        "entries queued by this server that the CalTrack app hasn't folded in yet (listed under pendingOps).")]
    public static string GetDay(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\". Defaults to today.")] string? date = null)
    {
        var day = ResolveDate(date, clock);
        var snap = store.Read();
        return Json(DayReport(snap, day, clock));
    }

    [McpServerTool(Name = "log_food", Destructive = false)]
    [Description(
        "Log servings of a food that is ON the user's menu (see list_menu_items) to a day. The name must " +
        "match a menu item or recipe exactly (case-insensitive); otherwise the call fails and returns the " +
        "closest menu names — pick one of those, or ask the user. Logging the same item twice on one day " +
        "adds to its servings. The entry is queued for the CalTrack app, which shows it within seconds when open.")]
    public static string LogFood(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Exact menu item or recipe name, as returned by list_menu_items.")] string itemName,
        [Description("Number of servings, e.g. 2 or 0.5. One serving is the item's listed serving size.")] double servings,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\". Defaults to today.")] string? date = null)
    {
        var day = ResolveDate(date, clock);
        CheckServings(servings);
        var name = itemName?.Trim() ?? "";
        if (name.Length == 0) throw new McpException("itemName is required.");

        var snap = store.Read();
        var item = snap.View.ResolveItem(name);
        if (item is null)
        {
            var close = NameMatcher.Closest(name, snap.View.AllMenuItems().Select(i => i.Name), 5);
            throw new McpException(
                $"\"{name}\" is not on the user's menu, so nothing was logged. " +
                (close.Count > 0 ? $"Closest menu names: {string.Join(", ", close.Select(c => $"\"{c}\""))}. " : "") +
                "Use an exact name from list_menu_items, or ask the user. For a food that isn't on the menu, " +
                "use log_adhoc — but only with nutrition the user has given or confirmed.");
        }

        var op = new InboxOp
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = clock.GetUtcNow().UtcDateTime,
            Kind = InboxOp.LogFood,
            Date = AppData.DayKey(day),
            Servings = servings,
            ItemName = item.Name,
            ItemSnapshot = item.Clone(),
            Source = "mcp",
        };
        return Json(WriteAndVerify(store, op, day, clock, item));
    }

    [McpServerTool(Name = "log_adhoc", Destructive = false)]
    [Description(
        "Log a one-off food that is NOT on the menu, with its per-serving nutrition. Only use numbers the " +
        "user stated or explicitly confirmed (e.g. from a label) — never invent or silently estimate them; " +
        "if you'd have to guess, ask the user first. Fails for names that are on the menu (use log_food).")]
    public static string LogAdHoc(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Food name, e.g. \"Gas station burrito\".")] string name,
        [Description("Calories per serving.")] double calories,
        [Description("Number of servings. Defaults to 1.")] double servings = 1,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\". Defaults to today.")] string? date = null,
        [Description("Protein per serving, grams (optional).")] double? proteinG = null,
        [Description("Carbohydrates per serving, grams (optional).")] double? carbsG = null,
        [Description("Fat per serving, grams (optional).")] double? fatG = null,
        [Description("What one serving is, e.g. \"1 burrito (250 g)\" (optional).")] string? servingSize = null)
    {
        var day = ResolveDate(date, clock);
        CheckServings(servings);
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) throw new McpException("name is required.");
        if (!(calories >= 0) || calories > MaxCaloriesPerServing)
            throw new McpException($"calories must be between 0 and {MaxCaloriesPerServing} per serving (got {calories}).");
        foreach (var (label, v) in new[] { ("proteinG", proteinG), ("carbsG", carbsG), ("fatG", fatG) })
            if (v is { } g && (!(g >= 0) || g > 1000)) throw new McpException($"{label} must be between 0 and 1000 grams (got {g}).");

        var snap = store.Read();
        if (snap.View.ResolveItem(trimmed) is { } onMenu)
            throw new McpException($"\"{onMenu.Name}\" is on the menu — use log_food so its saved nutrition is used.");

        var item = new FoodItem { Name = trimmed, Calories = calories, ServingSize = string.IsNullOrWhiteSpace(servingSize) ? null : servingSize.Trim() };
        if (proteinG is { } p) item.Nutrients["protein"] = p;
        if (carbsG is { } c) item.Nutrients["carbs"] = c;
        if (fatG is { } f) item.Nutrients["fat"] = f;

        var op = new InboxOp
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = clock.GetUtcNow().UtcDateTime,
            Kind = InboxOp.LogAdHoc,
            Date = AppData.DayKey(day),
            Servings = servings,
            AdHoc = item,
            Source = "mcp",
        };
        return Json(WriteAndVerify(store, op, day, clock, item));
    }

    // ---------- helpers ----------

    /// <summary>
    /// Write the op, then re-read the folder and confirm it's there — pending in the inbox,
    /// or already folded in by the app. Success is only reported for a write we can see.
    /// </summary>
    private static object WriteAndVerify(DataFolderStore store, InboxOp op, DateOnly day, TimeProvider clock, FoodItem item)
    {
        store.WriteOp(op);
        var after = store.Read();
        var seen = after.Pending.Any(p => p.Id == op.Id) || after.View.ProcessedOpIds.Contains(op.Id);
        if (!seen)
            throw new McpException("The entry was written but couldn't be read back from the data folder, so it may not have been saved. Check the CalTrack app before retrying.");

        var cals = item.Calories is { } perServing ? Round(perServing * op.Servings) : (double?)null;
        return new
        {
            logged = new
            {
                name = item.Name,
                servings = op.Servings,
                date = op.Date,
                calories = cals,
                oneOff = op.Kind == InboxOp.LogAdHoc ? true : (bool?)null,
            },
            status = after.Pending.Any(p => p.Id == op.Id) ? "queued" : "applied",
            note = "Queued in the data folder's inbox; the CalTrack app adds it to the log automatically (within a few seconds if it's open, otherwise next time it opens).",
            day = DayReport(after, day, clock),
        };
    }

    private static object DayReport(FolderSnapshot snap, DateOnly day, TimeProvider clock)
    {
        var view = snap.View;
        var entries = view.GetDay(day).Select(e =>
        {
            var item = view.ResolveEntry(e);
            return new
            {
                name = e.ItemName,
                servings = e.Servings,
                calories = item?.Calories is { } c ? Round(c * e.Servings) : (double?)null,
                proteinG = Macro(item, "protein", e.Servings),
                carbsG = Macro(item, "carbs", e.Servings),
                fatG = Macro(item, "fat", e.Servings),
                oneOff = e.AdHoc is not null ? true : (bool?)null,
                note = item is null ? "not on the menu any more — no nutrition" : null,
            };
        }).ToList();

        var totals = view.TotalsForDay(day);
        var dayKey = AppData.DayKey(day);
        var pending = snap.Pending.Where(p => p.Date == dayKey).Select(p => p.Kind == InboxOp.LogFood
            ? $"{Fmt(p.Servings)} × {p.ItemName}"
            : $"{Fmt(p.Servings)} × {p.AdHoc?.Name} (one-off)").ToList();
        return new
        {
            date = dayKey,
            today = AppData.DayKey(Today(clock)),
            entries,
            totals = new
            {
                calories = Round(totals.Calories),
                proteinG = Round(totals.Nutrient("protein") ?? 0),
                carbsG = Round(totals.Nutrient("carbs") ?? 0),
                fatG = Round(totals.Nutrient("fat") ?? 0),
            },
            pendingOps = pending.Count > 0 ? pending : null,
            dataFile = snap.MainExists ? null : "The CalTrack app hasn't saved to this folder yet — only queued entries are shown.",
        };
    }

    private sealed record MenuItemInfo(string Name, string Kind, string Category, string? Serving,
        double? Calories, double? ProteinG, double? CarbsG, double? FatG);

    private static MenuItemInfo Describe(FoodItem i, string kind) =>
        new(i.Name, kind, i.Category.ToString(), i.ServingSize, i.Calories,
            i.Nutrient("protein"), i.Nutrient("carbs"), i.Nutrient("fat"));

    private static double? Macro(FoodItem? item, string key, double servings) =>
        item?.Nutrient(key) is { } v ? Round(v * servings) : null;

    internal static DateOnly ResolveDate(string? date, TimeProvider clock)
    {
        var s = date?.Trim().ToLowerInvariant();
        var today = Today(clock);
        if (string.IsNullOrEmpty(s) || s == "today") return today;
        if (s == "yesterday") return today.AddDays(-1);
        if (Inbox.TryParseDay(s, out var d)) return d;
        throw new McpException($"date must be yyyy-MM-dd, \"today\" or \"yesterday\" (got \"{date}\"). Today is {AppData.DayKey(today)}.");
    }

    /// <summary>The user's local calendar day — CalTrack days are local, not UTC.</summary>
    private static DateOnly Today(TimeProvider clock) =>
        DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private static void CheckServings(double servings)
    {
        if (!(servings > 0) || servings > MaxServings || double.IsInfinity(servings))
            throw new McpException($"servings must be greater than 0 and at most {MaxServings} (got {servings}).");
    }

    private static double Round(double v) => Math.Round(v, 1);

    private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Json(object o) => JsonSerializer.Serialize(o, Out);
}
