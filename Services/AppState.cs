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

    public Recipe? FindRecipe(string name) =>
        Data.Recipes.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolve a schedule/template name to a menu item: a plain item, or a recipe presented
    /// as its per-serving menu-item view. Null when the name matches neither.
    /// </summary>
    public FoodItem? ResolveItem(string name) =>
        FindItem(name) ?? FindRecipe(name)?.ToMenuItem();

    /// <summary>All schedulable menu items: plain items plus recipes as per-serving views.</summary>
    public IEnumerable<FoodItem> AllMenuItems() =>
        Data.Items.Concat(Data.Recipes.Select(r => r.ToMenuItem()));

    /// <summary>The item a schedule entry stands for: its embedded one-off item, or the named menu item/recipe.</summary>
    public FoodItem? ResolveEntry(ScheduleEntry entry) =>
        entry.AdHoc ?? ResolveItem(entry.ItemName);

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
        if (FindRecipe(name) is { } recipeDup) return $"A recipe named \"{recipeDup.Name}\" already exists. Names must be unique across items and recipes.";

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
                    // Keep schedule and template entries pointing at the renamed item
                    // (one-off entries that happen to share the name are their own thing).
                    foreach (var list in Data.Days.Values.Concat(Data.Templates.Values))
                        foreach (var e in list.Where(e => e.AdHoc is null && string.Equals(e.ItemName, originalName, StringComparison.OrdinalIgnoreCase)))
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
        RemoveEntriesReferencing(name);
        await PersistAsync();
    }

    /// <summary>Drop schedule/template entries that reference a deleted item or recipe by name. One-off entries stay.</summary>
    private void RemoveEntriesReferencing(string name)
    {
        foreach (var key in Data.Days.Keys.ToList())
        {
            Data.Days[key].RemoveAll(e => e.AdHoc is null && string.Equals(e.ItemName, name, StringComparison.OrdinalIgnoreCase));
            if (Data.Days[key].Count == 0) Data.Days.Remove(key);
        }
        foreach (var key in Data.Templates.Keys.ToList())
        {
            Data.Templates[key].RemoveAll(e => e.AdHoc is null && string.Equals(e.ItemName, name, StringComparison.OrdinalIgnoreCase));
            if (Data.Templates[key].Count == 0) Data.Templates.Remove(key);
        }
    }

    // ---------- Recipes ----------

    /// <summary>Add or update a recipe. <paramref name="originalName"/> is non-null when editing an existing recipe.</summary>
    public async Task<string?> UpsertRecipeAsync(Recipe recipe, string? originalName)
    {
        var name = recipe.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";
        if (recipe.Servings <= 0) return "Servings must be greater than zero.";
        recipe.Name = name;

        var duplicate = Data.Recipes.FirstOrDefault(r =>
            string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(r.Name, originalName, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null) return $"A recipe named \"{duplicate.Name}\" already exists. Names must be unique.";
        if (FindItem(name) is { } itemDup) return $"An item named \"{itemDup.Name}\" already exists. Names must be unique across items and recipes.";

        if (originalName is null)
        {
            Data.Recipes.Add(recipe);
        }
        else
        {
            var existing = FindRecipe(originalName);
            if (existing is null)
            {
                Data.Recipes.Add(recipe);
            }
            else
            {
                var idx = Data.Recipes.IndexOf(existing);
                Data.Recipes[idx] = recipe;
                if (!string.Equals(originalName, name, StringComparison.OrdinalIgnoreCase))
                {
                    // Keep schedule and template entries pointing at the renamed recipe
                    // (one-off entries that happen to share the name are their own thing).
                    foreach (var list in Data.Days.Values.Concat(Data.Templates.Values))
                        foreach (var e in list.Where(e => e.AdHoc is null && string.Equals(e.ItemName, originalName, StringComparison.OrdinalIgnoreCase)))
                            e.ItemName = name;
                }
            }
        }

        Data.Recipes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        await PersistAsync();
        return null;
    }

    /// <summary>Add (index null) or replace one ingredient of a recipe. Null on success.</summary>
    public async Task<string?> SaveIngredientAsync(string recipeName, RecipeIngredient ingredient, int? index)
    {
        var recipe = FindRecipe(recipeName);
        if (recipe is null) return "Recipe not found.";
        var name = ingredient.Name?.Trim() ?? "";
        if (name.Length == 0) return "Ingredient name is required.";
        if (ingredient.AmountInBase is null) return "Enter an amount greater than zero.";
        ingredient.Name = name;

        if (index is { } i && i >= 0 && i < recipe.Ingredients.Count) recipe.Ingredients[i] = ingredient;
        else recipe.Ingredients.Add(ingredient);
        await PersistAsync();
        return null;
    }

    public async Task RemoveIngredientAsync(string recipeName, int index)
    {
        var recipe = FindRecipe(recipeName);
        if (recipe is null || index < 0 || index >= recipe.Ingredients.Count) return;
        recipe.Ingredients.RemoveAt(index);
        await PersistAsync();
    }

    public async Task DeleteRecipeAsync(string name)
    {
        Data.Recipes.RemoveAll(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        RemoveEntriesReferencing(name);
        await PersistAsync();
    }

    // ---------- Schedule ----------

    public List<ScheduleEntry> GetDay(DateOnly date) =>
        Data.Days.TryGetValue(AppData.DayKey(date), out var list) ? list : new List<ScheduleEntry>();

    public async Task AddEntryAsync(DateOnly date, string itemName, double servings)
    {
        if (servings <= 0) return;
        var list = DayList(date);

        var existing = list.FirstOrDefault(e => e.AdHoc is null && string.Equals(e.ItemName, itemName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) existing.Servings += servings;
        else list.Add(new ScheduleEntry { ItemName = itemName, Servings = servings });

        await PersistAsync();
    }

    private List<ScheduleEntry> DayList(DateOnly date)
    {
        var key = AppData.DayKey(date);
        if (!Data.Days.TryGetValue(key, out var list))
            Data.Days[key] = list = new List<ScheduleEntry>();
        return list;
    }

    /// <summary>
    /// Add a one-off item to a day. When <paramref name="saveToMenu"/> is set the item is
    /// added to the menu instead and the entry references it normally. Null on success.
    /// </summary>
    public async Task<string?> AddAdHocEntryAsync(DateOnly date, FoodItem item, double servings, bool saveToMenu)
    {
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";
        if (servings <= 0) return "Servings must be greater than zero.";
        item.Name = name;

        if (saveToMenu)
        {
            var error = await UpsertItemAsync(item, null);
            if (error is not null) return error;
            await AddEntryAsync(date, name, servings);
            return null;
        }

        DayList(date).Add(new ScheduleEntry { ItemName = name, Servings = servings, AdHoc = item });
        await PersistAsync();
        return null;
    }

    /// <summary>
    /// Replace a one-off entry's embedded item. When <paramref name="saveToMenu"/> is set the
    /// item joins the menu and the entry converts to a regular reference. Null on success.
    /// </summary>
    public async Task<string?> UpdateAdHocEntryAsync(DateOnly date, ScheduleEntry entry, FoodItem item, bool saveToMenu)
    {
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";
        item.Name = name;

        if (saveToMenu)
        {
            var error = await UpsertItemAsync(item, null);
            if (error is not null) return error;
            entry.AdHoc = null;
            entry.ItemName = name;
            // Fold into an existing regular entry for the same item, if the day has one.
            var list = DayList(date);
            var twin = list.FirstOrDefault(e => e != entry && e.AdHoc is null && string.Equals(e.ItemName, name, StringComparison.OrdinalIgnoreCase));
            if (twin is not null)
            {
                twin.Servings += entry.Servings;
                list.Remove(entry);
            }
        }
        else
        {
            entry.AdHoc = item;
            entry.ItemName = name;
        }

        await PersistAsync();
        return null;
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
            .Select(e => new ScheduleEntry { ItemName = e.ItemName, Servings = e.Servings, AdHoc = e.AdHoc?.Clone() })
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

        var list = DayList(date);

        foreach (var t in template)
        {
            // Regular entries merge by name; one-off entries always land as their own copies
            // (same-named one-offs can carry different nutrition).
            var existing = t.AdHoc is null
                ? list.FirstOrDefault(e => e.AdHoc is null && string.Equals(e.ItemName, t.ItemName, StringComparison.OrdinalIgnoreCase))
                : null;
            if (existing is not null) existing.Servings += t.Servings;
            else list.Add(new ScheduleEntry { ItemName = t.ItemName, Servings = t.Servings, AdHoc = t.AdHoc?.Clone() });
        }

        await PersistAsync();
        return null;
    }

    // ---------- Goals & weight log ----------

    public async Task LogWeightAsync(DateOnly date, double weightKg)
    {
        if (weightKg <= 0) return;
        Data.Weights[AppData.DayKey(date)] = Math.Round(weightKg, 2);
        await PersistAsync();
    }

    public async Task RemoveWeightAsync(DateOnly date)
    {
        if (Data.Weights.Remove(AppData.DayKey(date)))
            await PersistAsync();
    }

    /// <summary>Save the goal profile. Rejects target weights below the healthy BMI floor.</summary>
    public async Task<string?> SaveGoalsAsync(GoalSettings goals)
    {
        if (goals.HeightCm < 90 || goals.HeightCm > 250) return "Height looks out of range.";
        var heightM = goals.HeightCm / 100.0;
        var minHealthyKg = GoalMath.MinHealthyBmi * heightM * heightM;
        if (goals.TargetWeightKg < minHealthyKg)
        {
            var min = goals.Units == UnitSystem.Imperial
                ? $"{GoalMath.KgToLb(minHealthyKg):0} lb"
                : $"{minHealthyKg:0.0} kg";
            return $"That target is below the healthy range for your height (BMI {GoalMath.MinHealthyBmi}, about {min}). CalTrack won't set targets below it.";
        }
        goals.TargetRatePercentPerWeek = Math.Clamp(goals.TargetRatePercentPerWeek, 0, GoalMath.MaxRatePercentPerWeek);
        Data.Goals = goals;
        await PersistAsync();
        return null;
    }

    /// <summary>Current goal status, or null when goals aren't set up or no weight is logged yet.</summary>
    public GoalStatus? GetGoalStatus() =>
        Data.Goals is null
            ? null
            : GoalMath.Compute(Data.Goals, Data.Weights,
                d => TotalsForDay(d).Calories, DateOnly.FromDateTime(DateTime.Today));

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
            var item = ResolveEntry(entry);
            if (item is not null) totals.Add(item, entry.Servings);
        }
    }

    /// <summary>Sunday-based start of the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    // ---------- Random generation ----------

    /// <summary>
    /// Fill each day in [start, end] with a randomized plan. The calorie target acts as a daily
    /// cap (tolerance sets how far a day may exceed it); nutrient targets act as daily minimums.
    /// Returns an error, plus a warning when some days could not fully meet the targets.
    /// </summary>
    public async Task<(string? Error, string? Warning)> RandomizeRangeAsync(DateOnly start, DateOnly endInclusive, RandomTargets targets, bool overwriteExisting)
    {
        if (targets.Calories <= 0) return ("Calorie target must be greater than zero.", null);
        if (endInclusive < start) return ("End date must be on or after the start date.", null);

        var pool = AllMenuItems().Where(IsRandomEligible).ToList();
        if (pool.Count == 0) return ("Random generation needs at least one menu item or recipe with calories greater than zero that isn't excluded from randomize.", null);

        var rng = new Random();
        int generated = 0, missed = 0;
        for (var d = start; d <= endInclusive; d = d.AddDays(1))
        {
            var key = AppData.DayKey(d);
            if (!overwriteExisting && Data.Days.TryGetValue(key, out var existing) && existing.Count > 0)
                continue;
            var (entries, meetsTargets) = GenerateDay(pool, targets, rng);
            Data.Days[key] = entries;
            generated++;
            if (!meetsTargets) missed++;
        }

        await PersistAsync();
        var warning = missed > 0
            ? $"{missed} of {generated} generated day(s) couldn't fully meet the targets with the eligible menu items — the closest fit was used. Consider adding higher-nutrient items or raising the calorie cap."
            : null;
        return (null, warning);
    }

    public static bool IsRandomEligible(FoodItem item) => (item.Calories ?? 0) > 0 && !item.ExcludeFromRandom;

    private static (List<ScheduleEntry> Entries, bool MeetsTargets) GenerateDay(List<FoodItem> pool, RandomTargets t, Random rng)
    {
        var minItems = Math.Max(1, Math.Min(t.MinItemsPerDay, pool.Count));
        var maxItems = Math.Max(minItems, Math.Min(t.MaxItemsPerDay, pool.Count));
        var maxServings = Math.Max(1, t.MaxServingsPerItem);
        var tol = Math.Max(0.5, t.TolerancePercent) / 100.0;
        var calCap = t.Calories * (1 + tol);
        var targets = t.NutrientTargets.Where(kv => kv.Value > 0).ToArray();

        FoodItem[]? bestItems = null;
        int[]? bestServings = null;
        var bestK = 0;
        var bestScore = double.MaxValue;
        var bestMeets = false;
        var indices = Enumerable.Range(0, pool.Count).ToArray();

        for (var iter = 0; iter < 600; iter++)
        {
            // Random distinct item set, one serving each.
            var k = rng.Next(minItems, maxItems + 1);
            for (var i = 0; i < k; i++)
            {
                var j = rng.Next(i, indices.Length);
                (indices[i], indices[j]) = (indices[j], indices[i]);
            }
            var items = new FoodItem[k];
            var servings = new int[k];
            for (var i = 0; i < k; i++)
            {
                items[i] = pool[indices[i]];
                servings[i] = 1;
            }

            double Calories()
            {
                var c = 0.0;
                for (var i = 0; i < k; i++) c += (items[i].Calories ?? 0) * servings[i];
                return c;
            }

            double NutrientTotal(string nutrientKey)
            {
                var v = 0.0;
                for (var i = 0; i < k; i++) v += (items[i].Nutrient(nutrientKey) ?? 0) * servings[i];
                return v;
            }

            // Asymmetric score: exceeding the calorie cap and missing nutrient minimums are heavily
            // penalized; staying under the calorie target is only mildly penalized (fill it up).
            double Score(out bool meets)
            {
                var cal = Calories();
                var score = 0.0;
                meets = true;
                if (cal > calCap)
                {
                    score += (cal - calCap) / t.Calories * 8.0;
                    meets = false;
                }
                else
                {
                    score += Math.Max(0, t.Calories - cal) / t.Calories;
                }
                foreach (var (nk, target) in targets)
                {
                    var have = NutrientTotal(nk);
                    if (have < target)
                    {
                        score += (target - have) / target * 4.0;
                        if (have < target * (1 - tol)) meets = false;
                    }
                }
                return score;
            }

            // Greedy phase 1: satisfy nutrient minimums by repeatedly bumping the item that
            // delivers the most missing nutrients per calorie, while it still fits under the cap.
            for (var guard = 0; guard < 200 && targets.Length > 0; guard++)
            {
                if (!targets.Any(kv => NutrientTotal(kv.Key) < kv.Value)) break;
                var cal = Calories();
                var bestIdx = -1;
                var bestRatio = 0.0;
                for (var i = 0; i < k; i++)
                {
                    if (servings[i] >= maxServings) continue;
                    var itemCal = items[i].Calories ?? 0;
                    if (cal + itemCal > calCap) continue;
                    var gain = 0.0;
                    foreach (var (nk, target) in targets)
                    {
                        var deficit = target - NutrientTotal(nk);
                        if (deficit > 0) gain += Math.Min(items[i].Nutrient(nk) ?? 0, deficit) / target;
                    }
                    if (gain <= 0) continue;
                    var ratio = gain / Math.Max(itemCal, 1);
                    if (ratio > bestRatio)
                    {
                        bestRatio = ratio;
                        bestIdx = i;
                    }
                }
                if (bestIdx < 0) break;
                servings[bestIdx]++;
            }

            // Greedy phase 2: fill remaining room up toward the calorie target with random bumps.
            for (var guard = 0; guard < 100; guard++)
            {
                var cal = Calories();
                if (cal >= t.Calories * (1 - tol)) break;
                var fillable = new List<int>();
                for (var i = 0; i < k; i++)
                {
                    var itemCal = items[i].Calories ?? 0;
                    if (servings[i] < maxServings && itemCal > 0 && cal + itemCal <= calCap)
                        fillable.Add(i);
                }
                if (fillable.Count == 0) break;
                servings[fillable[rng.Next(fillable.Count)]]++;
            }

            // Local search: single-serving tweaks while they improve the score.
            var current = Score(out var meets);
            var improved = true;
            for (var pass = 0; improved && pass < 20; pass++)
            {
                improved = false;
                for (var i = 0; i < k; i++)
                {
                    foreach (var delta in Deltas)
                    {
                        var ns = servings[i] + delta;
                        if (ns < 1 || ns > maxServings) continue;
                        servings[i] = ns;
                        var s = Score(out var m);
                        if (s < current - 1e-9)
                        {
                            current = s;
                            meets = m;
                            improved = true;
                        }
                        else
                        {
                            servings[i] -= delta;
                        }
                    }
                }
            }

            if (current < bestScore)
            {
                bestScore = current;
                bestMeets = meets;
                bestItems = items;
                bestServings = servings;
                bestK = k;
                // Perfect enough: minimums met, under the cap, and filled to within tolerance.
                if (meets && current <= tol + 0.001) break;
            }
        }

        var entries = new List<ScheduleEntry>(bestK);
        for (var i = 0; i < bestK; i++)
            entries.Add(new ScheduleEntry { ItemName = bestItems![i].Name, Servings = bestServings![i] });
        entries.Sort((a, b) => string.Compare(a.ItemName, b.ItemName, StringComparison.OrdinalIgnoreCase));
        return (entries, bestMeets);
    }

    private static readonly int[] Deltas = { 1, -1 };
}
