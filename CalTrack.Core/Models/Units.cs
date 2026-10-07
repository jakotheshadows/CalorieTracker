namespace CalorieTracker.Models;

/// <summary>Mass/volume unit conversion shared by USDA lookup and recipe ingredients.</summary>
public static class Units
{
    public static readonly string[] Mass = { "g", "kg", "oz", "lb" };
    public static readonly string[] Volume = { "ml", "l", "fl oz" };

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

    /// <summary>"g" for mass units, "ml" for volume units.</summary>
    public static string BaseUnitFor(string unit) => IsVolume(unit) ? "ml" : "g";

    public static string[] ForBase(string baseUnit) => baseUnit == "ml" ? Volume : Mass;

    /// <summary>Convert an amount+unit to its base unit (g or ml). Null if invalid.</summary>
    public static double? ToBase(double amount, string unit)
    {
        if (amount <= 0) return null;
        var table = IsVolume(unit) ? VolumeToMl : MassToGrams;
        return table.TryGetValue(unit, out var factor) ? amount * factor : null;
    }
}
