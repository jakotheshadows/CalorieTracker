using CalorieTracker.Models;
using CalorieTracker.Services;
using Microsoft.AspNetCore.Components;

namespace CalorieTracker.Components;

public partial class ItemForm
{
    [Parameter] public FoodItem Item { get; set; } = new();
    [Parameter] public string? OriginalName { get; set; }
    [Parameter] public string? Title { get; set; }
    [Parameter] public string SaveLabel { get; set; } = "Save";
    [Parameter] public string? Error { get; set; }
    [Parameter] public RenderFragment? ExtraFields { get; set; }
    [Parameter] public EventCallback OnSave { get; set; }
    [Parameter] public EventCallback OnCancel { get; set; }

    private void OnUsdaApplied(UsdaApplied a)
    {
        Item.ServingSize = a.ServingText;
        Item.Calories = a.Food.CaloriesFor(a.AmountInBase);
        Item.Nutrients = a.Food.NutrientsFor(a.AmountInBase);
    }

    private string? NutrientValue(string key) =>
        Item.Nutrient(key)?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void SetNutrient(string key, string? raw)
    {
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v))
            Item.Nutrients[key] = v;
        else
            Item.Nutrients.Remove(key);
    }
}
