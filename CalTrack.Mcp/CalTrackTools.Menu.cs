using System.ComponentModel;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>
/// Reading one menu entry in full, editing menu items and deleting them. Deleting keeps the
/// user's history: log entries that used the item keep its numbers as one-offs.
/// </summary>
public static partial class CalTrackTools
{
    private const string NutrientKeys =
        "protein, carbs, fat, fiber, sugar (g); sodium, potassium, calcium, iron, cholesterol, vitaminC (mg); vitaminA, vitaminD (µg)";

    [McpServerTool(Name = "get_menu_item", ReadOnly = true, Idempotent = true)]
    [Description(
        "Everything about one menu item or recipe: category, serving, description, calories and every nutrient " +
        "per serving, and for a recipe its servings and each ingredient's amount and nutrition. Also how many " +
        "logged days and templates use it. Use it before editing or deleting, or when the user asks what's in something.")]
    public static string GetMenuItem(
        DataFolderStore store,
        [Description("Exact menu item or recipe name, as returned by list_menu_items.")] string name)
    {
        var view = store.Read().View;
        var wanted = name?.Trim() ?? "";
        var (days, templates) = view.UsesOf(wanted);
        var usedIn = new { loggedDays = days.Count, lastLogged = days.LastOrDefault(), templates = templates.Count > 0 ? templates : null };
        if (view.FindItem(wanted) is { } item)
            return Json(new { kind = "item", item = ItemInfo(item), usedIn, nutrientUnits = NutrientKeys });
        if (view.FindRecipe(wanted) is { } recipe)
            return Json(new { kind = "recipe", recipe = RecipeInfo(recipe), usedIn, nutrientUnits = NutrientKeys });
        throw NotOnMenu(wanted, view);
    }

    [McpServerTool(Name = "update_menu_item", Destructive = true, Idempotent = true)]
    [Description(
        "Edit a menu item (not a recipe — use update_recipe): rename it, or change its category, serving, " +
        "description, calories, nutrients or whether Randomize may pick it. Only the fields you pass change. Use " +
        "only numbers the user gave or confirmed (e.g. from a label) — never estimate. Renaming carries its log " +
        "entries along. The result shows the item before and after.")]
    public static string UpdateMenuItem(
        DataFolderStore store,
        TimeProvider clock,
        [Description("The item's current exact name, as returned by list_menu_items.")] string name,
        [Description("A new name. Must not clash with another item or recipe.")] string? newName = null,
        [Description("Food, Beverage or Snack.")] string? category = null,
        [Description("What one serving is, e.g. \"1 cup (240 ml)\". Empty string clears it.")] string? servingSize = null,
        [Description("A note shown with the item. Empty string clears it.")] string? description = null,
        [Description("Calories per serving.")] double? calories = null,
        [Description("Per-serving nutrients to set, e.g. {\"protein\": 6.3, \"sodium\": 140}; null removes one. Keys and units: " + NutrientKeys + ".")]
        Dictionary<string, double?>? nutrients = null,
        [Description("true to keep Randomize from picking this item; false to allow it.")] bool? excludeFromRandom = null)
    {
        var snap = store.Read();
        var view = snap.View;
        var wanted = name?.Trim() ?? "";
        if (view.FindItem(wanted) is not { } item)
            throw view.FindRecipe(wanted) is { } r
                ? new McpException($"\"{r.Name}\" is a recipe — use update_recipe.")
                : NotOnMenu(wanted, view);

        var patch = new FoodItemPatch
        {
            NewName = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim(),
            Category = category is null ? null : ParseCategory(category),
            ServingSize = servingSize,
            Description = description,
            Calories = calories,
            Nutrients = nutrients,
            ExcludeFromRandom = excludeFromRandom,
        };
        if (patch is { NewName: null, Category: null, ServingSize: null, Description: null, Calories: null, ExcludeFromRandom: null } && nutrients is not { Count: > 0 })
            throw new McpException("Nothing to change: pass at least one field to update.");
        if (calories is { } kcal && (!(kcal >= 0) || kcal > MaxCaloriesPerServing))
            throw new McpException($"calories must be between 0 and {MaxCaloriesPerServing} per serving (got {kcal}).");
        CheckNutrients(nutrients);
        if (patch.NewName is { } renamed && view.ResolveItem(renamed) is { } clash && !string.Equals(clash.Name, item.Name, StringComparison.OrdinalIgnoreCase))
            throw new McpException($"\"{clash.Name}\" is already on the menu; pick another name.");

        var op = NewOp(InboxOp.UpdateItem, clock);
        op.ItemName = item.Name;
        op.ItemPatch = patch;
        var (after, status, _) = Submit(store, snap, op, "edit menu items");
        var updated = after.View.FindItem(patch.NewName ?? item.Name);
        return Json(new
        {
            before = ItemInfo(item),
            after = updated is null ? null : ItemInfo(updated),
            status,
            note = QueuedNote + (patch.NewName is not null ? " Log entries for it follow the new name." : ""),
        });
    }

    [McpServerTool(Name = "delete_menu_item", Destructive = true)]
    [Description(
        "Delete a menu item (not a recipe — use delete_recipe). The user's history is kept: log and template entries " +
        "that used it become one-offs with the same numbers, so past totals don't change. Only when the user asks " +
        "to delete it. The result has the full item, so it can be added back.")]
    public static string DeleteMenuItem(
        DataFolderStore store,
        TimeProvider clock,
        [Description("Exact menu item name, as returned by list_menu_items.")] string name)
    {
        var snap = store.Read();
        var view = snap.View;
        var wanted = name?.Trim() ?? "";
        if (view.FindItem(wanted) is not { } item)
            throw view.FindRecipe(wanted) is { } r
                ? new McpException($"\"{r.Name}\" is a recipe — use delete_recipe.")
                : NotOnMenu(wanted, view);

        var (days, templates) = view.UsesOf(item.Name);
        var op = NewOp(InboxOp.DeleteItem, clock);
        op.ItemName = item.Name;
        var (_, status, summary) = Submit(store, snap, op, "delete menu items");
        return Json(new
        {
            deleted = ItemInfo(item),
            historyKept = HistoryKept(days, templates),
            status,
            note = QueuedNote + " To add it back: add_menu_item with the numbers above (past entries stay one-offs).",
        });
    }

    // ---------- helpers ----------

    private static McpException NotOnMenu(string name, AppData view) =>
        new($"\"{name}\" is not on the user's menu, so nothing was changed. " +
            (NameMatcher.Closest(name, view.AllMenuItems().Select(i => i.Name), 5) is { Count: > 0 } close
                ? $"Closest menu names: {string.Join(", ", close.Select(c => $"\"{c}\""))}. "
                : "") +
            "Use an exact name from list_menu_items, or ask the user.");

    private static FoodCategory ParseCategory(string category) =>
        Enum.TryParse<FoodCategory>(category.Trim(), ignoreCase: true, out var c) && Enum.IsDefined(c)
            ? c
            : throw new McpException($"category must be Food, Beverage or Snack (got \"{category}\").");

    private static void CheckNutrients(Dictionary<string, double?>? nutrients)
    {
        foreach (var (key, value) in nutrients ?? new())
        {
            if (NutrientCatalog.Find(key) is not { } def)
                throw new McpException($"Unknown nutrient \"{key}\". Keys: {NutrientKeys}.");
            var max = def.Unit == "g" ? 1000 : 100000;
            if (value is { } v && (!(v >= 0) || v > max))
                throw new McpException($"{key} must be between 0 and {max} {def.Unit} per serving (got {v}).");
        }
    }

    private static Dictionary<string, double> NutrientMap(Dictionary<string, double> nutrients) =>
        NutrientCatalog.All.Where(n => nutrients.ContainsKey(n.Key)).ToDictionary(n => n.Key, n => Math.Round(nutrients[n.Key], 2));

    private static object ItemInfo(FoodItem i) => new
    {
        name = i.Name,
        category = i.Category.ToString(),
        servingSize = i.ServingSize,
        description = i.Description,
        calories = i.Calories,
        nutrients = NutrientMap(i.Nutrients),
        excludeFromRandom = i.ExcludeFromRandom ? true : (bool?)null,
    };

    private static object HistoryKept(List<string> days, List<string> templates) => new
    {
        loggedDays = days.Count,
        days = days.Count > 0 ? days.TakeLast(20).ToList() : null,
        templates = templates.Count > 0 ? templates : null,
        note = days.Count + templates.Count > 0 ? "These entries keep its numbers as one-offs." : null,
    };
}
