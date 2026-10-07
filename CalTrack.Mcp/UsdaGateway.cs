using System.Collections.Concurrent;
using CalorieTracker.Sync;
using CalorieTracker.Usda;
using ModelContextProtocol;

namespace CalTrack.Mcp;

/// <summary>
/// USDA lookups for the MCP tools, asked OF THE CALTRACK APP through the data folder. The
/// app holds the user's USDA key (in the browser, never exported) and already talks to
/// USDA, so the server needs no key and no USDA access of its own: it drops a request in
/// requests/ and waits for the app's answer in responses/. The cost: these lookups need
/// CalTrack open; everything else the server does works without it.
/// </summary>
public sealed class UsdaGateway(DataFolderStore store, TimeSpan? timeout = null)
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);

    // Search hits, so add_menu_item still has the numbers if the full lookup fails.
    private readonly ConcurrentDictionary<int, UsdaFood> _seen = new();

    public async Task<List<UsdaFood>> SearchAsync(string query, int max)
    {
        var response = await AskAsync(new AppRequest { Kind = AppRequest.UsdaSearch, Query = query, Max = max });
        foreach (var f in response.Foods.Where(f => f.FdcId > 0)) _seen[f.FdcId] = f;
        return response.Foods;
    }

    /// <summary>
    /// The full record (with household portions). If that lookup fails but the food came
    /// from a search this session, the search hit is used and a warning says what's missing.
    /// </summary>
    public async Task<(UsdaFood Food, string? Warning)> GetAsync(int fdcId)
    {
        try
        {
            var response = await AskAsync(new AppRequest { Kind = AppRequest.UsdaFood, FdcId = fdcId });
            if (response.Foods.FirstOrDefault() is { } food)
            {
                _seen[fdcId] = food;
                return (food, null);
            }
            throw new McpException($"CalTrack found no USDA food with id {fdcId}.");
        }
        catch (McpException ex) when (_seen.TryGetValue(fdcId, out var hit))
        {
            return (hit, $"USDA's full record couldn't be fetched ({ex.Message}), so the search result was used — it has no household portions.");
        }
    }

    private async Task<AppResponse> AskAsync(AppRequest request)
    {
        // Only ask an app that has said it can answer: a question it doesn't understand
        // would just sit there until the timeout.
        var snap = store.Read();
        if (!snap.MainExists || !snap.View.RequestKinds.Contains(request.Kind))
            throw new McpException(
                "USDA lookups go through the CalTrack app (it holds the user's USDA key), and the version that last " +
                "saved the data folder can't answer them yet. Ask the user to open CalTrack and update it (the " +
                "\"Update now\" banner, or Settings → Check for updates), then try again.");

        store.SweepOrphanResponses(TimeSpan.FromMinutes(10));
        request.Id = Guid.NewGuid().ToString("N");
        request.CreatedUtc = DateTime.UtcNow;
        request.TimeoutSeconds = (int)Math.Ceiling(_timeout.TotalSeconds);
        store.WriteRequest(request);

        var deadline = DateTime.UtcNow + _timeout;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            if (store.TryTakeResponse(request.Id) is { } response)
                return response.Error is null ? response : throw new McpException("CalTrack: " + response.Error);
        }

        store.WithdrawRequest(request.Id);
        throw new McpException(
            $"CalTrack didn't answer within {_timeout.TotalSeconds:0} seconds. USDA lookups are made by the CalTrack " +
            "app with the user's USDA key, so it has to be open in the browser — ask the user to open CalTrack (or " +
            "bring it to the front if it's minimized) and try again.");
    }
}
