using CalorieTracker.Models;
using CalorieTracker.Services;

namespace CalorieTracker.Data;

/// <summary>
/// Every edit the app's screens make, as pure operations on <see cref="AppData"/>. The app's
/// AppState calls these and then saves; the data-folder inbox applies the MCP server's
/// requests with the very same calls. Each returns an error (and changes nothing) or null.
/// </summary>
public static partial class AppDataOps
{
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---------- Menu items ----------

    /// <summary>Add (<paramref name="originalName"/> null) or replace a menu item. A rename carries its log and template entries along.</summary>
    public static string? UpsertItem(this AppData data, FoodItem item, string? originalName)
    {
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";

        var duplicate = data.Items.FirstOrDefault(i => Same(i.Name, name) && !Same(i.Name, originalName));
        if (duplicate is not null) return $"An item named \"{duplicate.Name}\" already exists. Names must be unique.";
        if (data.FindRecipe(name) is { } recipeDup) return $"A recipe named \"{recipeDup.Name}\" already exists. Names must be unique across items and recipes.";
        item.Name = name;

        var existing = originalName is null ? null : data.FindItem(originalName);
        if (existing is null)
        {
            data.Items.Add(item);
        }
        else
        {
            data.Items[data.Items.IndexOf(existing)] = item;
            if (!Same(existing.Name, name)) data.RenameReferences(existing.Name, name);
        }
        data.Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Apply a patch to a menu item (not a recipe).</summary>
    public static string? UpdateItem(this AppData data, string name, FoodItemPatch patch)
    {
        if (data.FindItem(name) is not { } item) return $"\"{name}\" is not a menu item.";
        var copy = item.Clone();
        if (patch.NewName is { } newName) copy.Name = newName;
        if (patch.Category is { } category) copy.Category = category;
        if (patch.ServingSize is { } serving) copy.ServingSize = serving.Trim().Length == 0 ? null : serving.Trim();
        if (patch.Description is { } description) copy.Description = description.Trim().Length == 0 ? null : description.Trim();
        if (patch.Calories is { } calories)
        {
            if (!(calories >= 0) || double.IsInfinity(calories)) return $"Calories must be 0 or more (got {calories}).";
            copy.Calories = calories;
        }
        foreach (var (key, value) in patch.Nutrients ?? new())
        {
            if (NutrientCatalog.Find(key) is null) return $"Unknown nutrient \"{key}\".";
            if (value is not { } v) copy.Nutrients.Remove(key);
            else if (!(v >= 0) || double.IsInfinity(v)) return $"{NutrientCatalog.LabelFor(key)} must be 0 or more (got {v}).";
            else copy.Nutrients[key] = v;
        }
        if (patch.ExcludeFromRandom is { } exclude) copy.ExcludeFromRandom = exclude;
        return data.UpsertItem(copy, item.Name);
    }

    /// <summary>
    /// Delete a menu item. Log and template entries that used it keep its numbers, as
    /// one-offs, so past days' totals don't change. Returns how many entries that was, or
    /// null when there's no such item.
    /// </summary>
    public static int? DeleteItem(this AppData data, string name)
    {
        if (data.FindItem(name) is not { } item) return null;
        data.Items.Remove(item);
        return data.KeepAsOneOffs(item);
    }

    // ---------- Recipes ----------

    /// <summary>Add (<paramref name="originalName"/> null) or replace a recipe. A rename carries its log and template entries along.</summary>
    public static string? UpsertRecipe(this AppData data, Recipe recipe, string? originalName)
    {
        var name = recipe.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";
        if (!(recipe.Servings > 0) || double.IsInfinity(recipe.Servings)) return "Servings must be greater than zero.";

        var duplicate = data.Recipes.FirstOrDefault(r => Same(r.Name, name) && !Same(r.Name, originalName));
        if (duplicate is not null) return $"A recipe named \"{duplicate.Name}\" already exists. Names must be unique.";
        if (data.FindItem(name) is { } itemDup) return $"An item named \"{itemDup.Name}\" already exists. Names must be unique across items and recipes.";
        recipe.Name = name;

        var existing = originalName is null ? null : data.FindRecipe(originalName);
        if (existing is null)
        {
            data.Recipes.Add(recipe);
        }
        else
        {
            data.Recipes[data.Recipes.IndexOf(existing)] = recipe;
            if (!Same(existing.Name, name)) data.RenameReferences(existing.Name, name);
        }
        data.Recipes.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Add (<paramref name="index"/> null) or replace one ingredient of a recipe.</summary>
    public static string? SaveIngredient(this AppData data, string recipeName, RecipeIngredient ingredient, int? index)
    {
        if (data.FindRecipe(recipeName) is not { } recipe) return "Recipe not found.";
        if (CheckIngredient(ingredient) is { } error) return error;
        ingredient.Name = ingredient.Name.Trim();
        if (index is { } i && i >= 0 && i < recipe.Ingredients.Count) recipe.Ingredients[i] = ingredient;
        else recipe.Ingredients.Add(ingredient);
        return null;
    }

    public static bool RemoveIngredient(this AppData data, string recipeName, int index)
    {
        if (data.FindRecipe(recipeName) is not { } recipe || index < 0 || index >= recipe.Ingredients.Count) return false;
        recipe.Ingredients.RemoveAt(index);
        return true;
    }

    /// <summary>Apply a patch to a recipe: remove ingredients, change amounts, add ingredients, then the rest.</summary>
    public static string? UpdateRecipe(this AppData data, string name, RecipePatch patch)
    {
        if (data.FindRecipe(name) is not { } recipe) return $"\"{name}\" is not a recipe.";
        var copy = recipe.Clone();
        foreach (var remove in patch.RemoveIngredients ?? new())
            if (copy.Ingredients.RemoveAll(i => Same(i.Name, remove?.Trim())) == 0)
                return $"\"{recipe.Name}\" has no ingredient named \"{remove}\".";
        foreach (var change in patch.IngredientAmounts ?? new())
        {
            if (copy.Ingredients.FirstOrDefault(i => Same(i.Name, change.Name?.Trim())) is not { } ingredient)
                return $"\"{recipe.Name}\" has no ingredient named \"{change.Name}\".";
            var unit = change.Unit?.Trim().ToLowerInvariant() ?? "";
            if (Units.ToBase(change.Amount, unit) is null) return $"\"{change.Name}\": amount must be greater than 0 in g, kg, oz, lb, ml, l or fl oz (got {change.Amount} {change.Unit}).";
            // Nutrition is stored per 100 g or ml; switching between mass and volume would misread it.
            if (Units.BaseUnitFor(unit) != ingredient.BaseUnit) return $"\"{change.Name}\" is measured by {(ingredient.BaseUnit == "ml" ? "volume" : "weight")}; use {string.Join(", ", Units.ForBase(ingredient.BaseUnit))}.";
            ingredient.Amount = change.Amount;
            ingredient.Unit = unit;
        }
        foreach (var add in patch.AddIngredients ?? new())
        {
            if (CheckIngredient(add) is { } error) return error;
            var copyOfAdd = add.Clone();
            copyOfAdd.Name = copyOfAdd.Name.Trim();
            copy.Ingredients.Add(copyOfAdd);
        }
        if (patch.NewName is { } newName) copy.Name = newName;
        if (patch.Servings is { } servings) copy.Servings = servings;
        if (patch.Description is { } description) copy.Description = description.Trim().Length == 0 ? null : description.Trim();
        if (patch.ExcludeFromRandom is { } exclude) copy.ExcludeFromRandom = exclude;
        return data.UpsertRecipe(copy, recipe.Name);
    }

    /// <summary>Delete a recipe; like <see cref="DeleteItem"/>, its log and template entries keep its numbers as one-offs.</summary>
    public static int? DeleteRecipe(this AppData data, string name)
    {
        if (data.FindRecipe(name) is not { } recipe) return null;
        data.Recipes.Remove(recipe);
        return data.KeepAsOneOffs(recipe.ToMenuItem());
    }

    private static string? CheckIngredient(RecipeIngredient ingredient)
    {
        if (string.IsNullOrWhiteSpace(ingredient.Name)) return "Ingredient name is required.";
        if (ingredient.AmountInBase is null) return "Enter an amount greater than zero.";
        return null;
    }

    /// <summary>Point regular log and template entries at a renamed item (one-offs that share the name are their own thing).</summary>
    private static void RenameReferences(this AppData data, string from, string to)
    {
        foreach (var list in data.Days.Values.Concat(data.Templates.Values))
            foreach (var e in list.Where(e => e.AdHoc is null && Same(e.ItemName, from)))
                e.ItemName = to;
    }

    /// <summary>Turn regular entries for a deleted item into one-offs carrying its per-serving numbers.</summary>
    private static int KeepAsOneOffs(this AppData data, FoodItem snapshot)
    {
        var count = 0;
        foreach (var list in data.Days.Values.Concat(data.Templates.Values))
            foreach (var e in list.Where(e => e.AdHoc is null && Same(e.ItemName, snapshot.Name)))
            {
                e.AdHoc = snapshot.Clone();
                e.ItemName = snapshot.Name;
                count++;
            }
        return count;
    }

    /// <summary>Dates (yyyy-MM-dd) and template names whose regular entries use <paramref name="name"/>.</summary>
    public static (List<string> Days, List<string> Templates) UsesOf(this AppData data, string name) =>
        (data.Days.Where(d => d.Value.Any(e => e.AdHoc is null && Same(e.ItemName, name))).Select(d => d.Key).Order().ToList(),
         data.Templates.Where(t => t.Value.Any(e => e.AdHoc is null && Same(e.ItemName, name))).Select(t => t.Key).Order().ToList());

    // ---------- Log entries ----------

    /// <summary>
    /// A day's entry by name: regular entries merge by name, so there's at most one; one-offs
    /// can repeat, so <paramref name="occurrence"/> (1-based) picks among same-named ones.
    /// </summary>
    public static ScheduleEntry? FindEntry(this AppData data, DateOnly date, string name, bool oneOff, int occurrence = 1) =>
        occurrence < 1 ? null : data.GetDay(date).Where(e => (e.AdHoc is not null) == oneOff && Same(e.ItemName, name?.Trim())).Skip(occurrence - 1).FirstOrDefault();

    /// <summary>Which same-named entry of its kind this is (1-based), the inverse of <see cref="FindEntry"/>.</summary>
    public static int OccurrenceOf(this AppData data, DateOnly date, ScheduleEntry entry) =>
        data.GetDay(date).Where(e => (e.AdHoc is not null) == (entry.AdHoc is not null) && Same(e.ItemName, entry.ItemName)).TakeWhile(e => e != entry).Count() + 1;

    /// <summary>Change an entry's servings; zero or less removes it.</summary>
    public static void SetServings(this AppData data, DateOnly date, ScheduleEntry entry, double servings)
    {
        if (servings <= 0) data.RemoveEntry(date, entry);
        else entry.Servings = servings;
    }

    public static bool RemoveEntry(this AppData data, DateOnly date, ScheduleEntry entry)
    {
        var key = AppData.DayKey(date);
        if (!data.Days.TryGetValue(key, out var list) || !list.Remove(entry)) return false;
        if (list.Count == 0) data.Days.Remove(key);
        return true;
    }

    /// <summary>Remove every entry from a day; returns how many there were.</summary>
    public static int ClearDay(this AppData data, DateOnly date) =>
        data.Days.Remove(AppData.DayKey(date), out var list) ? list.Count : 0;

    /// <summary>Add a one-off to a day — or, with <paramref name="saveToMenu"/>, add it to the menu and log that.</summary>
    public static string? AddAdHocEntry(this AppData data, DateOnly date, FoodItem item, double servings, bool saveToMenu)
    {
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";
        if (!(servings > 0)) return "Servings must be greater than zero.";
        item.Name = name;
        if (saveToMenu)
        {
            if (data.UpsertItem(item, null) is { } error) return error;
            data.AddEntry(date, name, servings);
        }
        else
        {
            data.AddAdHoc(date, item, servings);
        }
        return null;
    }

    /// <summary>
    /// Replace a one-off entry's item. With <paramref name="saveToMenu"/> the item joins the
    /// menu and the entry becomes a regular one, merging with a regular entry for it if the day has one.
    /// </summary>
    public static string? UpdateAdHocEntry(this AppData data, DateOnly date, ScheduleEntry entry, FoodItem item, bool saveToMenu)
    {
        var name = item.Name?.Trim() ?? "";
        if (name.Length == 0) return "Name is required.";
        item.Name = name;
        if (saveToMenu)
        {
            if (data.UpsertItem(item, null) is { } error) return error;
            entry.AdHoc = null;
            entry.ItemName = name;
            var list = data.DayList(date);
            if (list.FirstOrDefault(e => e != entry && e.AdHoc is null && Same(e.ItemName, name)) is { } twin)
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
        return null;
    }

    /// <summary>Replace whole days (yyyy-MM-dd → entries). Without <paramref name="overwrite"/>, days that have entries are left alone.</summary>
    public static int SetDays(this AppData data, IReadOnlyDictionary<string, List<ScheduleEntry>> days, bool overwrite)
    {
        var set = 0;
        foreach (var (key, entries) in days)
        {
            if (!overwrite && data.Days.TryGetValue(key, out var existing) && existing.Count > 0) continue;
            if (entries.Count == 0) data.Days.Remove(key);
            else data.Days[key] = entries.Select(e => new ScheduleEntry { ItemName = e.ItemName, Servings = e.Servings, AdHoc = e.AdHoc?.Clone() }).ToList();
            set++;
        }
        return set;
    }

    // ---------- Day templates ----------

    /// <summary>The stored spelling of a template name (names are unique case-insensitively), or null.</summary>
    public static string? FindTemplate(this AppData data, string? name) =>
        data.Templates.Keys.FirstOrDefault(k => Same(k, name?.Trim()));

    /// <summary>Save a copy of <paramref name="entries"/> as a template. An existing name is refused unless <paramref name="overwrite"/>.</summary>
    public static string? SaveTemplate(this AppData data, string name, List<ScheduleEntry> entries, bool overwrite)
    {
        name = name?.Trim() ?? "";
        if (name.Length == 0) return "Template name is required.";
        if (entries.Count == 0) return "This day has no entries to save.";
        if (data.FindTemplate(name) is { } existing)
        {
            if (!overwrite) return $"A template named \"{existing}\" already exists.";
            data.Templates.Remove(existing);
        }
        data.Templates[name] = entries.Select(e => new ScheduleEntry { ItemName = e.ItemName, Servings = e.Servings, AdHoc = e.AdHoc?.Clone() }).ToList();
        return null;
    }

    public static bool DeleteTemplate(this AppData data, string name) =>
        data.FindTemplate(name) is { } key && data.Templates.Remove(key);

    /// <summary>Add a template's entries to a day: regular entries merge with ones already there, one-offs land as their own copies.</summary>
    public static string? ApplyTemplate(this AppData data, DateOnly date, string name)
    {
        if (data.FindTemplate(name) is not { } key) return "Template not found.";
        var list = data.DayList(date);
        foreach (var t in data.Templates[key])
        {
            var existing = t.AdHoc is null ? list.FirstOrDefault(e => e.AdHoc is null && Same(e.ItemName, t.ItemName)) : null;
            if (existing is not null) existing.Servings += t.Servings;
            else list.Add(new ScheduleEntry { ItemName = t.ItemName, Servings = t.Servings, AdHoc = t.AdHoc?.Clone() });
        }
        return null;
    }

    // ---------- Weight log & goals ----------

    /// <summary>Record a day's weight (one per day; a second one replaces it).</summary>
    public static bool LogWeight(this AppData data, DateOnly date, double weightKg)
    {
        if (!(weightKg > 0) || double.IsInfinity(weightKg)) return false;
        data.Weights[AppData.DayKey(date)] = Math.Round(weightKg, 2);
        return true;
    }

    public static bool RemoveWeight(this AppData data, DateOnly date) => data.Weights.Remove(AppData.DayKey(date));

    /// <summary>Save the goal profile. Refuses heights out of range and targets below the healthy BMI floor.</summary>
    public static string? SaveGoals(this AppData data, GoalSettings goals)
    {
        if (!(goals.HeightCm >= 90 && goals.HeightCm <= 250)) return "Height looks out of range.";
        var heightM = goals.HeightCm / 100.0;
        var minHealthyKg = GoalMath.MinHealthyBmi * heightM * heightM;
        if (!(goals.TargetWeightKg >= minHealthyKg))
        {
            var min = goals.Units == UnitSystem.Imperial ? $"{GoalMath.KgToLb(minHealthyKg):0} lb" : $"{minHealthyKg:0.0} kg";
            return $"That target is below the healthy range for your height (BMI {GoalMath.MinHealthyBmi}, about {min}). CalTrack won't set targets below it.";
        }
        goals.TargetRatePercentPerWeek = Math.Clamp(goals.TargetRatePercentPerWeek, 0, GoalMath.MaxRatePercentPerWeek);
        data.Goals = goals;
        return null;
    }

    /// <summary>Apply a patch to the goal profile; setting goals up the first time needs sex, birth date, height and target weight.</summary>
    public static string? UpdateGoals(this AppData data, GoalsPatch patch)
    {
        if (data.Goals is null)
        {
            var missing = new[] { ("sex", patch.Sex is null), ("birth date", patch.BirthDate is null), ("height", patch.HeightCm is null), ("target weight", patch.TargetWeightKg is null) }
                .Where(m => m.Item2).Select(m => m.Item1).ToList();
            if (missing.Count > 0) return $"Goals aren't set up yet; setting them up needs {string.Join(", ", missing)}.";
        }
        var g = data.Goals is { } current ? Clone(current) : new GoalSettings();
        if (patch.Units is { } units) g.Units = units;
        if (patch.Sex is { } sex) g.Sex = sex;
        if (patch.BirthDate is { } birth) g.BirthDate = birth;
        if (patch.HeightCm is { } height) g.HeightCm = height;
        if (patch.Activity is { } activity) g.Activity = activity;
        if (patch.UseLeanMassFormula is { } lean) g.UseLeanMassFormula = lean;
        if (patch.BodyFatPercent is { } bf) g.BodyFatPercent = bf;
        if (patch.TargetWeightKg is { } target) g.TargetWeightKg = target;
        if (patch.TargetRatePercentPerWeek is { } rate) g.TargetRatePercentPerWeek = rate;
        if (g.UseLeanMassFormula && g.BodyFatPercent is not (> 0 and < 70)) return "The lean-mass formula needs a body fat % between 0 and 70.";
        return data.SaveGoals(g);
    }

    /// <summary>Goal status as the Goals screen shows it; null until goals are set up and a weight is logged.</summary>
    public static GoalStatus? GoalStatus(this AppData data, DateOnly today) =>
        data.Goals is null ? null : GoalMath.Compute(data.Goals, data.Weights, d => data.TotalsForDay(d).Calories, today);

    private static GoalSettings Clone(GoalSettings g) => new()
    {
        Units = g.Units,
        Sex = g.Sex,
        BirthDate = g.BirthDate,
        HeightCm = g.HeightCm,
        Activity = g.Activity,
        UseLeanMassFormula = g.UseLeanMassFormula,
        BodyFatPercent = g.BodyFatPercent,
        TargetWeightKg = g.TargetWeightKg,
        TargetRatePercentPerWeek = g.TargetRatePercentPerWeek,
    };
}
