namespace CalorieTracker.Models;

public record NutrientDef(string Key, string Label, string Unit, bool IsMacro);

/// <summary>Fixed catalog of tracked nutrients. Values are stored per serving, keyed by <see cref="NutrientDef.Key"/>.</summary>
public static class NutrientCatalog
{
    public static readonly NutrientDef[] All =
    {
        new("protein",     "Protein",       "g",  true),
        new("carbs",       "Carbohydrates", "g",  true),
        new("fat",         "Fat",           "g",  true),
        new("fiber",       "Fiber",         "g",  false),
        new("sugar",       "Sugar",         "g",  false),
        new("sodium",      "Sodium",        "mg", false),
        new("potassium",   "Potassium",     "mg", false),
        new("calcium",     "Calcium",       "mg", false),
        new("iron",        "Iron",          "mg", false),
        new("cholesterol", "Cholesterol",   "mg", false),
        new("vitaminA",    "Vitamin A",     "µg", false),
        new("vitaminC",    "Vitamin C",     "mg", false),
        new("vitaminD",    "Vitamin D",     "µg", false),
    };

    public static IEnumerable<NutrientDef> Macros => All.Where(n => n.IsMacro);
    public static IEnumerable<NutrientDef> Micros => All.Where(n => !n.IsMacro);

    public static NutrientDef? Find(string key) => All.FirstOrDefault(n => n.Key == key);

    public static string LabelFor(string key) => Find(key)?.Label ?? key;
    public static string UnitFor(string key) => Find(key)?.Unit ?? "";
}
