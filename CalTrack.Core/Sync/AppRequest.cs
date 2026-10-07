using System.Text.Json;
using CalorieTracker.Usda;

namespace CalorieTracker.Sync;

/// <summary>
/// A question for the CalTrack app, asked through the data folder. A browser app can't
/// accept connections, so this is its API: the MCP server drops a request file in
/// requests/, the app (when open) answers it in responses/. Used for anything that needs
/// what only the app holds — chiefly the user's USDA key, which never leaves the browser.
/// </summary>
public class AppRequest
{
    public const string UsdaSearch = "usda_search";
    public const string UsdaFood = "usda_food";

    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Kind { get; set; } = "";

    /// <summary>How long the asker waits; the app skips requests older than this (nobody's listening).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary><see cref="UsdaSearch"/>: what to look for, and how many results.</summary>
    public string? Query { get; set; }
    public int Max { get; set; } = 8;

    /// <summary><see cref="UsdaFood"/>: the FoodData Central id.</summary>
    public int FdcId { get; set; }
}

/// <summary>The app's answer to an <see cref="AppRequest"/> with the same <see cref="Id"/>.</summary>
public class AppResponse
{
    public string Id { get; set; } = "";

    /// <summary>User-facing failure (e.g. no USDA key in the app); null on success.</summary>
    public string? Error { get; set; }

    public List<UsdaFood> Foods { get; set; } = new();
}

public static class AppRequests
{
    public const string RequestsDir = "requests";
    public const string ResponsesDir = "responses";

    /// <summary>Request kinds this build answers. The app advertises it in <see cref="CalorieTracker.Models.AppData.RequestKinds"/>.</summary>
    public static readonly IReadOnlyList<string> SupportedKinds = new[] { AppRequest.UsdaSearch, AppRequest.UsdaFood };

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FileNameFor(string id) => $"{id}.json";

    public static string Serialize(AppRequest r) => JsonSerializer.Serialize(r, Json);
    public static string Serialize(AppResponse r) => JsonSerializer.Serialize(r, Json);

    public static AppRequest? TryParseRequest(string text) => TryParse<AppRequest>(text, r => r.Id, r => r.Kind);
    public static AppResponse? TryParseResponse(string text) => TryParse<AppResponse>(text, r => r.Id, _ => "-");

    /// <summary>Past its asker's timeout: answering it would only leave an orphaned response.</summary>
    public static bool IsExpired(AppRequest r, DateTime utcNow) =>
        utcNow - r.CreatedUtc > TimeSpan.FromSeconds(Math.Max(1, r.TimeoutSeconds));

    private static T? TryParse<T>(string text, Func<T, string> id, Func<T, string> kind) where T : class
    {
        try
        {
            var r = JsonSerializer.Deserialize<T>(text, Json);
            return r is null || string.IsNullOrWhiteSpace(id(r)) || string.IsNullOrWhiteSpace(kind(r)) ? null : r;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
