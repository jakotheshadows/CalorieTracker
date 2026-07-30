using System.Text.Json;
using CalorieTracker.Models;

namespace CalorieTracker.Services;

/// <summary>
/// In-memory application state persisted to localStorage. Owns menu items, the schedule,
/// nutrition aggregation, and random schedule generation.
/// </summary>
public class AppState(LocalStore store)
{
    public const string DataKey = "caltrack-data";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    public AppData Data { get; private set; } = new();
    public bool Loaded { get; private set; }

    /// <summary>Raised whenever data changes (UI should re-render).</summary>
    public event Action? Changed;

    public async Task EnsureLoadedAsync()
    {
        if (Loaded) return;
        try
        {
            var json = await store.GetAsync(DataKey);
            if (!string.IsNullOrWhiteSpace(json))
                Data = JsonSerializer.Deserialize<AppData>(json, JsonOpts) ?? new AppData();
        }
        catch
        {
            // Corrupt/missing local data — start fresh rather than crash.
            Data = new AppData();
        }
        Loaded = true;
        Changed?.Invoke();
    }

    public string ExportJson() => JsonSerializer.Serialize(Data, new JsonSerializerOptions { WriteIndented = true });

    public async Task<string?> ImportJsonAsync(string json)
    {
        try
        {
            var data = JsonSerializer.Deserialize<AppData>(json, JsonOpts);
            if (data is null) return "File did not contain valid data.";
            Data = data;
            await PersistAsync();
            return null;
        }
        catch (Exception ex)
        {
            return "Import failed: " + ex.Message;
        }
    }

    public async Task ClearAllAsync()
    {
        Data = new AppData();
        await PersistAsync();
    }

    public async Task PersistAsync()
    {
        Data.LastModifiedUtc = DateTime.UtcNow;
        await store.SetAsync(DataKey, JsonSerializer.Serialize(Data, JsonOpts));
        Changed?.Invoke();
    }

    // ---------- Menu items ----------

    public FoodItem? FindItem(string name) =>
        Data.Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Add or update an item. <paramref name="originalName"/> is non-null when editing an existing item.</summary>
    public async Task<string?> UpsertItemAsync(FoodItem item, string? originalName)
    {
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";
        item.Name = name;

        var duplicate = Data.Items.FirstOrDefault(i =>
            string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(i.Name, originalName, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null) return $"An item named \"{duplicate.Name}\" already exists. Names must be unique.";

        if (originalName is null)
        {
            Data.Items.Add(item);
        }
        else
        {
            var existing = FindItem(originalName);
            if (existing is null)
            {
                Data.Items.Add(item);
            }
            else
            {
                var idx = Data.Items.IndexOf(existing);
                Data.Items[idx] = item;
                if (!string.Equals(originalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    // Keep schedule and template entries pointing at the renamed item.
                    foreach (var list in Data.Days.Values.Concat(Data.Templates.Values))
                        foreach (var e in list.Where(e => string.Equals(e.ItemName, originalName, StringComparison.OrdinalIgnoreCase)))
                            e.ItemName = name;
                }
            }
        }

        Data.Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        await PersistAsync();
        return null;
    }

    public async Task DeleteItemAsync(string name)
    {
        Data.Items.RemoveAll(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));
        foreach (var key in Data.Days.Keys.ToList())
        {
            Data.Days[key].RemoveAll(e => string.Equals(e.ItemName, name, StringComparison.OrdinalIgnoreCase));
            if (Data.Days[key].Count == 0) Data.Days.Remove(key);
        }
        foreach (var key in Data.Templates.Keys.ToList())
        {
            Data.Templates[key].RemoveAll(e => string.Equals(e.ItemName, name, StringComparison.OrdinalIgnoreCase));
            if (Data.Templates[key].Count == 0) Data.Templates.Remove(key);
        }
        await PersistAsync();
    }

    // ---------- Schedule ----------

    public List<ScheduleEntry> GetDay(DateOnly date) =>
        Data.Days.TryGetValue(AppData.DayKey(date), out var list) ? list : new List<ScheduleEntry>();

    public async Task AddEntryAsync(DateOnly date, string itemName, double servings)
    {
        if (servings <= 0) return;
        var key = AppData.DayKey(date);
        if (!Data.Days.TryGetValue(key, out var list))
            Data.Days[key] = list = new List<ScheduleEntry>();

        var existing = list.FirstOrDefault(e => string.Equals(e.ItemName, itemName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Servings += servings;
        else list.Add(new ScheduleEntry { ItemName = itemName, Servings = servings });

        await PersistAsync();
    }

    public async Task UpdateServingsAsync(DateOnly date, ScheduleEntry entry, double servings)
    {
        if (servings <= 0) { await RemoveEntryAsync(date, entry); return; }
        entry.Servings = servings;
        await PersistAsync();
    }

    public async Task RemoveEntryAsync(DateOnly date, ScheduleEntry entry)
    {
        var key = AppData.DayKey(date);
        if (Data.Days.TryGetValue(key, out var list))
        {
            list.Remove(entry);
            if (list.Count == 0) Data.Days.Remove(key);
        }
        await PersistAsync();
    }

    public async Task ClearDayAsync(DateOnly date)
    {
        if (Data.Days.Remove(AppData.DayKey(date)))
            await PersistAsync();
    }

    // ---------- Day templates ----------

    public IEnumerable<string> TemplateNames =>
        Data.Templates.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    public bool TemplateExists(string name) =>
        Data.Templates.Keys.Any(k => string.Equals(k, name?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Save a snapshot of <paramref name="entries"/> under a template name. Existing names are rejected unless <paramref name="overwrite"/> is set.</summary>
    public async Task<string?> SaveTemplateAsync(string name, List<ScheduleEntry> entries, bool overwrite = false)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return "Template name is required.";
        if (entries.Count == 0) return "This day has no entries to save.";

        var existing = Data.Templates.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!overwrite) return $"A template named \"{existing}\" already exists.";
            Data.Templates.Remove(existing);
        }

        Data.Templates[name] = entries
            .Select(e => new ScheduleEntry { ItemName = e.ItemName, Servings = e.Servings })
            .ToList();
        await PersistAsync();
        return null;
    }

    public async Task DeleteTemplateAsync(string name)
    {
        if (Data.Templates.Remove(name))
            await PersistAsync();
    }

    /// <summary>Add a template's entries to a day, merging servings with entries already present.</summary>
    public async Task<string?> ApplyTemplateAsync(DateOnly date, string name)
    {
        if (!Data.Templates.TryGetValue(name, out var template))
            return "Template not found.";

        var key = AppData.DayKey(date);
        if (!Data.Days.TryGetValue(key, out var list))
            Data.Days[key] = list = new List<ScheduleEntry>();

        foreach (var t in template)
        {
            var existing = list.FirstOrDefault(e => string.Equals(e.ItemName, t.ItemName, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) existing.Servings += t.Servings;
            else list.Add(new ScheduleEntry { ItemName = t.ItemName, Servings = t.Servings });
        }

        await PersistAsync();
        return null;
    }

    // ---------- Aggregation ----------

    public Totals TotalsForDay(DateOnly date)
    {
        var totals = new Totals();
        AccumulateDay(totals, date);
        return totals;
    }

    public Totals TotalsForRange(DateOnly start, DateOnly endInclusive)
    {
        var totals = new Totals();
        for (var d = start; d <= endInclusive; d = d.AddDays(1))
        {
            var before = totals.EntryCount;
            AccumulateDay(totals, d);
            if (totals.EntryCount > before) totals.DaysWithEntries++;
        }
        return totals;
    }

    private void AccumulateDay(Totals totals, DateOnly date)
    {
        foreach (var entry in GetDay(date))
        {
            var item = FindItem(entry.ItemName);
            if (item is not null) totals.Add(item, entry.Servings);
        }
    }

    /// <summary>Sunday-based start of the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    // ---------- Random generation ----------

    /// <summary>
    /// Fill each day in [start, end] with a randomized plan approximating the targets.
    /// Calories must be &gt; 0; macro/micro targets are optional.
    /// </summary>
    public async Task<string?> RandomizeRangeAsync(DateOnly start, DateOnly endInclusive, RandomTargets targets, bool overwriteExisting)
    {
        if (targets.Calories <= 0) return "Calorie target must be greater than zero.";
        if (endInclusive < start) return "End date must be on or after the start date.";

        var pool = Data.Items.Where(i => (i.Calories ?? 0) > 0).ToList();
        if (pool.Count == 0) return "Random generation needs at least one menu item with calories greater than zero.";

        var rng = new Random();
        for (var d = start; d <= endInclusive; d = d.AddDays(1))
        {
            var key = AppData.DayKey(d);
            if (!overwriteExisting && Data.Days.TryGetValue(key, out var existing) && existing.Count > 0)
                continue;
            Data.Days[key] = GenerateDay(pool, targets, rng);
        }

        await PersistAsync();
        return null;
    }

    private static List<ScheduleEntry> GenerateDay(List<FoodItem> pool, RandomTargets t, Random rng)
    {
        var minItems = Math.Max(1, Math.Min(t.MinItemsPerDay, pool.Count));
        var maxItems = Math.Max(minItems, Math.Min(t.MaxItemsPerDay, pool.Count));
        var maxServings = Math.Max(1, t.MaxServingsPerItem);
        var tolerance = Math.Max(0.5, t.TolerancePercent) / 100.0;

        List<ScheduleEntry>? best = null;
        var bestScore = double.MaxValue;
        var indices = Enumerable.Range(0, pool.Count).ToArray();

        for (var iter = 0; iter < 3000; iter++)
        {
            // Sample a candidate: k distinct items, each with 1..maxServings servings.
            var k = rng.Next(minItems, maxItems + 1);
            for (var i = 0; i < k; i++)
            {
                var j = rng.Next(i, indices.Length);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }

            var calories = 0.0;
            var candidate = new List<ScheduleEntry>(k);
            var nutrients = new Dictionary<string, double>();
            for (var i = 0; i < k; i++)
            {
                var item = pool[indices[i]];
                var servings = rng.Next(1, maxServings + 1);
                calories += (item.Calories ?? 0) * servings;
                foreach (var (nk, nv) in item.Nutrients)
                    nutrients[nk] = nutrients.GetValueOrDefault(nk) + nv * servings;
                candidate.Add(new ScheduleEntry { ItemName = item.Name, Servings = servings });
            }

            // Score: relative deviation from each target; calories weighted double.
            var calDev = Math.Abs(calories - t.Calories) / t.Calories;
            var score = calDev * 2.0;
            var allWithinTolerance = calDev <= tolerance;
            foreach (var (nk, target) in t.NutrientTargets)
            {
                if (target <= 0) continue;
                var dev = Math.Abs(nutrients.GetValueOrDefault(nk) - target) / target;
                score += dev;
                if (dev > tolerance) allWithinTolerance = false;
            }

            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
                if (allWithinTolerance) break;
            }
        }

        best!.Sort((a, b) => string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase));
        return best;
    }
}
