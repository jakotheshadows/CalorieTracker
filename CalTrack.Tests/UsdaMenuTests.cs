using System.Text.Json;
using System.Text.RegularExpressions;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using CalorieTracker.Usda;
using CalTrack.Mcp;
using ModelContextProtocol;

namespace CalTrack.Tests;

/// <summary>Growing the menu from USDA: parsing, the add_item op, and the MCP tools.</summary>
public sealed class UsdaMenuTests : IDisposable
{
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static readonly DateOnly Day = new(2026, 10, 7);

    private readonly string _dir = Directory.CreateTempSubdirectory("caltrack-usda-test-").FullName;
    private readonly DataFolderStore _store;
    private readonly FakeApp _app;
    private string MainPath => Path.Combine(_dir, Inbox.MainFileName);
    private string InboxDir => Path.Combine(_dir, Inbox.DirName);

    public UsdaMenuTests()
    {
        _store = new DataFolderStore(_dir);
        var data = InboxTests.Menu();
        // An up-to-date app saved this: it applies add_item and answers USDA requests.
        data.InboxKinds = Inbox.SupportedKinds.ToList();
        data.RequestKinds = AppRequests.SupportedKinds.ToList();
        WriteMain(data);
        _app = new FakeApp(_dir);
    }

    public void Dispose()
    {
        _app.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private UsdaGateway Usda(double timeoutSeconds = 5) => new(_store, TimeSpan.FromSeconds(timeoutSeconds));

    private void WriteMain(AppData data) => File.WriteAllText(MainPath, AppDataJson.Serialize(data, indented: true));

    private AppData ReadMain() => AppDataJson.Parse(File.ReadAllText(MainPath));

    private int InboxCount() => Directory.Exists(InboxDir) ? Directory.GetFiles(InboxDir).Length : 0;

    private void AppIngest()
    {
        var data = ReadMain();
        var files = Directory.GetFiles(InboxDir).Select(p => new InboxFile(Path.GetFileName(p), File.ReadAllText(p))).ToList();
        var r = Inbox.Ingest(data, files);
        if (r.DataChanged) WriteMain(data);
        foreach (var name in r.FilesToDelete) File.Delete(Path.Combine(InboxDir, name));
    }

    // ---------- parsing real USDA responses ----------

    [Fact]
    public void Search_results_parse_generic_and_branded_foods()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "usda-search-ice-cream-sandwich.json")));
        var foods = doc.RootElement.GetProperty("foods").EnumerateArray().Select(UsdaClient.ParseSearchFood).ToList();

        var generic = foods.Single(f => f.FdcId == 172226);
        Assert.Equal("Ice cream sandwich", generic.Description);
        Assert.Equal(237, generic.CaloriesPer100);
        Assert.Null(generic.LabelServingAmount); // generic: per 100 g only
        Assert.Empty(generic.Portions);         // search results omit portions

        var branded = foods.Single(f => f.FdcId == 2062888);
        Assert.Equal(31, branded.LabelServingAmount);
        Assert.Equal("1.1 ONZ (31 g)", branded.DefaultServingDisplay);
    }

    [Fact]
    public void Food_detail_parses_nutrients_and_household_portions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "usda-food-172226.json")));
        var food = UsdaClient.ParseDetailFood(doc.RootElement);

        Assert.Equal(237, food.CaloriesPer100);
        Assert.Equal(4.29, food.NutrientsPer100["protein"]);
        var portion = Assert.Single(food.Portions);
        Assert.Equal(new UsdaPortion("1 serving", 70), portion);
    }

    [Fact]
    public void Usda_replies_become_foods_or_plain_errors()
    {
        Assert.Equal("The USDA API rejected the key (HTTP 403). Check it.", UsdaClient.ReadSearch(403, "", "Check it.").Error);
        Assert.Contains("Rate limit", UsdaClient.ReadSearch(429, "").Error);
        Assert.Contains("offline", UsdaClient.ReadSearch(0, null).Error);
        Assert.Equal("USDA API error (HTTP 500).", UsdaClient.ReadSearch(500, "").Error);
        Assert.Contains("couldn't be read", UsdaClient.ReadSearch(200, "<html>").Error);
        Assert.Equal("USDA has no food with id 5.", UsdaClient.ReadFood(404, "", 5).Error);

        var (food, error) = UsdaClient.ReadFood(200, File.ReadAllText(Path.Combine(Fixtures, "usda-food-172226.json")), 172226);
        Assert.Null(error);
        Assert.Equal(new UsdaPortion("1 serving", 70), food!.Portions.Single());
    }

    [Fact]
    public void Lookup_paths_carry_no_key_and_fit_what_the_app_will_fetch()
    {
        var search = UsdaClient.SearchPath("ice cream sandwich", 8);
        var food = UsdaClient.FoodPath(172226);

        Assert.True(WorkerAllows(search), search);
        Assert.True(WorkerAllows(food), food);
        Assert.DoesNotContain("api_key", search);
        Assert.False(WorkerAllows("https://example.com/steal"));
        Assert.False(WorkerAllows("food/1/../../elsewhere"));

        Assert.Equal("https://api.nal.usda.gov/fdc/v1/food/172226?api_key=k%2B1", UsdaClient.Url(food, "k+1"));
        Assert.EndsWith("&api_key=k", UsdaClient.Url(search, "k"));
    }

    // ---------- the add_item op ----------

    [Fact]
    public void AddItem_adds_to_the_menu_and_refuses_duplicates_of_items_and_recipes()
    {
        var data = InboxTests.Menu();
        var (ok, _) = Inbox.Apply(data, AddOp("Ice cream sandwich", 166));
        Assert.NotNull(ok);
        Assert.NotNull(data.FindItem("ice cream sandwich"));

        Assert.Contains("already on the menu", Inbox.Apply(data, AddOp("LARGE EGG", 1)).Error);
        Assert.Contains("already on the menu", Inbox.Apply(data, AddOp("Overnight oats", 1)).Error); // a recipe
    }

    [Fact]
    public void A_food_added_and_logged_in_one_go_lands_in_order()
    {
        var main = InboxTests.Menu();
        var add = AddOp("Ice cream sandwich", 166);
        var log = InboxTests.Food("Ice cream sandwich", 1);
        log.CreatedUtc = add.CreatedUtc.AddMilliseconds(5);
        var files = new[] { InboxTests.File(log), InboxTests.File(add) };

        var view = Inbox.View(main, files, out _);
        Inbox.Ingest(main, files);

        Assert.Equal(166, view.TotalsForDay(Day).Calories);
        Assert.Equal(166, main.TotalsForDay(Day).Calories);
        Assert.Null(Assert.Single(main.GetDay(Day)).AdHoc); // a real menu reference, not a one-off
    }

    [Fact]
    public void An_app_leaves_op_kinds_it_does_not_know_for_a_newer_version()
    {
        var data = InboxTests.Menu();
        var future = InboxTests.Food("Large egg", 1);
        future.Kind = "some_future_kind";

        var r = Inbox.Ingest(data, new[] { InboxTests.File(future) });

        Assert.Single(r.Unsupported);
        Assert.Empty(r.FilesToDelete);       // not consumed
        Assert.Empty(data.ProcessedOpIds);   // and not marked done
    }

    [Fact]
    public void Writers_only_queue_newer_kinds_for_an_app_that_advertises_them()
    {
        var old = new AppData(); // saved by an app version that predates InboxKinds
        Assert.True(Inbox.AppUnderstands(old, InboxOp.LogFood));
        Assert.False(Inbox.AppUnderstands(old, InboxOp.AddItem));
        Assert.True(Inbox.AppUnderstands(new AppData { InboxKinds = Inbox.SupportedKinds.ToList() }, InboxOp.AddItem));
    }

    // ---------- MCP tools ----------

    [Fact]
    public async Task Add_is_refused_while_the_app_is_too_old_to_apply_it()
    {
        WriteMain(InboxTests.Menu()); // no InboxKinds: the deployed app predates add_item

        var ex = await Assert.ThrowsAsync<McpException>(() => CalTrackTools.AddMenuItem(_store, Usda(), usdaFdcId: 172226));

        Assert.Contains("update it", ex.Message);
        Assert.Equal(0, InboxCount());
        Assert.Equal(0, _app.Answered); // refused before asking the app anything
    }

    [Fact]
    public async Task Search_then_add_then_log_end_to_end()
    {
        var usda = Usda();
        var search = JsonDocument.Parse(await CalTrackTools.SearchUsda(usda, "ice cream sandwich"));
        Assert.Contains(search.RootElement.GetProperty("results").EnumerateArray(), r => r.GetProperty("fdcId").GetInt32() == 172226);

        var added = JsonDocument.Parse(await CalTrackTools.AddMenuItem(_store, usda, usdaFdcId: 172226, name: "Ice cream sandwich"))
            .RootElement.GetProperty("added");
        Assert.Equal("1 serving (70 g)", added.GetProperty("serving").GetString()); // USDA's household portion
        Assert.Equal(166, added.GetProperty("calories").GetDouble());              // 237 kcal/100 g × 70 g

        // Loggable immediately, before the app has even seen the add.
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(-4)));
        CalTrackTools.LogFood(_store, clock, "ice cream sandwich", 1);

        AppIngest();

        var main = ReadMain();
        var item = main.FindItem("Ice cream sandwich")!;
        Assert.Contains("fdc.nal.usda.gov/food-details/172226", item.Description);
        Assert.Equal(166, main.TotalsForDay(Day).Calories);
        Assert.All(_app.Paths, path => Assert.True(WorkerAllows(path), path)); // the real worker would have fetched them
    }

    [Fact]
    public async Task An_explicit_serving_rescales_the_usda_numbers()
    {
        var added = JsonDocument.Parse(await CalTrackTools.AddMenuItem(_store, Usda(), usdaFdcId: 172226, name: "Mini ice cream sandwich", servingAmount: 59))
            .RootElement.GetProperty("added");
        Assert.Equal("59 g", added.GetProperty("serving").GetString());
        Assert.Equal(140, added.GetProperty("calories").GetDouble()); // round(237 × 0.59)
    }

    [Fact]
    public async Task Usda_numbers_cannot_be_overridden_and_manual_adds_need_real_calories()
    {
        var usda = Usda();
        Assert.Contains("not both", (await Assert.ThrowsAsync<McpException>(() =>
            CalTrackTools.AddMenuItem(_store, usda, usdaFdcId: 172226, calories: 100))).Message);
        Assert.Contains("calories is required", (await Assert.ThrowsAsync<McpException>(() =>
            CalTrackTools.AddMenuItem(_store, usda, name: "Mystery bar"))).Message);
        Assert.Contains("already on the menu", (await Assert.ThrowsAsync<McpException>(() =>
            CalTrackTools.AddMenuItem(_store, usda, name: "large egg", calories: 70))).Message);
        Assert.Equal(0, InboxCount());
    }

    [Fact]
    public async Task An_unknown_portion_lists_the_real_ones()
    {
        var ex = await Assert.ThrowsAsync<McpException>(() =>
            CalTrackTools.AddMenuItem(_store, Usda(), usdaFdcId: 172226, portion: "1 sandwich"));
        Assert.Contains("\"1 serving\"", ex.Message);
    }

    [Fact]
    public async Task If_the_full_record_fails_the_search_hit_is_used_with_a_warning()
    {
        var usda = Usda();
        await CalTrackTools.SearchUsda(usda, "ice cream sandwich");
        _app.FoodStatus = 500;

        var result = JsonDocument.Parse(await CalTrackTools.AddMenuItem(_store, usda, usdaFdcId: 172226, name: "Ice cream sandwich")).RootElement;

        Assert.Equal("100 g", result.GetProperty("added").GetProperty("serving").GetString());
        Assert.Contains("no household portions", result.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task The_server_holds_no_key_and_relays_the_apps_own_errors()
    {
        _app.Error = "No API key configured. Add one in Settings.";
        var ex = await Assert.ThrowsAsync<McpException>(() => CalTrackTools.SearchUsda(Usda(), "anything"));
        Assert.Equal("CalTrack: No API key configured. Add one in Settings.", ex.Message);
    }

    [Fact]
    public async Task With_CalTrack_closed_lookups_fail_clearly_and_leave_nothing_behind()
    {
        _app.Dispose(); // nobody answering
        var ex = await Assert.ThrowsAsync<McpException>(() => CalTrackTools.SearchUsda(Usda(timeoutSeconds: 1), "ice cream sandwich"));
        Assert.Contains("open CalTrack", ex.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, AppRequests.RequestsDir))); // request withdrawn
    }

    [Theory]
    [InlineData("")]                     // a version without the request channel
    [InlineData("usda_search,usda_food")] // the first channel, answered by .NET (throttled in the background)
    public async Task An_app_that_cannot_answer_is_not_asked(string kinds)
    {
        var data = ReadMain();
        data.RequestKinds = kinds.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        WriteMain(data);

        var ex = await Assert.ThrowsAsync<McpException>(() => CalTrackTools.SearchUsda(Usda(), "ice cream sandwich"));
        Assert.Contains("update it", ex.Message);
        Assert.False(Directory.Exists(Path.Combine(_dir, AppRequests.RequestsDir)));
    }

    [Fact]
    public void Requests_and_responses_round_trip_and_expire()
    {
        var body = File.ReadAllText(Path.Combine(Fixtures, "usda-food-172226.json"));
        var back = AppRequests.TryParseResponse(AppRequests.Serialize(new AppResponse { Id = "x", Status = 200, Body = body }))!;
        Assert.Equal(body, back.Body); // USDA's reply arrives verbatim

        // PascalCase, as request-worker.js writes it.
        var fromWorker = AppRequests.TryParseResponse("""{"Id":"y","Error":null,"Status":404,"Body":""}""")!;
        Assert.Equal("USDA has no food with id 9.", UsdaClient.ReadFood(fromWorker.Status, fromWorker.Body, 9).Error);

        var request = new AppRequest { Id = "r", Kind = AppRequest.UsdaGet, CreatedUtc = DateTime.UtcNow.AddSeconds(-30), TimeoutSeconds = 20 };
        Assert.True(AppRequests.IsExpired(request, DateTime.UtcNow)); // asker gave up: the app skips it
    }

    private static InboxOp AddOp(string name, double calories) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CreatedUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
        Kind = InboxOp.AddItem,
        Item = new FoodItem { Name = name, Calories = calories, ServingSize = "1 serving (70 g)" },
    };

    /// <summary>
    /// The allowlist in the app's request worker, read from wwwroot/js/request-worker.js itself,
    /// so the paths the server asks for and the paths the app will fetch can't drift apart.
    /// </summary>
    private static bool WorkerAllows(string path) => WorkerAllowlist.Value.IsMatch(path);

    private static readonly Lazy<Regex> WorkerAllowlist = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "wwwroot", "js", "request-worker.js"))) dir = dir.Parent;
        var js = File.ReadAllText(Path.Combine(dir!.FullName, "wwwroot", "js", "request-worker.js"));
        var literal = Regex.Match(js, @"const ALLOWED = /(.+)/;").Groups[1].Value;
        return new Regex(literal);
    });

    /// <summary>
    /// Stands in for the CalTrack app's side of the request channel (request-worker.js):
    /// watches requests/, "fetches" from the captured USDA replies, writes responses/ and
    /// removes the request. Error and FoodStatus simulate an app without a key and USDA
    /// failing; Paths records what was asked for.
    /// </summary>
    private sealed class FakeApp : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        public int Answered;
        public string? Error;
        public int? FoodStatus;
        public readonly List<string> Paths = new();

        public FakeApp(string root)
        {
            var requests = Path.Combine(root, AppRequests.RequestsDir);
            var responses = Path.Combine(root, AppRequests.ResponsesDir);
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    if (Directory.Exists(requests))
                        foreach (var path in Directory.GetFiles(requests, "*.json"))
                        {
                            var request = AppRequests.TryParseRequest(File.ReadAllText(path));
                            if (request is not null && !AppRequests.IsExpired(request, DateTime.UtcNow))
                            {
                                Directory.CreateDirectory(responses);
                                File.WriteAllText(Path.Combine(responses, AppRequests.FileNameFor(request.Id)), AppRequests.Serialize(Answer(request)));
                                Interlocked.Increment(ref Answered);
                            }
                            File.Delete(path);
                        }
                    try { await Task.Delay(25, _stop.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        private AppResponse Answer(AppRequest r)
        {
            var path = r.Path ?? "";
            lock (Paths) Paths.Add(path);
            var response = new AppResponse { Id = r.Id, Error = Error };
            if (Error is not null) return response;
            if (path.StartsWith("foods/search?"))
                (response.Status, response.Body) = (200, File.ReadAllText(Path.Combine(Fixtures, "usda-search-ice-cream-sandwich.json")));
            else if (FoodStatus is { } status)
                (response.Status, response.Body) = (status, "");
            else if (path == UsdaClient.FoodPath(172226))
                (response.Status, response.Body) = (200, File.ReadAllText(Path.Combine(Fixtures, "usda-food-172226.json")));
            else
                (response.Status, response.Body) = (404, "");
            return response;
        }

        public void Dispose()
        {
            if (_stop.IsCancellationRequested) return;
            _stop.Cancel();
            _loop.Wait();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
    }
}
