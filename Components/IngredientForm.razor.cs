using CalorieTracker.Models;
using CalorieTracker.Services;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class IngredientForm
{
    [Parameter] public RecipeIngredient Ingredient { get; set; } = new();
    [Parameter] public string Title { get; set; } = "Add ingredient";
    [Parameter] public string SaveLabel { get; set; } = "Save";
    [Parameter] public string? Error { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    private void OnUsdaApplied(UsdaApplied a)
    {
        Ingredient.Amount = a.Amount;
        Ingredient.Unit = a.Unit;
        Ingredient.CaloriesPer100 = a.Food.CaloriesPer100;
        Ingredient.NutrientsPer100 = new Dictionary<string, double>(a.Food.NutrientsPer100);
    }

    private string? CaloriesValue() =>
        Ingredient.Calories?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private void SetCalories(string? raw)
    {
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            Ingredient.CaloriesPer100 = null;
            return;
        }
        if (Ingredient.AmountInBase is { } b) Ingredient.CaloriesPer100 = v * 100.0 / b;
    }

    private string? NutrientValue(string key) =>
        Ingredient.Nutrient(key)?.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private void SetNutrient(string key, string? raw)
    {
        if (!double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            Ingredient.NutrientsPer100.Remove(key);
            return;
        }
        if (Ingredient.AmountInBase is { } b) Ingredient.NutrientsPer100[key] = v * 100.0 / b;
    }
}
