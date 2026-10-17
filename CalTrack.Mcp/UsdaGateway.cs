using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
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
/// <param name="timeout">How long one lookup waits for answers in all, retries included.</param>
/// <param name="retryDelays">Waits before retrying a transient USDA failure; <see cref="UsdaClient.RetryDelays"/> by default.</param>
/// <param name="trace">When the protocol trace is on, each attempt is recorded there.</param>
public sealed class UsdaGateway(
    DataFolderStore store, TimeSpan? timeout = null, IReadOnlyList<TimeSpan>? retryDelays = null, ProtocolTrace? trace = null)
{
    private const string KeyHint = "Check the USDA key in CalTrack's Settings.";

    private static readonly JsonSerializerOptions TraceJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // A retry is only started with time left for the app to answer it: its worker alone polls every half second.
    private static readonly TimeSpan MinTimeToAnswer = TimeSpan.FromSeconds(1);

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(20);
    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? UsdaClient.RetryDelays;

    // Search hits: add_menu_item still has the numbers if the full lookup fails, and a full
    // record that lost its label serving gets it back from here.
    private readonly ConcurrentDictionary<int, UsdaFood> _seen = new();

    // Full records already fetched this session (search_usda fetches some for their
    // portions; adding the same food then costs no second lookup).
    private readonly ConcurrentDictionary<int, UsdaFood> _full = new();

    public async Task<List<UsdaFood>> SearchAsync(string query, int max, string dataTypes = UsdaClient.AllTypes)
    {
        var reply = await FetchAsync(UsdaClient.SearchPath(query, max, dataTypes));
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
        if (_full.TryGetValue(fdcId, out var cached)) return (cached, null);
        try
        {
            var reply = await FetchAsync(UsdaClient.FoodPath(fdcId));
            var (food, error) = UsdaClient.ReadFood(reply.Status, reply.Body, fdcId, KeyHint);
            if (food is null) throw new McpException(error ?? $"USDA has no food with id {fdcId}.");
            food = food.WithLabelServingFrom(_seen.GetValueOrDefault(fdcId));
            _full[fdcId] = food;
            return (food, null);
        }
        catch (McpException ex) when (_seen.TryGetValue(fdcId, out var hit))
        {
            return (hit, $"USDA's full record couldn't be fetched ({ex.Message}), so the search result was used" +
                         (hit.Portions.Count > 0 ? "." : " — it has no household portions."));
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

        // Well past any asker's timeout, so another server's answer in flight is never swept.
        store.SweepOrphanResponses(TimeSpan.FromMinutes(2));

        // USDA fails some requests by chance (UsdaClient.IsTransient), so ask again, within the
        // same overall wait. If time runs out on a retry, USDA's last answer is the truer report.
        var deadline = DateTime.UtcNow + _timeout;
        AppResponse? answered = null;
        for (var attempt = 1; ; attempt++)
        {
            var (id, response, waited) = await AskAsync(path, deadline);
            if (response is null)
            {
                Record(id, path, attempt, "no answer", null, waited);
                return answered ?? throw new McpException(
                    $"CalTrack didn't answer within {_timeout.TotalSeconds:0} seconds. USDA lookups are made by the CalTrack " +
                    "app with the user's USDA key, so it has to be open in the browser (in the background is fine) with its " +
                    "data folder connected — ask the user to open CalTrack, click \"Reconnect folder\" if it shows that " +
                    "banner, and try again.");
            }
            if (response.Error is not null)
            {
                Record(id, path, attempt, "app error", response, waited);
                throw new McpException("CalTrack: " + response.Error);
            }

            answered = response;
            var delay = attempt <= _retryDelays.Count && UsdaClient.IsTransient(response.Status, response.Body)
                ? _retryDelays[attempt - 1]
                : (TimeSpan?)null;
            var retry = delay is { } d && DateTime.UtcNow + d + MinTimeToAnswer < deadline;
            Record(id, path, attempt, response.Status is >= 200 and <= 299 ? "ok" : retry ? "retry" : "failed", response, waited);
            if (!retry) return response;
            await Task.Delay(delay!.Value);
        }
    }

    /// <summary>One request to the app: its answer, or null once <paramref name="deadline"/> passes (the request is withdrawn).</summary>
    private async Task<(string Id, AppResponse? Response, TimeSpan Waited)> AskAsync(string path, DateTime deadline)
    {
        var started = DateTime.UtcNow;
        var request = new AppRequest
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = started,
            Kind = AppRequest.UsdaGet,
            // The app skips a request whose asker has stopped waiting.
            TimeoutSeconds = Math.Max(1, (int)Math.Ceiling((deadline - started).TotalSeconds)),
            Path = path,
        };
        store.WriteRequest(request);

        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(150);
            if (store.TryTakeResponse(request.Id) is { } response)
                return (request.Id, response, DateTime.UtcNow - started);
        }

        store.WithdrawRequest(request.Id);
        return (request.Id, null, DateTime.UtcNow - started);
    }

    /// <summary>
    /// One attempt, as a line of the protocol trace (when it's on): the request's id (its
    /// responses/ file), the USDA path (never the key: the server doesn't have it), how it
    /// ended, USDA's status and reason, the reply's length, the server's wait (queue and fetch)
    /// and the app's own fetch time.
    /// </summary>
    private void Record(string id, string path, int attempt, string outcome, AppResponse? response, TimeSpan waited)
    {
        if (trace is null) return;
        var failed = response is { Error: null } && response.Status is < 200 or > 299;
        trace.RecordUsda(JsonSerializer.Serialize(new
        {
            id,
            path,
            attempt,
            outcome,
            status = response is { Error: null } ? response.Status : (int?)null,
            reason = failed ? UsdaClient.ErrorReason(response!.Body) : response?.Error,
            length = response?.Body?.Length,
            waitMs = (int)waited.TotalMilliseconds,
            fetchMs = response?.FetchMs,
        }, TraceJson));
    }
}
