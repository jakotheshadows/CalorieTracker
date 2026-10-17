namespace CalorieTracker.Models;

/// <summary>Mass/volume unit conversion shared by USDA lookup and recipe ingredients.</summary>
public static class Units
{
    public static readonly string[] Mass = { "g", "kg", "oz", "lb" };
    public static readonly string[] Volume = { "ml", "l", "fl oz" };

    /// <summary>
    /// Servings of a menu item used as a recipe ingredient (e.g. 1.5 servings of the user's
    /// chili in a chili dog). Its own base unit: nutrition is stored per 100 servings, so it
    /// scales like any other ingredient but never converts to or from weight or volume.
    /// </summary>
    public const string Serving = "serving";

    private static readonly Dictionary<string, double> MassToGrams = new(StringComparer.OrdinalIgnoreCase)
    {
        ["g"] = 1,
        ["kg"] = 1000,
        ["oz"] = 28.3495,
        ["lb"] = 453.592,
    };

    private static readonly Dictionary<string, double> VolumeToMl = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ml"] = 1,
        ["l"] = 1000,
        ["fl oz"] = 29.5735,
    };

    public static bool IsVolume(string unit) => VolumeToMl.ContainsKey(unit);

    public static bool IsServing(string unit) => string.Equals(unit, Serving, StringComparison.OrdinalIgnoreCase);

    /// <summary>"g" for mass units, "ml" for volume units, "serving" for servings.</summary>
    public static string BaseUnitFor(string unit) => IsVolume(unit) ? "ml" : IsServing(unit) ? Serving : "g";

    public static string[] ForBase(string baseUnit) =>
        baseUnit == "ml" ? Volume : baseUnit == Serving ? new[] { Serving } : Mass;

    /// <summary>Convert an amount+unit to its base unit (g, ml or servings). Null if invalid.</summary>
    public static double? ToBase(double amount, string unit)
    {
        if (amount <= 0) return null;
        if (IsServing(unit)) return amount;
        var table = IsVolume(unit) ? VolumeToMl : MassToGrams;
        return table.TryGetValue(unit, out var factor) ? amount * factor : null;
    }
}
