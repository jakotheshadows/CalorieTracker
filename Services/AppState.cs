using CalorieTracker.Data;
using CalorieTracker.Models;

namespace CalorieTracker.Services;

/// <summary>
/// In-memory application state persisted to localStorage — and, when the user connects a
/// data folder, to its file too (see AppState.Folder.cs). Owns menu items, the schedule,
/// nutrition aggregation, and random schedule generation. The data operations themselves
/// live in CalTrack.Core (AppDataOps) so the MCP server performs the identical logic.
/// </summary>
public partial class AppState(LocalStore store, DataFolder folder)
{
    public const string DataKey = "caltrack-data";

    public AppData Data { get; private set; } = new();
    public bool Loaded { get; private set; }

    /// <summary>Raised whenever data changes (UI should re-render).</summary>
    public event Action? Changed;

    public async Task EnsureLoadedAsync()
    {
        if (Loaded) return;
        try
        {
            Data = AppDataJson.Parse(await store.GetAsync(DataKey));
        }
        catch
        {
            // Corrupt/missing local data — start fresh rather than crash.
            Data = new AppData();
        }
        // A connected data folder is the shared source of truth: load from it when access
        // is already granted (never throws; keeps the browser copy above on any failure).
        await InitFolderAsync();
        Loaded = true;
        Changed?.Invoke();
    }

    public string ExportJson() => AppDataJson.Serialize(Data, indented: true);

    public async Task<string?> ImportJsonAsync(string json)
    {
        try
        {
            Data = AppDataJson.Parse(json);
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
        await store.SetAsync(DataKey, AppDataJson.Serialize(Data));
        // Write-through to the connected data folder (no-op when none is connected).
        await PersistToFolderAsync();
        Changed?.Invoke();
    }

    // ---------- Menu items ----------
    // The rules for every edit live in CalTrack.Core (AppDataOps), shared with the MCP
    // server's requests; these wrappers add saving.

    public FoodItem? FindItem(string name) => Data.FindItem(name);

    public Recipe? FindRecipe(string name) => Data.FindRecipe(name);

    /// <summary>
    /// Resolve a schedule/template name to a menu item: a plain item, or a recipe presented
    /// as its per-serving menu-item view. Null when the name matches neither.
    /// </summary>
    public FoodItem? ResolveItem(string name) => Data.ResolveItem(name);

    /// <summary>All schedulable menu items: plain items plus recipes as per-serving views.</summary>
    public IEnumerable<FoodItem> AllMenuItems() => Data.AllMenuItems();

    /// <summary>The item a schedule entry stands for: its embedded one-off item, or the named menu item/recipe.</summary>
    public FoodItem? ResolveEntry(ScheduleEntry entry) => Data.ResolveEntry(entry);

    /// <summary>Save after an edit that succeeded. Returns the edit's error, if any.</summary>
    private async Task<string?> SaveIfOk(string? error)
    {
        if (error is null) await PersistAsync();
        return error;
    }

    /// <summary>Add or update an item. <paramref name="originalName"/> is non-null when editing an existing item.</summary>
    public Task<string?> UpsertItemAsync(FoodItem item, string? originalName) => SaveIfOk(Data.UpsertItem(item, originalName));

    /// <summary>Delete an item; log and template entries that used it keep its numbers as one-offs.</summary>
    public async Task DeleteItemAsync(string name)
    {
        if (Data.DeleteItem(name) is not null) await PersistAsync();
    }

    // ---------- Recipes ----------

    /// <summary>Add or update a recipe. <paramref name="originalName"/> is non-null when editing an existing recipe.</summary>
    public Task<string?> UpsertRecipeAsync(Recipe recipe, string? originalName) => SaveIfOk(Data.UpsertRecipe(recipe, originalName));

    /// <summary>Add (index null) or replace one ingredient of a recipe. Null on success.</summary>
    public Task<string?> SaveIngredientAsync(string recipeName, RecipeIngredient ingredient, int? index) =>
        SaveIfOk(Data.SaveIngredient(recipeName, ingredient, index));

    public async Task RemoveIngredientAsync(string recipeName, int index)
    {
        if (Data.RemoveIngredient(recipeName, index)) await PersistAsync();
    }

    /// <summary>Delete a recipe; log and template entries that used it keep its numbers as one-offs.</summary>
    public async Task DeleteRecipeAsync(string name)
    {
        if (Data.DeleteRecipe(name) is not null) await PersistAsync();
    }

    // ---------- Schedule ----------

    public List<ScheduleEntry> GetDay(DateOnly date) => Data.GetDay(date);

    public async Task AddEntryAsync(DateOnly date, string itemName, double servings)
    {
        if (servings <= 0) return;
        Data.AddEntry(date, itemName, servings);
        await PersistAsync();
    }

    /// <summary>
    /// Add a one-off item to a day. When <paramref name="saveToMenu"/> is set the item is
    /// added to the menu instead and the entry references it normally. Null on success.
    /// </summary>
    public Task<string?> AddAdHocEntryAsync(DateOnly date, FoodItem item, double servings, bool saveToMenu) =>
        SaveIfOk(Data.AddAdHocEntry(date, item, servings, saveToMenu));

    /// <summary>
    /// Replace a one-off entry's embedded item. When <paramref name="saveToMenu"/> is set the
    /// item joins the menu and the entry converts to a regular reference. Null on success.
    /// </summary>
    public Task<string?> UpdateAdHocEntryAsync(DateOnly date, ScheduleEntry entry, FoodItem item, bool saveToMenu) =>
        SaveIfOk(Data.UpdateAdHocEntry(date, entry, item, saveToMenu));

    public async Task UpdateServingsAsync(DateOnly date, ScheduleEntry entry, double servings)
    {
        Data.SetServings(date, entry, servings);
        await PersistAsync();
    }

    public async Task RemoveEntryAsync(DateOnly date, ScheduleEntry entry)
    {
        Data.RemoveEntry(date, entry);
        await PersistAsync();
    }

    public async Task ClearDayAsync(DateOnly date)
    {
        if (Data.ClearDay(date) > 0) await PersistAsync();
    }

    // ---------- Day templates ----------

    public IEnumerable<string> TemplateNames =>
        Data.Templates.Keys.OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

    public bool TemplateExists(string name) => Data.FindTemplate(name) is not null;

    /// <summary>Save a snapshot of <paramref name="entries"/> under a template name. Existing names are rejected unless <paramref name="overwrite"/> is set.</summary>
    public Task<string?> SaveTemplateAsync(string name, List<ScheduleEntry> entries, bool overwrite = false) =>
        SaveIfOk(Data.SaveTemplate(name, entries, overwrite));

    public async Task DeleteTemplateAsync(string name)
    {
        if (Data.DeleteTemplate(name)) await PersistAsync();
    }

    /// <summary>Add a template's entries to a day, merging servings with entries already present.</summary>
    public Task<string?> ApplyTemplateAsync(DateOnly date, string name) => SaveIfOk(Data.ApplyTemplate(date, name));

    // ---------- Goals & weight log ----------

    public async Task LogWeightAsync(DateOnly date, double weightKg)
    {
        if (Data.LogWeight(date, weightKg)) await PersistAsync();
    }

    public async Task RemoveWeightAsync(DateOnly date)
    {
        if (Data.RemoveWeight(date)) await PersistAsync();
    }

    /// <summary>Save the goal profile. Rejects target weights below the healthy BMI floor.</summary>
    public Task<string?> SaveGoalsAsync(GoalSettings goals) => SaveIfOk(Data.SaveGoals(goals));

    /// <summary>Current goal status, or null when goals aren't set up or no weight is logged yet.</summary>
    public GoalStatus? GetGoalStatus() => Data.GoalStatus(DateOnly.FromDateTime(DateTime.Today));

    // ---------- Aggregation ----------

    public Totals TotalsForDay(DateOnly date) => Data.TotalsForDay(date);

    public Totals TotalsForRange(DateOnly start, DateOnly endInclusive)
    {
        var totals = new Totals();
        for (var d = start; d <= endInclusive; d = d.AddDays(1))
        {
            var before = totals.EntryCount;
            Data.AccumulateDay(totals, d);
            if (totals.EntryCount > before) totals.DaysWithEntries++;
        }
        return totals;
    }

    /// <summary>Sunday-based start of the week containing <paramref name="date"/>.</summary>
    public static DateOnly WeekStart(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    // ---------- Random generation ----------

    /// <summary>
    /// Fill each day in [start, end] with a randomized plan (see <see cref="Planner"/>).
    /// Returns an error, plus a warning when some days could not fully meet the targets.
    /// </summary>
    public async Task<(string? Error, string? Warning)> RandomizeRangeAsync(DateOnly start, DateOnly endInclusive, RandomTargets targets, bool overwriteExisting)
    {
        var plan = Planner.Plan(Data, start, endInclusive, targets, overwriteExisting, new Random());
        if (plan.Error is not null) return (plan.Error, null);
        Data.SetDays(plan.Days, overwrite: true);
        await PersistAsync();
        return (null, Planner.MissedWarning(plan));
    }

    public static bool IsRandomEligible(FoodItem item) => Planner.IsRandomEligible(item);
}
