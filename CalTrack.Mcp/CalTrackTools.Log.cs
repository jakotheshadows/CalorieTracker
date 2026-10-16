using System.ComponentModel;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>
/// Changing what's logged: servings, removing entries, clearing a day — and reading a range
/// of days. Entries are named as get_day shows them; an entry number is only needed when a
/// day has two entries of the same name (one-offs don't merge).
/// </summary>
public static partial class CalTrackTools
{
    private const int MaxSummaryDays = 366;

    [McpServerTool(Name = "update_entry", Destructive = true, Idempotent = true)]
    [Description(
        "Change how many servings a logged entry has, e.g. \"make that 2 eggs, not 3\". Name the entry as get_day " +
        "shows it; if a day has two entries with that name, pass its entry number from get_day instead. To add " +
        "more of a food, log_food adds to its entry.")]
    public static string UpdateEntry(
        DataFolderStore store,
        TimeProvider clock,
        [Description("The new number of servings, e.g. 2 or 0.5. To delete the entry use remove_entry.")] double servings,
        [Description("The entry's name, as get_day shows it.")] string? name = null,
        [Description("The entry's number from get_day — needed only when two entries share a name.")] int? entry = null,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\". Defaults to today.")] string? date = null)
    {
        var day = ResolveDate(date, clock);
        CheckServings(servings);
        var snap = store.Read();
        var (target, number) = PickEntry(snap.View, day, name, entry);
        var before = target.Servings;

        var op = EntryOp(InboxOp.UpdateEntry, snap.View, day, target, clock);
        op.Servings = servings;
        var (after, status, _) = Submit(store, snap, op, "change logged entries");
        var item = snap.View.ResolveEntry(target);
        return Json(new
        {
            updated = new
            {
                date = op.Date,
                entry = number,
                name = target.ItemName,
                oneOff = op.OneOff ? true : (bool?)null,
                servingsBefore = before,
                servings,
                calories = item?.Calories is { } c ? Round(c * servings) : (double?)null,
            },
            status,
            note = QueuedNote,
            day = DayReport(after, day, clock),
        });
    }

    [McpServerTool(Name = "remove_entry", Destructive = true)]
    [Description(
        "Remove one entry from a day's log, e.g. \"I didn't actually eat the cookie\". Name the entry as get_day shows " +
        "it; if a day has two entries with that name, pass its entry number from get_day instead. The result says " +
        "what was removed, so it can be logged again if the user changes their mind.")]
    public static string RemoveEntry(
        DataFolderStore store,
        TimeProvider clock,
        [Description("The entry's name, as get_day shows it.")] string? name = null,
        [Description("The entry's number from get_day — needed only when two entries share a name.")] int? entry = null,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\". Defaults to today.")] string? date = null)
    {
        var day = ResolveDate(date, clock);
        var snap = store.Read();
        var (target, _) = PickEntry(snap.View, day, name, entry);

        var op = EntryOp(InboxOp.RemoveEntry, snap.View, day, target, clock);
        var (after, status, _) = Submit(store, snap, op, "remove logged entries");
        return Json(new
        {
            removed = EntryInfo(snap.View, target),
            date = op.Date,
            status,
            note = QueuedNote + (target.AdHoc is not null
                ? " To put it back: log_adhoc with the oneOff numbers above."
                : " To put it back: log_food with the same name and servings."),
            day = DayReport(after, day, clock),
        });
    }

    [McpServerTool(Name = "clear_day", Destructive = true)]
    [Description(
        "Remove every entry from one day's log. Only when the user asks to clear or reset a whole day; to drop one " +
        "food use remove_entry. The result lists what was removed.")]
    public static string ClearDay(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Day as yyyy-MM-dd, or \"today\" / \"yesterday\" — always say which day.")] string date)
    {
        var day = ResolveDate(date ?? throw new McpException("date is required."), clock);
        var snap = store.Read();
        var entries = snap.View.GetDay(day);
        if (entries.Count == 0) throw new McpException($"Nothing is logged on {AppData.DayKey(day)}; nothing to clear.");

        var op = NewOp(InboxOp.ClearDay, clock);
        op.Date = AppData.DayKey(day);
        var (after, status, _) = Submit(store, snap, op, "clear days");
        return Json(new
        {
            removed = entries.Select(e => EntryInfo(snap.View, e)).ToList(),
            date = op.Date,
            status,
            note = QueuedNote,
            day = DayReport(after, day, clock),
        });
    }

    [McpServerTool(Name = "get_summary", ReadOnly = true, Idempotent = true)]
    [Description(
        "Totals over a range of days — e.g. \"how did I do this week?\": calories and macros for each logged day, the " +
        "range's totals and per-logged-day averages, and the user's suggested daily calories when goals are set up.")]
    public static string GetSummary(
        DataFolderStore store,
        TimeProvider clock,
        [Description("First day, yyyy-MM-dd or \"today\" / \"yesterday\". Defaults to 6 days before the last day (a week).")] string? from = null,
        [Description("Last day, yyyy-MM-dd or \"today\" / \"yesterday\". Defaults to today.")] string? to = null)
    {
        var last = ResolveDate(to, clock);
        var first = from is null ? last.AddDays(-6) : ResolveDate(from, clock);
        if (first > last) throw new McpException($"from ({AppData.DayKey(first)}) is after to ({AppData.DayKey(last)}).");
        var span = last.DayNumber - first.DayNumber + 1;
        if (span > MaxSummaryDays) throw new McpException($"At most {MaxSummaryDays} days at a time (asked for {span}).");

        var view = store.Read().View;
        var totals = new Totals();
        var days = new List<object>();
        for (var d = first; d <= last; d = d.AddDays(1))
        {
            var t = view.TotalsForDay(d);
            if (t.EntryCount == 0) continue;
            view.AccumulateDay(totals, d);
            days.Add(new
            {
                date = AppData.DayKey(d),
                calories = Round(t.Calories),
                proteinG = Round(t.Nutrient("protein") ?? 0),
                carbsG = Round(t.Nutrient("carbs") ?? 0),
                fatG = Round(t.Nutrient("fat") ?? 0),
                entries = t.EntryCount,
            });
        }
        double? Avg(double v) => days.Count > 0 ? Round(v / days.Count) : null;
        var goal = view.GoalStatus(Today(clock));
        return Json(new
        {
            from = AppData.DayKey(first),
            to = AppData.DayKey(last),
            daysInRange = span,
            daysLogged = days.Count,
            totals = new
            {
                calories = Round(totals.Calories),
                proteinG = Round(totals.Nutrient("protein") ?? 0),
                carbsG = Round(totals.Nutrient("carbs") ?? 0),
                fatG = Round(totals.Nutrient("fat") ?? 0),
            },
            averagePerLoggedDay = days.Count == 0 ? null : new
            {
                calories = Avg(totals.Calories),
                proteinG = Avg(totals.Nutrient("protein") ?? 0),
                carbsG = Avg(totals.Nutrient("carbs") ?? 0),
                fatG = Avg(totals.Nutrient("fat") ?? 0),
            },
            suggestedDailyCalories = goal is null ? (double?)null : Math.Round(goal.SuggestedCalories),
            days,
        });
    }

    // ---------- helpers ----------

    /// <summary>The entry the model means, by name or by its get_day number. Ambiguity is an error that lists the day.</summary>
    private static (ScheduleEntry Entry, int Number) PickEntry(AppData view, DateOnly day, string? name, int? entry)
    {
        var list = view.GetDay(day);
        var dayKey = AppData.DayKey(day);
        if (list.Count == 0) throw new McpException($"Nothing is logged on {dayKey}, so nothing was changed.");
        string Listing() => string.Join("; ", list.Select((e, i) => $"{i + 1}. {e.ItemName}{(e.AdHoc is not null ? " (one-off)" : "")} × {Fmt(e.Servings)}"));

        var wanted = name?.Trim() ?? "";
        if (entry is { } n)
        {
            if (n < 1 || n > list.Count)
                throw new McpException($"entry must be 1 to {list.Count} on {dayKey}. Entries: {Listing()}.");
            var e = list[n - 1];
            if (wanted.Length > 0 && !string.Equals(e.ItemName, wanted, StringComparison.OrdinalIgnoreCase))
                throw new McpException($"Entry {n} on {dayKey} is \"{e.ItemName}\", not \"{wanted}\", so nothing was changed. Entries: {Listing()}.");
            return (e, n);
        }
        if (wanted.Length == 0) throw new McpException("Say which entry: its name, or its entry number from get_day.");
        var matches = list.Select((e, i) => (Entry: e, Number: i + 1))
            .Where(x => string.Equals(x.Entry.ItemName, wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0)
            throw new McpException($"Nothing named \"{wanted}\" is logged on {dayKey}, so nothing was changed. Entries: {Listing()}.");
        if (matches.Count > 1)
            throw new McpException($"{dayKey} has {matches.Count} entries named \"{wanted}\" (entries {string.Join(", ", matches.Select(m => m.Number))}); pass entry to say which. Entries: {Listing()}.");
        return matches[0];
    }

    /// <summary>An op addressing one entry the way the app finds it again: name, kind, and which of the same-named ones.</summary>
    private static InboxOp EntryOp(string kind, AppData view, DateOnly day, ScheduleEntry entry, TimeProvider clock)
    {
        var op = NewOp(kind, clock);
        op.Date = AppData.DayKey(day);
        op.ItemName = entry.ItemName;
        op.OneOff = entry.AdHoc is not null;
        op.Occurrence = view.OccurrenceOf(day, entry);
        return op;
    }

    /// <summary>An entry as reported back — with a one-off's per-serving numbers, enough to log it again.</summary>
    private static object EntryInfo(AppData view, ScheduleEntry e)
    {
        var item = view.ResolveEntry(e);
        return new
        {
            name = e.ItemName,
            servings = e.Servings,
            calories = item?.Calories is { } c ? Round(c * e.Servings) : (double?)null,
            oneOff = e.AdHoc is { } adhoc
                ? new
                {
                    caloriesPerServing = adhoc.Calories,
                    proteinG = adhoc.Nutrient("protein"),
                    carbsG = adhoc.Nutrient("carbs"),
                    fatG = adhoc.Nutrient("fat"),
                    servingSize = adhoc.ServingSize,
                }
                : null,
        };
    }
}
