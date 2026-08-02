using System.Text.Json;
using CalorieTracker.Models;

namespace CalorieTracker.Services;

/// <summary>A search hit from USDA FoodData Central, scaled to one serving.</summary>
public class UsdaFood
{
    public int FdcId { get; init; }
    public string Description { get; init; } = "";
    public string? Brand { get; init; }
    public string DataType { get; init; } = "";
    public string ServingText { get; init; } = "100 g";
    public double? Calories { get; init; }
    public Dictionary<string, double> Nutrients { get; init; } = new();
}

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
        // Search results report nutrients per 100 g; branded items also carry their label
        // serving size, which we scale to.
        var factor = 1.0;
        var servingText = "100 g";
        if (food.TryGetProperty("servingSize", out var ss) && ss.TryGetDouble(out var size) && size > 0 &&
            food.TryGetProperty("servingSizeUnit", out var ssu))
        {
            var unit = (ssu.GetString() ?? "").ToLowerInvariant();
            if (unit is "g" or "grm" or "ml" or "mlt")
            {
                factor = size / 100.0;
                var household = food.TryGetProperty("householdServingFullText", out var h) ? h.GetString() : null;
                var metric = $"{size:0.#} {(unit.StartsWith('m') ? "ml" : "g")}";
                servingText = string.IsNullOrWhiteSpace(household) ? metric : $"{household!.Trim()} ({metric})";
            }
        }

        double? calories = null;
        var nutrients = new Dictionary<string, double>();
        if (food.TryGetProperty("foodNutrients", out var list))
        {
            foreach (var n in list.EnumerateArray())
            {
                if (!n.TryGetProperty("value", out var v) || !v.TryGetDouble(out var value)) continue;
                var number = n.TryGetProperty("nutrientNumber", out var num) ? num.GetString() ?? "" : "";
                // Energy: prefer 208 (kcal); Foundation foods may only have Atwater energy (957/958).
                if (number == "208" || ((number == "957" || number == "958") && calories is null))
                    calories = value * factor;
                else if (NutrientMap.TryGetValue(number, out var nutrientKey) && value > 0)
                    nutrients[nutrientKey] = Math.Round(value * factor, 1);
            }
        }

        return new UsdaFood
        {
            FdcId = food.TryGetProperty("fdcId", out var id) && id.TryGetInt32(out var fdcId) ? fdcId : 0,
            Description = food.TryGetProperty("description", out var d) ? ToTitleCase(d.GetString() ?? "") : "",
            Brand = food.TryGetProperty("brandOwner", out var b) ? ToTitleCase(b.GetString() ?? "") :
                    food.TryGetProperty("brandName", out var bn) ? ToTitleCase(bn.GetString() ?? "") : null,
            DataType = food.TryGetProperty("dataType", out var dt) ? dt.GetString() ?? "" : "",
            ServingText = servingText,
            Calories = calories is null ? null : Math.Round(calories.Value, 0),
            Nutrients = nutrients,
        };
    }

    private static string ToTitleCase(string s) =>
        s.All(c => !char.IsLetter(c) || char.IsUpper(c))
            ? System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant())
            : s;
}
