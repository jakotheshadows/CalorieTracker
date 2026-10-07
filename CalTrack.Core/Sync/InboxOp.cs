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

    /// <summary>Unique per op; the idempotency key recorded in <see cref="AppData.ProcessedOpIds"/>.</summary>
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Kind { get; set; } = "";

    /// <summary>Target day, "yyyy-MM-dd".</summary>
    public string Date { get; set; } = "";
    public double Servings { get; set; }

    /// <summary><see cref="LogFood"/>: the menu item or recipe name.</summary>
    public string? ItemName { get; set; }

    /// <summary>
    /// <see cref="LogFood"/>: the item's per-serving nutrition as resolved when the op was
    /// written. If the user renames or deletes the menu item before the app ingests the op,
    /// the entry is still logged — as a one-off with these numbers — instead of being lost.
    /// </summary>
    public FoodItem? ItemSnapshot { get; set; }

    /// <summary><see cref="LogAdHoc"/>: the one-off item.</summary>
    public FoodItem? AdHoc { get; set; }

    /// <summary>Who asked for it, e.g. "mcp"; shown when the app reports what landed.</summary>
    public string? Source { get; set; }
}
