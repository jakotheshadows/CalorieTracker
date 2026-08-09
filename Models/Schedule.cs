namespace CalorieTracker.Models;

/// <summary>One line of a day's plan: a menu item and how many servings of it.</summary>
public class ScheduleEntry
{
    public string ItemName { get; set; } = "";
    public double Servings { get; set; } = 1;
}

/// <summary>The whole persisted app state: menu items plus the schedule (days keyed "yyyy-MM-dd").</summary>
public class AppData
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime LastModifiedUtc { get; set; }
    public List<FoodItem> Items { get; set; } = new();
    public Dictionary<string, List<ScheduleEntry>> Days { get; set; } = new();

    /// <summary>Reusable day plans keyed by template name (unique, case-insensitive).</summary>
    public Dictionary<string, List<ScheduleEntry>> Templates { get; set; } = new();

    /// <summary>Recipes; names share the menu-item namespace (unique, case-insensitive).</summary>
    public List<Recipe> Recipes { get; set; } = new();

    /// <summary>Body weight log in kilograms, keyed "yyyy-MM-dd" (one entry per day).</summary>
    public Dictionary<string, double> Weights { get; set; } = new();

    /// <summary>Profile + weight goal; null until the user sets goals up.</summary>
    public GoalSettings? Goals { get; set; }

    public static string DayKey(DateOnly d) => d.ToString("yyyy-MM-dd");
}

/// <summary>Aggregated calories + nutrients over any set of schedule entries.</summary>
public class Totals
{
    public double Calories { get; private set; }
    public Dictionary<string, double> Nutrients { get; } = new();
    public int EntryCount { get; private set; }
    public int DaysWithEntries { get; set; }

    public void Add(FoodItem item, double servings)
    {
        Calories += (item.Calories ?? 0) * servings;
        foreach (var (key, value) in item.Nutrients)
            Nutrients[key] = Nutrients.GetValueOrDefault(key) + value * servings;
        EntryCount++;
    }

    public double? Nutrient(string key) => Nutrients.TryGetValue(key, out var v) ? v : null;
}

/// <summary>Targets for random schedule generation. Calories is the only required target and must be &gt; 0.</summary>
public class RandomTargets
{
    public double Calories { get; set; } = 2000;
    public double TolerancePercent { get; set; } = 5;
    public int MinItemsPerDay { get; set; } = 3;
    public int MaxItemsPerDay { get; set; } = 6;
    public int MaxServingsPerItem { get; set; } = 3;

    /// <summary>Optional macro/micro targets keyed by nutrient key.</summary>
    public Dictionary<string, double> NutrientTargets { get; set; } = new();
}
