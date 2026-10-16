using CalorieTracker.Models;

namespace CalorieTracker.Sync;

/// <summary>
/// One change requested by an outside writer (the MCP server), delivered as its own file in
/// the data folder's <c>inbox/</c>. Outside writers never touch the main data file — the
/// app is its ONLY writer and folds inbox ops in idempotently. That is what makes a lost
/// update impossible: with two processes rewriting one JSON file, a write landing between
/// the other side's read and write vanishes silently, and the tool that made it would
/// still have reported success.
/// </summary>
public class InboxOp
{
    public const string LogFood = "log_food";
    public const string LogAdHoc = "log_adhoc";
    public const string AddItem = "add_item";
    public const string AddRecipe = "add_recipe";
    public const string UpdateEntry = "update_entry";
    public const string RemoveEntry = "remove_entry";
    public const string ClearDay = "clear_day";
    public const string UpdateItem = "update_item";
    public const string DeleteItem = "delete_item";
    public const string UpdateRecipe = "update_recipe";
    public const string DeleteRecipe = "delete_recipe";
    public const string SaveTemplate = "save_template";
    public const string ApplyTemplate = "apply_template";
    public const string DeleteTemplate = "delete_template";
    public const string LogWeight = "log_weight";
    public const string RemoveWeight = "remove_weight";
    public const string SetGoals = "set_goals";
    public const string SetDays = "set_days";

    /// <summary>Unique per op; the idempotency key recorded in <see cref="AppData.ProcessedOpIds"/>.</summary>
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Kind { get; set; } = "";

    /// <summary>Target day, "yyyy-MM-dd" (for <see cref="SaveTemplate"/>, the day to copy).</summary>
    public string Date { get; set; } = "";

    /// <summary><see cref="LogFood"/>, <see cref="LogAdHoc"/>: servings to add; <see cref="UpdateEntry"/>: the new servings.</summary>
    public double Servings { get; set; }

    /// <summary>
    /// The menu item or recipe the op is about — logged, edited or deleted — or, for entry
    /// ops, the entry's name.
    /// </summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// <see cref="UpdateEntry"/>, <see cref="RemoveEntry"/>: which entry — a one-off or a
    /// regular one, and which of the same-named ones (1-based; one-offs don't merge).
    /// </summary>
    public bool OneOff { get; set; }
    public int Occurrence { get; set; } = 1;

    /// <summary>Template ops: the template's name.</summary>
    public string? TemplateName { get; set; }

    /// <summary><see cref="SaveTemplate"/>: replace a template of that name; <see cref="SetDays"/>: replace days that have entries.</summary>
    public bool Overwrite { get; set; }

    /// <summary><see cref="LogWeight"/>: the weight in kilograms.</summary>
    public double? WeightKg { get; set; }

    /// <summary><see cref="UpdateItem"/>: what to change.</summary>
    public FoodItemPatch? ItemPatch { get; set; }

    /// <summary><see cref="UpdateRecipe"/>: what to change.</summary>
    public RecipePatch? RecipePatch { get; set; }

    /// <summary><see cref="SetGoals"/>: what to change.</summary>
    public GoalsPatch? GoalsPatch { get; set; }

    /// <summary><see cref="SetDays"/>: whole days, yyyy-MM-dd → entries (a generated random plan).</summary>
    public Dictionary<string, List<ScheduleEntry>>? Days { get; set; }

    /// <summary>
    /// <see cref="LogFood"/>: the item's per-serving nutrition as resolved when the op was
    /// written. If the user renames or deletes the menu item before the app ingests the op,
    /// the entry is still logged — as a one-off with these numbers — instead of being lost.
    /// </summary>
    public FoodItem? ItemSnapshot { get; set; }

    /// <summary><see cref="LogAdHoc"/>: the one-off item.</summary>
    public FoodItem? AdHoc { get; set; }

    /// <summary><see cref="AddItem"/>: the new menu item.</summary>
    public FoodItem? Item { get; set; }

    /// <summary><see cref="AddRecipe"/>: the new recipe, ingredients and all.</summary>
    public Recipe? Recipe { get; set; }

    /// <summary>Who asked for it, e.g. "mcp"; shown when the app reports what landed.</summary>
    public string? Source { get; set; }
}
