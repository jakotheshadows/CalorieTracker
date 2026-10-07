using System.Collections.Concurrent;
using CalorieTracker.Sync;
using CalorieTracker.Usda;
using ModelContextProtocol;

namespace CalTrack.Mcp;

/// <summary>
/// USDA lookups for the MCP tools, fetched BY THE CALTRACK APP through the data folder. The
/// app holds the user's USDA key (in the browser, never exported), so the server needs no
/// key and no USDA access of its own: it drops a request for a USDA path in requests/, the
/// app adds the key and fetches it, and the server reads USDA's reply from responses/ with
/// the same Core code the app uses. The cost: these lookups need CalTrack open; everything
/// else the server does works without it.
/// </summary>
public sealed class UsdaGateway(DataFolderStore store, TimeSpan? timeout = null)
{
    private const string KeyHint = "Check the USDA key in CalTrack's Settings.";

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);

    // Search hits, so add_menu_item still has the numbers if the full lookup fails.
    private readonly ConcurrentDictionary<int, UsdaFood> _seen = new();

    public async Task<List<UsdaFood>> SearchAsync(string query, int max)
    {
        var reply = await FetchAsync(UsdaClient.SearchPath(query, max));
        var (foods, error) = UsdaClient.ReadSearch(reply.Status, reply.Body, KeyHint);
        if (foods is null) throw new McpException(error ?? "USDA search failed.");
        foreach (var f in foods.Where(f => f.FdcId > 0)) _seen[f.FdcId] = f;
        return foods;
    }

    /// <summary>
    /// The full record (with household portions). If that lookup fails but the food came
    /// from a search this session, the search hit is used and a warning says what's missing.
    /// </summary>
    public async Task<(UsdaFood Food, string? Warning)> GetAsync(int fdcId)
    {
        try
        {
            var reply = await FetchAsync(UsdaClient.FoodPath(fdcId));
            var (food, error) = UsdaClient.ReadFood(reply.Status, reply.Body, fdcId, KeyHint);
            if (food is null) throw new McpException(error ?? $"USDA has no food with id {fdcId}.");
            _seen[fdcId] = food;
            return (food, null);
        }
        catch (McpException ex) when (_seen.TryGetValue(fdcId, out var hit))
        {
            return (hit, $"USDA's full record couldn't be fetched ({ex.Message}), so the search result was used — it has no household portions.");
        }
    }

    private async Task<AppResponse> FetchAsync(string path)
    {
        // Only ask an app that has said it can answer: a question it doesn't understand
        // would just sit there until the timeout.
        var snap = store.Read();
        if (!snap.MainExists || !snap.View.RequestKinds.Contains(AppRequest.UsdaGet))
            throw new McpException(
                "USDA lookups go through the CalTrack app (it holds the user's USDA key), and the version that last " +
                "saved the data folder can't answer this server's requests. Ask the user to open CalTrack and update it " +
                "(the \"Update now\" banner, or Settings → Check for updates), then try again.");

        store.SweepOrphanResponses(TimeSpan.FromMinutes(10));
        var request = new AppRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = DateTime.UtcNow,
            Kind = AppRequest.UsdaGet,
            TimeoutSeconds = (int)Math.Ceiling(_timeout.TotalSeconds),
            Path = path,
        };
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
            "app with the user's USDA key, so it has to be open in the browser (in the background is fine) with its " +
            "data folder connected — ask the user to open CalTrack, click \"Reconnect folder\" if it shows that " +
            "banner, and try again.");
    }
}
