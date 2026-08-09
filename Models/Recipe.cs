namespace CalorieTracker.Models;

/// <summary>
/// One ingredient of a recipe. Nutrition is stored per 100 of the base unit (g or ml) —
/// the "per unit of mass" definition — so it rescales when the amount changes.
/// </summary>
public class RecipeIngredient
{
    public string Name { get; set; } = "";
    public double Amount { get; set; } = 100;
    public string Unit { get; set; } = "g";
    public double? CaloriesPer100 { get; set; }
    public Dictionary<string, double> NutrientsPer100 { get; set; } = new();

    public string BaseUnit => Units.BaseUnitFor(Unit);
    public double? AmountInBase => Units.ToBase(Amount, Unit);

    /// <summary>Calories contributed by this ingredient at its amount.</summary>
    public double? Calories =>
        CaloriesPer100 is { } per100 && AmountInBase is { } b ? per100 * b / 100.0 : null;

    public double? Nutrient(string key) =>
        NutrientsPer100.TryGetValue(key, out var per100) && AmountInBase is { } b ? per100 * b / 100.0 : null;

    public string AmountDisplay => $"{Amount:0.##} {Unit}";

    public RecipeIngredient Clone() => new()
    {
        Name = Name,
        Amount = Amount,
        Unit = Unit,
        CaloriesPer100 = CaloriesPer100,
        NutrientsPer100 = new Dictionary<string, double>(NutrientsPer100),
    };
}

/// <summary>
/// A named collection of ingredients that makes a number of servings. Scheduled like any
/// other menu item: one serving = 1/Servings of the whole recipe.
/// </summary>
public class Recipe
{
    public string Name { get; set; } = "";
    public double Servings { get; set; } = 1;
    public string? Description { get; set; }
    public bool ExcludeFromRandom { get; set; }
    public List<RecipeIngredient> Ingredients { get; set; } = new();

    /// <summary>Whole-recipe calories; null when no ingredient specifies any.</summary>
    public double? TotalCalories() =>
        Ingredients.Any(i => i.Calories is not null) ? Ingredients.Sum(i => i.Calories ?? 0) : null;

    public Dictionary<string, double> TotalNutrients()
    {
        var totals = new Dictionary<string, double>();
        foreach (var ing in Ingredients)
            foreach (var key in ing.NutrientsPer100.Keys)
                if (ing.Nutrient(key) is { } v)
                    totals[key] = totals.GetValueOrDefault(key) + v;
        return totals;
    }

    public double? PerServingCalories() =>
        TotalCalories() is { } total && Servings > 0 ? total / Servings : null;

    /// <summary>The recipe as a per-serving menu item, so it slots into the schedule/totals/randomizer.</summary>
    public FoodItem ToMenuItem()
    {
        var servings = Servings > 0 ? Servings : 1;
        var nutrients = new Dictionary<string, double>();
        foreach (var (key, value) in TotalNutrients())
            nutrients[key] = Math.Round(value / servings, 1);
        return new FoodItem
        {
            Name = Name,
            Category = FoodCategory.Food,
            ServingSize = servings > 1 ? $"1 of {servings:0.##} servings" : "1 serving",
            Description = Description,
            Calories = TotalCalories() is { } total ? Math.Round(total / servings) : null,
            ExcludeFromRandom = ExcludeFromRandom,
            Nutrients = nutrients,
        };
    }

    public Recipe Clone() => new()
    {
        Name = Name,
        Servings = Servings,
        Description = Description,
        ExcludeFromRandom = ExcludeFromRandom,
        Ingredients = Ingredients.Select(i => i.Clone()).ToList(),
    };
}
