using System.ComponentModel;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CalTrack.Mcp;

/// <summary>
/// The app's Randomize. The server runs the shared planner and sends the app the concrete
/// days it generated, so the plan reported to the user is exactly the plan that lands.
/// </summary>
public static partial class CalTrackTools
{
    private const int MaxPlanDays = 92;

    [McpServerTool(Name = "plan_random_days", Destructive = true)]
    [Description(
        "Fill days with a random plan from the user's menu, like CalTrack's Randomize: each day gets a few menu items " +
        "and recipes adding up to about the calorie target (never more than the tolerance over it), meeting any " +
        "nutrient minimums it can. Days that already have entries are left alone unless overwriteExisting is true " +
        "(which replaces them — ask the user first). Only when the user asks for a plan. The result lists every planned day.")]
    public static string PlanRandomDays(
        DataFolderStore store,
        TimeProvider clock,
        [Description("First day, yyyy-MM-dd or \"today\" / \"yesterday\".")] string from,
        [Description("Last day, yyyy-MM-dd. Defaults to the first day.")] string? to = null,
        [Description("Daily calorie target. Defaults to the suggested daily calories from the user's goals.")] double? calories = null,
        [Description("How far over the target a day may go, in percent. Defaults to 5.")] double tolerancePercent = 5,
        [Description("Fewest different foods per day. Defaults to 3.")] int minItemsPerDay = 3,
        [Description("Most different foods per day. Defaults to 6.")] int maxItemsPerDay = 6,
        [Description("Most servings of one food per day. Defaults to 3.")] int maxServingsPerItem = 3,
        [Description("Daily minimums for nutrients, e.g. {\"protein\": 120, \"fiber\": 30}. Keys and units: " + NutrientKeys + ".")]
        Dictionary<string, double>? nutrientTargets = null,
        [Description("true to replace days that already have entries.")] bool overwriteExisting = false)
    {
        var first = ResolveDate(from ?? throw new McpException("from is required."), clock);
        var last = to is null ? first : ResolveDate(to, clock);
        if (first > last) throw new McpException($"from ({AppData.DayKey(first)}) is after to ({AppData.DayKey(last)}).");
        var span = last.DayNumber - first.DayNumber + 1;
        if (span > MaxPlanDays) throw new McpException($"At most {MaxPlanDays} days at a time (asked for {span}).");

        var snap = store.Read();
        var view = snap.View;
        var target = calories ?? (view.GoalStatus(Today(clock)) is { } goal
            ? Math.Round(goal.SuggestedCalories)
            : throw new McpException("calories is required: the user has no goals set up to take a daily target from."));
        if (!(target >= 500 && target <= 10000)) throw new McpException($"calories must be between 500 and 10000 a day (got {target}).");
        if (!(tolerancePercent >= 0.5 && tolerancePercent <= 50)) throw new McpException($"tolerancePercent must be between 0.5 and 50 (got {tolerancePercent}).");
        if (minItemsPerDay < 1 || maxItemsPerDay < minItemsPerDay || maxItemsPerDay > 20)
            throw new McpException($"Need 1 ≤ minItemsPerDay ≤ maxItemsPerDay ≤ 20 (got {minItemsPerDay} and {maxItemsPerDay}).");
        if (maxServingsPerItem is < 1 or > 10) throw new McpException($"maxServingsPerItem must be 1 to 10 (got {maxServingsPerItem}).");
        foreach (var (key, min) in nutrientTargets ?? new())
        {
            if (NutrientCatalog.Find(key) is null) throw new McpException($"Unknown nutrient \"{key}\". Keys: {NutrientKeys}.");
            if (!(min > 0)) throw new McpException($"The {key} minimum must be greater than 0 (got {min}).");
        }

        var targets = new RandomTargets
        {
            Calories = target,
            TolerancePercent = tolerancePercent,
            MinItemsPerDay = minItemsPerDay,
            MaxItemsPerDay = maxItemsPerDay,
            MaxServingsPerItem = maxServingsPerItem,
            NutrientTargets = nutrientTargets ?? new(),
        };
        var plan = Planner.Plan(view, first, last, targets, overwriteExisting, new Random());
        if (plan.Error is not null) throw new McpException(plan.Error);
        if (plan.Days.Count == 0)
            throw new McpException($"Every day from {AppData.DayKey(first)} to {AppData.DayKey(last)} already has entries, so nothing was planned. " +
                                   "Pass overwriteExisting: true to replace them (ask the user first).");

        var op = NewOp(InboxOp.SetDays, clock);
        op.Days = new Dictionary<string, List<ScheduleEntry>>(plan.Days);
        op.Overwrite = overwriteExisting;
        var (after, status, _) = Submit(store, snap, op, "plan days");
        return Json(new
        {
            planned = plan.Days.Select(d => new
            {
                date = d.Key,
                calories = Round(d.Value.Sum(e => (view.ResolveItem(e.ItemName)?.Calories ?? 0) * e.Servings)),
                entries = d.Value.Select(e => new { name = e.ItemName, servings = e.Servings }).ToList(),
            }).ToList(),
            dailyCalorieTarget = target,
            leftAlone = plan.Skipped.Count > 0 ? plan.Skipped : null,
            replaced = overwriteExisting
                ? plan.Days.Keys.Where(k => snap.View.Days.TryGetValue(k, out var e) && e.Count > 0).ToList() is { Count: > 0 } r ? r : null
                : null,
            warning = Planner.MissedWarning(plan),
            status,
            note = QueuedNote,
        });
    }
}
