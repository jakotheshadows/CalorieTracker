namespace CalorieTracker.Models;

/// <summary>
/// A change to a menu item: only the fields that are set change. Edits arrive as patches,
/// not whole items, so an edit made in the app between the request and its arrival isn't
/// overwritten with stale values.
/// </summary>
public class FoodItemPatch
{
    public string? NewName { get; set; }
    public FoodCategory? Category { get; set; }

    /// <summary>Empty string clears it.</summary>
    public string? ServingSize { get; set; }

    /// <summary>Empty string clears it.</summary>
    public string? Description { get; set; }

    public double? Calories { get; set; }

    /// <summary>Per-serving nutrients to set, keyed by <see cref="NutrientDef.Key"/>; a null value removes one.</summary>
    public Dictionary<string, double?>? Nutrients { get; set; }

    public bool? ExcludeFromRandom { get; set; }
}

/// <summary>A new amount for one recipe ingredient, in a unit of the same kind (mass or volume).</summary>
public class IngredientAmount
{
    public string Name { get; set; } = "";
    public double Amount { get; set; }
    public string Unit { get; set; } = "g";
}

/// <summary>A change to a recipe. Applied in order: remove, re-amount, add.</summary>
public class RecipePatch
{
    public string? NewName { get; set; }
    public double? Servings { get; set; }

    /// <summary>Empty string clears it.</summary>
    public string? Description { get; set; }

    public bool? ExcludeFromRandom { get; set; }

    /// <summary>Ingredient names to remove (case-insensitive).</summary>
    public List<string>? RemoveIngredients { get; set; }

    public List<IngredientAmount>? IngredientAmounts { get; set; }

    public List<RecipeIngredient>? AddIngredients { get; set; }
}

/// <summary>A change to the goal profile. Setting goals up the first time needs sex, birth date, height and target weight.</summary>
public class GoalsPatch
{
    public UnitSystem? Units { get; set; }
    public Sex? Sex { get; set; }
    public DateOnly? BirthDate { get; set; }
    public double? HeightCm { get; set; }
    public ActivityLevel? Activity { get; set; }
    public bool? UseLeanMassFormula { get; set; }
    public double? BodyFatPercent { get; set; }
    public double? TargetWeightKg { get; set; }
    public double? TargetRatePercentPerWeek { get; set; }
}
