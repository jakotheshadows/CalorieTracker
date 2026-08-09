using System.Text.Json;
using CalorieTracker.Models;

namespace CalorieTracker.Services;

/// <summary>
/// A search hit from USDA FoodData Central. Nutrition is kept per 100 of the base unit
/// (g or ml) so it can be rescaled to any user-chosen serving.
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

    public double? CaloriesPer100 { get; init; }
    public Dictionary<string, double> NutrientsPer100 { get; init; } = new();

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
}

/// <summary>A USDA result applied at a chosen serving, as reported by the UsdaPanel component.</summary>
public record UsdaApplied(UsdaFood Food, double Amount, string Unit, double AmountInBase, string ServingText);

/// <summary>
/// USDA FoodData Central lookup (https://fdc.nal.usda.gov). Requires the user's own free
/// api.data.gov key, stored only in this browser's localStorage — never in exports.
/// </summary>
public class UsdaService(LocalStore store, HttpClient http)
{
    public const string ApiKeyStorageKey = "caltrack-usda-api-key";

    // FDC nutrient numbers → CalTrack nutrient keys (units already match the catalog).
    private static readonly Dictionary<string, string> NutrientMap = new()
    {
        ["203"] = "protein",
        ["204"] = "fat",
        ["205"] = "carbs",
        ["291"] = "fiber",
        ["269"] = "sugar",
        ["307"] = "sodium",
        ["306"] = "potassium",
        ["301"] = "calcium",
        ["303"] = "iron",
        ["601"] = "cholesterol",
        ["320"] = "vitaminA",
        ["401"] = "vitaminC",
        ["328"] = "vitaminD",
    };

    // ---------- Unit conversion (shared tables live in Models.Units) ----------

    public static string[] UnitsForBase(string baseUnit) => Units.ForBase(baseUnit);

    /// <summary>Convert a user amount+unit to the food's base unit (g or ml). Null if invalid.</summary>
    public static double? ToBaseAmount(double amount, string unit, string baseUnit) =>
        Units.BaseUnitFor(unit) == baseUnit ? Units.ToBase(amount, unit) : null;

    // ---------- API key ----------

    public async Task<string?> GetApiKeyAsync()
    {
        var key = await store.GetAsync(ApiKeyStorageKey);
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    public async Task SetApiKeyAsync(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) await store.RemoveAsync(ApiKeyStorageKey);
        else await store.SetAsync(ApiKeyStorageKey, key.Trim());
    }

    public async Task<(bool Ok, string Message)> TestKeyAsync(string key)
    {
        var (results, error) = await SearchWithKeyAsync(key, "apple", 1);
        return error is not null
            ? (false, error)
            : (true, $"Key works — the USDA database is reachable ({results!.Count} sample result).");
    }

    // ---------- Search ----------

    public async Task<(List<UsdaFood>? Results, string? Error)> SearchAsync(string query)
    {
        var key = await GetApiKeyAsync();
        if (key is null) return (null, "No API key configured. Add one in Settings.");
        return await SearchWithKeyAsync(key, query, 12);
    }

    private async Task<(List<UsdaFood>? Results, string? Error)> SearchWithKeyAsync(string key, string query, int pageSize)
    {
        if (string.IsNullOrWhiteSpace(query)) return (new List<UsdaFood>(), null);
        try
        {
            var url = "https://api.nal.usda.gov/fdc/v1/foods/search" +
                      $"?api_key={Uri.EscapeDataString(key)}" +
                      $"&query={Uri.EscapeDataString(query.Trim())}" +
                      $"&pageSize={pageSize}" +
                      "&dataType=" + Uri.EscapeDataString("Branded,Foundation,SR Legacy");
            using var resp = await http.GetAsync(url);
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return (null, "The USDA API rejected the key (HTTP 403). Double-check it in Settings.");
            if ((int)resp.StatusCode == 429)
                return (null, "Rate limit reached for this key — try again in a bit.");
            if (!resp.IsSuccessStatusCode)
                return (null, $"USDA API error (HTTP {(int)resp.StatusCode}).");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var results = new List<UsdaFood>();
            if (doc.RootElement.TryGetProperty("foods", out var foods))
                foreach (var food in foods.EnumerateArray())
                    results.Add(ParseFood(food));
            return (results, null);
        }
        catch (Exception)
        {
            return (null, "Couldn't reach the USDA API — are you offline?");
        }
    }

    private static UsdaFood ParseFood(JsonElement food)
    {
        // Search results report nutrients per 100 g (or 100 ml for liquids); branded items
        // additionally carry their label serving.
        var baseUnit = "g";
        double? labelAmount = null;
        string? labelText = null;
        if (food.TryGetProperty("servingSize", out var ss) && ss.TryGetDouble(out var size) && size > 0 &&
            food.TryGetProperty("servingSizeUnit", out var ssu))
        {
            var unit = (ssu.GetString() ?? "").ToLowerInvariant();
            if (unit is "g" or "grm" or "ml" or "mlt")
            {
                baseUnit = unit.StartsWith('m') ? "ml" : "g";
                labelAmount = size;
                labelText = food.TryGetProperty("householdServingFullText", out var h) ? h.GetString() : null;
            }
        }

        double? caloriesPer100 = null;
        var nutrientsPer100 = new Dictionary<string, double>();
        if (food.TryGetProperty("foodNutrients", out var list))
        {
            foreach (var n in list.EnumerateArray())
            {
                if (!n.TryGetProperty("value", out var v) || !v.TryGetDouble(out var value)) continue;
                var number = n.TryGetProperty("nutrientNumber", out var num) ? num.GetString() ?? "" : "";
                // Energy: prefer 208 (kcal); Foundation foods may only have Atwater energy (957/958).
                if (number == "208" || ((number == "957" || number == "958") && caloriesPer100 is null))
                    caloriesPer100 = value;
                else if (NutrientMap.TryGetValue(number, out var nutrientKey) && value > 0)
                    nutrientsPer100[nutrientKey] = value;
            }
        }

        return new UsdaFood
        {
            FdcId = food.TryGetProperty("fdcId", out var id) && id.TryGetInt32(out var fdcId) ? fdcId : 0,
            Description = food.TryGetProperty("description", out var d) ? ToTitleCase(d.GetString() ?? "") : "",
            Brand = food.TryGetProperty("brandOwner", out var b) ? ToTitleCase(b.GetString() ?? "") :
                    food.TryGetProperty("brandName", out var bn) ? ToTitleCase(bn.GetString() ?? "") : null,
            DataType = food.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "" : "",
            BaseUnit = baseUnit,
            LabelServingAmount = labelAmount,
            LabelServingText = labelText,
            CaloriesPer100 = caloriesPer100,
            NutrientsPer100 = nutrientsPer100,
        };
    }

    private static string ToTitleCase(string s) =>
        s.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant())
            : s;
}
