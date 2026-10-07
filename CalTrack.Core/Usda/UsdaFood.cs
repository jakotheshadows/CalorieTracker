using System.Globalization;
using CalorieTracker.Models;

namespace CalorieTracker.Usda;

/// <summary>A household measure USDA publishes for a food, e.g. "1 sandwich" = 59 g.</summary>
public sealed record UsdaPortion(string Description, double Grams);

/// <summary>
/// A food from USDA FoodData Central. Nutrition is kept per 100 of the base unit (g or ml)
/// so it can be rescaled to any serving. Shared by the app's USDA search and the MCP
/// server, so an item added either way comes out identical.
/// </summary>
public class UsdaFood
{
    public int FdcId { get; init; }
    public string Description { get; init; } = "";
    public string? Brand { get; init; }
    public string DataType { get; init; } = "";

    /// <summary>"g" for solid foods, "ml" for liquids (as reported by the label).</summary>
    public string BaseUnit { get; init; } = "g";

    /// <summary>Label serving in <see cref="BaseUnit"/> (branded foods only).</summary>
    public double? LabelServingAmount { get; init; }

    /// <summary>Label household text, e.g. "0.5 cup" (branded foods only).</summary>
    public string? LabelServingText { get; init; }

    /// <summary>Package barcode (branded foods only), as reported by FDC.</summary>
    public string? GtinUpc { get; init; }

    public double? CaloriesPer100 { get; init; }
    public Dictionary<string, double> NutrientsPer100 { get; init; } = new();

    /// <summary>Household measures (only from the per-food endpoint; search results omit them).</summary>
    public List<UsdaPortion> Portions { get; init; } = new();

    /// <summary>Amount used when a result is first applied: the label serving, else 100.</summary>
    public double DefaultAmount => LabelServingAmount ?? 100;

    public string DefaultServingDisplay
    {
        get
        {
            if (LabelServingAmount is null) return $"100 {BaseUnit}";
            var metric = $"{LabelServingAmount:0.#} {BaseUnit}";
            return string.IsNullOrWhiteSpace(LabelServingText) ? metric : $"{LabelServingText!.Trim()} ({metric})";
        }
    }

    public double? CaloriesFor(double amountInBase) =>
        CaloriesPer100 is null ? null : Math.Round(CaloriesPer100.Value * amountInBase / 100.0);

    public Dictionary<string, double> NutrientsFor(double amountInBase) =>
        NutrientsPer100.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value * amountInBase / 100.0, 1));

    /// <summary>
    /// Serving label for an amount the user chose: the unmodified label serving keeps its
    /// household text ("0.5 cup (113 g)"); anything else shows amount and unit.
    /// </summary>
    public string ServingText(double amount, string unit, double amountInBase)
    {
        if (LabelServingAmount is not null && unit == BaseUnit && Math.Abs(amount - LabelServingAmount.Value) < 0.001)
            return DefaultServingDisplay;
        return unit == BaseUnit
            ? $"{amount.ToString("0.##", CultureInfo.InvariantCulture)} {unit}"
            : $"{amount.ToString("0.##", CultureInfo.InvariantCulture)} {unit} ({amountInBase.ToString("0.#", CultureInfo.InvariantCulture)} {BaseUnit})";
    }

    /// <summary>A menu item for this food at one serving — what the app's item editor fills in.</summary>
    public FoodItem ToMenuItem(string name, double amountInBase, string servingText) => new()
    {
        Name = name,
        ServingSize = servingText,
        Calories = CaloriesFor(amountInBase),
        Nutrients = NutrientsFor(amountInBase),
    };
}
