using System.Text.Json;

namespace CalorieTracker.Sync;

/// <summary>
/// A question for the CalTrack app, asked through the data folder. A browser app can't
/// accept connections, so this is its API: the MCP server drops a request file in
/// requests/, the app (when open) answers it in responses/. Used for what only the app
/// holds — the user's USDA key, which never leaves the browser.
/// </summary>
/// <remarks>
/// The app answers from a Web Worker (wwwroot/js/request-worker.js), not from .NET: browsers
/// throttle a background page's timers to about one wake-up a minute, but not a worker's.
/// So the app is deliberately thin — a keyed fetch of a USDA path — and every decision
/// (building the path, reading the reply, wording errors) stays in C#
/// (<see cref="CalorieTracker.Usda.UsdaClient"/>), shared with the app's own lookups.
/// </remarks>
public class AppRequest
{
    /// <summary>
    /// Fetch <see cref="Path"/> from the USDA FoodData Central API with the app's key. The app
    /// only fetches the shapes <see cref="CalorieTracker.Usda.UsdaClient"/> builds (a search, or
    /// one food), so a stray file in the folder can't send the key anywhere else.
    /// </summary>
    public const string UsdaGet = "usda_get";

    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Kind { get; set; } = "";

    /// <summary>How long the asker waits; the app skips requests older than this (nobody's listening).</summary>
    public int TimeoutSeconds { get; set; } = 20;

    /// <summary><see cref="UsdaGet"/>: the path under the API root, without the key (e.g. "food/172226").</summary>
    public string? Path { get; set; }
}

/// <summary>The app's answer to an <see cref="AppRequest"/> with the same <see cref="Id"/>.</summary>
public class AppResponse
{
    public string Id { get; set; } = "";

    /// <summary>The app couldn't make the request (e.g. no USDA key saved); user-facing.</summary>
    public string? Error { get; set; }

    /// <summary>USDA's HTTP status, or 0 when USDA couldn't be reached.</summary>
    public int Status { get; set; }

    /// <summary>USDA's reply, verbatim.</summary>
    public string? Body { get; set; }
}

public static class AppRequests
{
    public const string RequestsDir = "requests";
    public const string ResponsesDir = "responses";

    /// <summary>Request kinds this build answers. The app advertises it in <see cref="CalorieTracker.Models.AppData.RequestKinds"/>.</summary>
    public static readonly IReadOnlyList<string> SupportedKinds = new[] { AppRequest.UsdaGet };

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
