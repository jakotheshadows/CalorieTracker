using CalorieTracker.Models;
using CalorieTracker.Usda;

namespace CalorieTracker.Services;

/// <summary>A USDA result applied at a chosen serving, as reported by the UsdaPanel component.</summary>
public record UsdaApplied(UsdaFood Food, double Amount, string Unit, double AmountInBase, string ServingText);

/// <summary>
/// USDA FoodData Central lookup for the app. The API calls and parsing live in
/// CalTrack.Core (<see cref="UsdaClient"/>), shared with the MCP server; this service owns
/// the user's api.data.gov key, stored only in this browser's localStorage — never in exports.
/// </summary>
public class UsdaService(LocalStore store, HttpClient http)
{
    public const string ApiKeyStorageKey = "caltrack-usda-api-key";
    private const string KeyHint = "Double-check it in Settings.";

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
        var (results, error) = await UsdaClient.SearchAsync(http, key, "apple", 1, KeyHint);
        return error is not null
            ? (false, error)
            : (true, $"Key works — the USDA database is reachable ({results!.Count} sample result).");
    }

    // ---------- Search ----------

    public async Task<(List<UsdaFood>? Results, string? Error)> SearchAsync(string query)
    {
        var key = await GetApiKeyAsync();
        if (key is null) return (null, "No API key configured. Add one in Settings.");
        return await UsdaClient.SearchAsync(http, key, query, 12, KeyHint);
    }

    /// <summary>Look up a scanned/typed GTIN/UPC. Matches on FDC's gtinUpc, ignoring leading zeros.</summary>
    public async Task<(UsdaFood? Food, string? Error)> LookupBarcodeAsync(string code)
    {
        var key = await GetApiKeyAsync();
        if (key is null) return (null, "No API key configured. Add one in Settings.");
        return await UsdaClient.LookupBarcodeAsync(http, key, code, KeyHint);
    }

    public static string? ExpandUpcE(string code) => UsdaClient.ExpandUpcE(code);
}
