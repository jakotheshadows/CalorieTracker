namespace CalorieTracker.Models;

public enum FoodCategory
{
    Food,
    Beverage,
    Snack,
}

/// <summary>A menu item serving. Only Name is required; Name is unique (case-insensitive).</summary>
public class FoodItem
{
    public string Name { get; set; } = "";
    public FoodCategory Category { get; set; } = FoodCategory.Food;
    public string? ServingSize { get; set; }
    public string? Description { get; set; }
    public double? Calories { get; set; }

    /// <summary>When true, the random schedule generator never picks this item.</summary>
    public bool ExcludeFromRandom { get; set; }

    /// <summary>Per-serving nutrient amounts keyed by <see cref="NutrientDef.Key"/>. Absent key = not specified.</summary>
    public Dictionary<string, double> Nutrients { get; set; } = new();

    public double? Nutrient(string key) => Nutrients.TryGetValue(key, out var v) ? v : null;

    public FoodItem Clone() => new()
    {
        Name = Name,
        Category = Category,
        ServingSize = ServingSize,
        Description = Description,
        Calories = Calories,
        ExcludeFromRandom = ExcludeFromRandom,
        Nutrients = new Dictionary<string, double>(Nutrients),
    };
}
