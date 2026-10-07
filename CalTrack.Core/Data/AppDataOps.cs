using CalorieTracker.Models;

namespace CalorieTracker.Data;

/// <summary>
/// Pure operations on <see cref="AppData"/> that both the app and the MCP server perform.
/// Living here (not in the app's state service) is what keeps "log two eggs" meaning the
/// same thing whether the user taps it in the app or an LLM asks for it over MCP.
/// </summary>
public static class AppDataOps
{
    public static FoodItem? FindItem(this AppData data, string name) =>
        data.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public static Recipe? FindRecipe(this AppData data, string name) =>
        data.Recipes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve a schedule/template name to a menu item: a plain item, or a recipe presented
    /// as its per-serving menu-item view. Null when the name matches neither.
    /// </summary>
    public static FoodItem? ResolveItem(this AppData data, string name) =>
        data.FindItem(name) ?? data.FindRecipe(name)?.ToMenuItem();

    /// <summary>All schedulable menu items: plain items plus recipes as per-serving views.</summary>
    public static IEnumerable<FoodItem> AllMenuItems(this AppData data) =>
        data.Items.Concat(data.Recipes.Select(r => r.ToMenuItem()));

    /// <summary>The item a schedule entry stands for: its embedded one-off item, or the named menu item/recipe.</summary>
    public static FoodItem? ResolveEntry(this AppData data, ScheduleEntry entry) =>
        entry.AdHoc ?? data.ResolveItem(entry.ItemName);

    public static List<ScheduleEntry> GetDay(this AppData data, DateOnly date) =>
        data.Days.TryGetValue(AppData.DayKey(date), out var list) ? list : new List<ScheduleEntry>();

    /// <summary>The day's entry list, created when absent.</summary>
    public static List<ScheduleEntry> DayList(this AppData data, DateOnly date)
    {
        var key = AppData.DayKey(date);
        if (!data.Days.TryGetValue(key, out var list))
            data.Days[key] = list = new List<ScheduleEntry>();
        return list;
    }

    /// <summary>
    /// Add servings of a menu item to a day. A regular entry for the same item MERGES
    /// (servings add up) rather than appearing twice; one-off entries never merge.
    /// </summary>
    public static void AddEntry(this AppData data, DateOnly date, string itemName, double servings)
    {
        if (servings <= 0) return;
        var list = data.DayList(date);
        var existing = list.FirstOrDefault(e => e.AdHoc is null && string.Equals(e.ItemName, itemName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Servings += servings;
        else list.Add(new ScheduleEntry { ItemName = itemName, Servings = servings });
    }

    /// <summary>Add a one-off item (embedded in the entry, not added to the menu) to a day.</summary>
    public static void AddAdHoc(this AppData data, DateOnly date, FoodItem item, double servings)
    {
        if (servings <= 0) return;
        data.DayList(date).Add(new ScheduleEntry { ItemName = item.Name, Servings = servings, AdHoc = item });
    }

    public static void AccumulateDay(this AppData data, Totals totals, DateOnly date)
    {
        foreach (var entry in data.GetDay(date))
        {
            var item = data.ResolveEntry(entry);
            if (item is not null) totals.Add(item, entry.Servings);
        }
    }

    public static Totals TotalsForDay(this AppData data, DateOnly date)
    {
        var totals = new Totals();
        data.AccumulateDay(totals, date);
        return totals;
    }
}
