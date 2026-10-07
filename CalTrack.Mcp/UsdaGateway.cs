using System.Collections.Concurrent;
using CalorieTracker.Usda;
using ModelContextProtocol;

namespace CalTrack.Mcp;

/// <summary>
/// USDA FoodData Central for the MCP tools, over the same client and parser the app uses.
/// The key comes from the server config (USDA_API_KEY / --usda-key); without one it falls
/// back to api.data.gov's shared DEMO_KEY, which works but allows only ~10 requests an hour.
/// </summary>
public sealed class UsdaGateway(HttpClient http, string? apiKey)
{
    public const string DemoKey = "DEMO_KEY";
    private const string KeyHint = "Check USDA_API_KEY in the CalTrack MCP server's config.";

    public string Key { get; } = string.IsNullOrWhiteSpace(apiKey) ? DemoKey : apiKey.Trim();
    public bool UsingDemoKey => Key == DemoKey;

    // Search hits, so add_menu_item still has the numbers if the full lookup fails.
    private readonly ConcurrentDictionary<int, UsdaFood> _seen = new();

    public async Task<List<UsdaFood>> SearchAsync(string query, int max)
    {
        var (results, error) = await UsdaClient.SearchAsync(http, Key, query, max, KeyHint);
        if (results is null) throw new McpException(Explain(error!));
        foreach (var f in results.Where(f => f.FdcId > 0)) _seen[f.FdcId] = f;
        return results;
    }

    /// <summary>
    /// The full record (with household portions). If that lookup fails but the food came
    /// from a search this session, the search hit is used and a warning says what's missing.
    /// </summary>
    public async Task<(UsdaFood Food, string? Warning)> GetAsync(int fdcId)
    {
        var (food, error) = await UsdaClient.GetFoodAsync(http, Key, fdcId, KeyHint);
        if (food is not null)
        {
            _seen[fdcId] = food;
            return (food, null);
        }
        if (_seen.TryGetValue(fdcId, out var hit))
            return (hit, $"USDA's full record couldn't be fetched ({error}), so the search result was used — it has no household portions.");
        throw new McpException(Explain(error!));
    }

    private string Explain(string error) =>
        UsingDemoKey && error.StartsWith("Rate limit", StringComparison.Ordinal)
            ? error + " The server is using api.data.gov's shared DEMO_KEY (about 10 requests an hour). " +
              "A free personal key from https://api.data.gov/signup goes in the server config as USDA_API_KEY."
            : error;
}
