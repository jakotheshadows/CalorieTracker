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
        data.InboxFeatures = Inbox.SupportedFeatures.ToList();
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
        Assert.Equal("USDA API error (HTTP 400): Invalid dataType", UsdaClient.ReadSearch(400, """{"error":"Bad Request","message":"Invalid dataType"}""").Error);
        Assert.Equal("USDA API error (HTTP 400): Bad thing", UsdaClient.ReadSearch(400, """{"error":{"code":"X","message":"Bad thing"}}""").Error);
        Assert.Equal("USDA API error (HTTP 400): bad query at /search?api_key=***", UsdaClient.ReadSearch(400, "bad  query\nat /search?api_key=SECRET123").Error);
        Assert.Equal("USDA API error (HTTP 502).", UsdaClient.ReadSearch(502, "<html><body>Bad Gateway</body></html>").Error);
        Assert.Equal("""USDA API error (HTTP 400): USDA's web server rejected the request before it reached the API ("400 Bad Request").""",
            UsdaClient.ReadSearch(400, "<html>\r\n<head><title>400 Bad Request</title></head>\r\n<body><center>nginx</center></body></html>").Error);
        Assert.EndsWith("…", UsdaClient.ReadSearch(400, new string('x', 500)).Error);
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
        _app.FoodStatus = 500; // full records fail: search_usda lists the food without portions, add_menu_item falls back
        await CalTrackTools.SearchUsda(usda, "ice cream sandwich");

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
    public void Responses_in_the_workers_shape_arrive_verbatim()
    {
        // PascalCase JSON, as request-worker.js writes it; USDA's reply passes through untouched.
        var body = File.ReadAllText(Path.Combine(Fixtures, "usda-food-172226.json"));
        var back = AppRequests.TryParseResponse(JsonSerializer.Serialize(new { Id = "x", Error = (string?)null, Status = 200, Body = body }))!;
        Assert.Equal(body, back.Body);

        var notFound = AppRequests.TryParseResponse("""{"Id":"y","Error":null,"Status":404,"Body":""}""")!;
        Assert.Equal("USDA has no food with id 9.", UsdaClient.ReadFood(notFound.Status, notFound.Body, 9).Error);

        Assert.Null(AppRequests.TryParseResponse("""{"Id":"z","Status":2""")); // cut short: not an answer yet
        Assert.Null(AppRequests.TryParseResponse("""{"Status":200}"""));       // no id: nobody's answer
    }

    // ---------- recipes ----------

    [Fact]
    public void AddRecipe_op_adds_a_recipe_with_the_apps_rules()
    {
        var data = InboxTests.Menu();
        var brownie = new RecipeIngredient { Name = "Brownie", Amount = 56, Unit = "g", CaloriesPer100 = 405 };

        var (ok, _) = Inbox.Apply(data, RecipeOp("Brownie sundae", 1, brownie));
        Assert.NotNull(ok);
        Assert.Equal(227, data.ResolveItem("brownie sundae")!.Calories); // 405 × 0.56, one serving
        Assert.Equal(new[] { "Brownie sundae", "Overnight oats" }, data.Recipes.Select(r => r.Name)); // kept sorted, as the app does

        Assert.Contains("already on the menu", Inbox.Apply(data, RecipeOp("LARGE EGG", 1, brownie)).Error);      // an item
        Assert.Contains("already on the menu", Inbox.Apply(data, RecipeOp("overnight oats", 1, brownie)).Error); // a recipe
        Assert.Contains("at least one ingredient", Inbox.Apply(data, RecipeOp("Empty", 1)).Error);
        Assert.Contains("bad servings", Inbox.Apply(data, RecipeOp("Zero", 0, brownie)).Error);
        Assert.Contains("bad ingredient", Inbox.Apply(data, RecipeOp("Weightless", 1,
            new RecipeIngredient { Name = "Air", Amount = 0, Unit = "g", CaloriesPer100 = 1 })).Error);
    }

    [Fact]
    public async Task A_recipe_from_usda_and_user_ingredients_is_added_and_logged_end_to_end()
    {
        var result = JsonDocument.Parse(await CalTrackTools.AddRecipe(_store, Usda(), "Sandwich sundae",
        [
            new RecipeIngredientInput { UsdaFdcId = 172226 },                                                  // USDA: 1 serving, 70 g
            new RecipeIngredientInput { Name = "Hot fudge", Amount = 40, Unit = "g", Calories = 140, CarbsG = 22 }, // the user's numbers
        ])).RootElement;

        var added = result.GetProperty("added");
        Assert.Equal(306, added.GetProperty("perServing").GetProperty("calories").GetDouble()); // 165.9 + 140
        var ingredients = result.GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal("1 serving (70 g)", ingredients[0].GetProperty("amount").GetString());
        Assert.Equal("USDA #172226 Ice cream sandwich", ingredients[0].GetProperty("source").GetString());
        Assert.Equal("user-provided", ingredients[1].GetProperty("source").GetString());

        // Loggable straight away, like any menu item; the app then folds both ops in, in order.
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(-4)));
        CalTrackTools.LogFood(_store, clock, "sandwich sundae", 1);
        AppIngest();

        var main = ReadMain();
        var recipe = main.FindRecipe("Sandwich sundae")!;
        Assert.Equal(2, recipe.Ingredients.Count);
        Assert.Contains("fdc.nal.usda.gov/food-details/172226", recipe.Description);
        Assert.Equal(306, main.TotalsForDay(Day).Calories);
    }

    [Fact]
    public async Task Recipe_ingredients_must_be_grounded_and_nothing_is_written_otherwise()
    {
        async Task<string> Refusal(string name, params RecipeIngredientInput[] ingredients) =>
            (await Assert.ThrowsAsync<McpException>(() => CalTrackTools.AddRecipe(_store, Usda(), name, ingredients.ToList()))).Message;

        Assert.Contains("not both", await Refusal("A", new RecipeIngredientInput { UsdaFdcId = 172226, Calories = 100 }));
        Assert.Contains("calories is required", await Refusal("B", new RecipeIngredientInput { Name = "Fudge", Amount = 40 }));
        Assert.Contains("amount is required", await Refusal("C", new RecipeIngredientInput { Name = "Fudge", Calories = 140 }));
        Assert.Contains("\"1 serving\"", await Refusal("D", new RecipeIngredientInput { UsdaFdcId = 172226, Portion = "1 sandwich" }));
        Assert.Contains("at least one", await Refusal("E"));
        Assert.Contains("already on the menu", await Refusal("Large egg", new RecipeIngredientInput { UsdaFdcId = 172226 }));
        Assert.Equal(0, InboxCount());
    }

    [Fact]
    public async Task Recipes_are_refused_while_the_app_is_too_old_to_add_them()
    {
        var data = ReadMain();
        data.InboxKinds = new() { InboxOp.LogFood, InboxOp.LogAdHoc, InboxOp.AddItem }; // a version before recipes
        WriteMain(data);

        var ex = await Assert.ThrowsAsync<McpException>(() => CalTrackTools.AddRecipe(_store, Usda(), "Sundae",
            [new RecipeIngredientInput { UsdaFdcId = 172226 }]));

        Assert.Contains("update it", ex.Message);
        Assert.Equal(0, _app.Answered); // refused before asking USDA anything
        Assert.Equal(0, InboxCount());
    }

    // ---------- 0.7.1: finding dishes, and building them without guesswork ----------

    [Fact]
    public void Searches_include_USDAs_mixed_dishes_and_can_be_narrowed()
    {
        Assert.Contains(Uri.EscapeDataString("Survey (FNDDS)"), UsdaClient.SearchPath("reuben", 5));
        Assert.Contains(Uri.EscapeDataString("Branded"), UsdaClient.SearchPath("reuben", 5));
        Assert.DoesNotContain("Branded", UsdaClient.SearchPath("reuben", 5, UsdaClient.GenericTypes));
        Assert.All(new[] { UsdaClient.SearchPath("reuben", 5), UsdaClient.SearchPath("reuben", 5, UsdaClient.BrandedTypes) },
            path => Assert.True(WorkerAllows(path), path)); // the app will fetch them
    }

    [Fact]
    public void FNDDS_search_hits_carry_household_portions()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, "usda-search-reuben-generic.json")));
        var reuben = UsdaClient.ParseSearchFood(doc.RootElement.GetProperty("foods")[0]);

        Assert.Equal("Survey (FNDDS)", reuben.DataType);
        Assert.Equal("Reuben sandwich", reuben.Description);
        Assert.Equal(new UsdaPortion("1 sandwich, any size", 215), Assert.Single(reuben.Portions)); // "Quantity not specified" skipped
        Assert.True(UsdaClient.IsGeneric(reuben));
    }

    [Fact]
    public async Task search_usda_lists_generic_foods_first_with_their_portions()
    {
        var result = JsonDocument.Parse(await CalTrackTools.SearchUsda(Usda(), "ice cream sandwich")).RootElement;
        var types = result.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("dataType").GetString()).ToList();

        // USDA ranked a branded product second; generic foods now come first, then branded.
        Assert.Equal(new[] { "SR Legacy", "SR Legacy", "SR Legacy", "SR Legacy", "Branded" }, types);
        var first = result.GetProperty("results")[0];
        Assert.Equal(172226, first.GetProperty("fdcId").GetInt32());
        Assert.Equal("1 serving (70 g)", first.GetProperty("portions")[0].GetString()); // looked up from the full record
        Assert.False(result.GetProperty("results")[4].TryGetProperty("portions", out _)); // branded: its label serving instead
    }

    [Fact]
    public async Task branded_narrows_the_search_and_a_named_brand_gets_only_products()
    {
        await CalTrackTools.SearchUsda(Usda(), "reuben on rye", branded: true);
        await CalTrackTools.SearchUsda(Usda(), "reuben", branded: false);

        lock (_app.Paths)
        {
            Assert.EndsWith("dataType=Branded", _app.Paths.First(p => p.Contains("query=reuben%20on%20rye")));
            Assert.EndsWith("dataType=" + Uri.EscapeDataString(UsdaClient.GenericTypes), _app.Paths.First(p => p.Contains("query=reuben&")));
        }
    }

    [Fact]
    public async Task A_branded_record_that_lost_its_serving_size_gets_it_back_from_the_search_hit()
    {
        // USDA's full record for this sandwich has no servingSize; its search hit says 268.2 g.
        var usda = Usda();
        await CalTrackTools.SearchUsda(usda, "reuben on rye", branded: true);

        var result = JsonDocument.Parse(await CalTrackTools.AddMenuItem(_store, usda, usdaFdcId: 2105271, name: "Walgreens Reuben")).RootElement;

        var added = result.GetProperty("added");
        Assert.Equal("1 CONTAINER (268.2 g)", added.GetProperty("serving").GetString());
        Assert.Equal(601, added.GetProperty("calories").GetDouble());
        Assert.Contains("branded product", result.GetProperty("note").GetString());
        Assert.False(result.TryGetProperty("warning", out _));
    }

    [Fact]
    public async Task Without_a_search_hit_a_serving_less_branded_record_says_its_serving_is_100_g()
    {
        var result = JsonDocument.Parse(await CalTrackTools.AddMenuItem(_store, Usda(), usdaFdcId: 2105271, name: "Walgreens Reuben")).RootElement;

        Assert.Equal("100 g", result.GetProperty("added").GetProperty("serving").GetString());
        Assert.Contains("no package serving", result.GetProperty("warning").GetString());
    }

    [Fact]
    public async Task A_recipe_ingredient_can_be_several_of_a_household_portion()
    {
        var result = JsonDocument.Parse(await CalTrackTools.AddRecipe(_store, Usda(), "Double sandwich",
            [new RecipeIngredientInput { UsdaFdcId = 172226, Portion = "1 serving", Count = 2 }])).RootElement;

        var ingredient = Assert.Single(result.GetProperty("ingredients").EnumerateArray());
        Assert.Equal("2 × 1 serving (70 g) = 140 g", ingredient.GetProperty("amount").GetString());
        Assert.Equal(332, result.GetProperty("added").GetProperty("perServing").GetProperty("calories").GetDouble()); // 237 kcal/100 g × 140 g

        AppIngest();
        var stored = Assert.Single(ReadMain().FindRecipe("Double sandwich")!.Ingredients);
        Assert.Equal((140, "g"), (stored.Amount, stored.Unit));
    }

    [Fact]
    public async Task A_food_on_the_menu_can_be_a_recipe_ingredient_measured_in_its_servings()
    {
        var result = JsonDocument.Parse(await CalTrackTools.AddRecipe(_store, Usda(), "Egg sandwich",
        [
            new RecipeIngredientInput { MenuItem = "large egg", Count = 2 },          // an item: 72 kcal a serving
            new RecipeIngredientInput { MenuItem = "Overnight oats", Count = 0.5 },   // a recipe: 380 / 2 = 190 kcal a serving
        ])).RootElement;

        Assert.Equal(239, result.GetProperty("added").GetProperty("perServing").GetProperty("calories").GetDouble()); // 144 + 95
        var report = result.GetProperty("ingredients").EnumerateArray().ToList();
        Assert.Equal("2 servings of \"Large egg\"", report[0].GetProperty("amount").GetString());
        Assert.Equal("the user's menu: Large egg", report[0].GetProperty("source").GetString());

        // The app applies it with the same numbers, and shows the amount in servings.
        AppIngest();
        var recipe = ReadMain().FindRecipe("Egg sandwich")!;
        Assert.Equal(new[] { "2 servings", "0.5 servings" }, recipe.Ingredients.Select(i => i.AmountDisplay));
        Assert.Equal(12.6, recipe.Ingredients[0].Nutrient("protein")!.Value, 3);
        Assert.Equal(239, recipe.ToMenuItem().Calories);
    }

    [Fact]
    public async Task Menu_item_ingredients_are_checked_and_wait_for_an_app_that_understands_servings()
    {
        async Task<string> Refusal(params RecipeIngredientInput[] ingredients) =>
            (await Assert.ThrowsAsync<McpException>(() => CalTrackTools.AddRecipe(_store, Usda(), "New dish", ingredients.ToList()))).Message;

        Assert.Contains("Closest menu names", await Refusal(new RecipeIngredientInput { MenuItem = "Large eggs" }));
        Assert.Contains("only count", await Refusal(new RecipeIngredientInput { MenuItem = "Large egg", Amount = 50, Unit = "g" }));
        Assert.Contains("not both", await Refusal(new RecipeIngredientInput { MenuItem = "Large egg", UsdaFdcId = 172226 }));
        Assert.Contains("not both", await Refusal(new RecipeIngredientInput { UsdaFdcId = 172226, Count = 2, Amount = 140 }));
        Assert.Contains("count must be", await Refusal(new RecipeIngredientInput { MenuItem = "Large egg", Count = 0 }));

        var data = ReadMain();
        data.InboxFeatures = new(); // an app from before serving units: it would count no calories for them
        WriteMain(data);
        Assert.Contains("update it", await Refusal(new RecipeIngredientInput { MenuItem = "Large egg" }));
        Assert.Equal(0, InboxCount());
    }

    [Fact]
    public async Task A_recipe_cannot_contain_itself()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.FromHours(-4)));
        var ex = await Assert.ThrowsAsync<McpException>(() => CalTrackTools.UpdateRecipe(_store, Usda(), clock, "Overnight oats",
            addIngredients: [new RecipeIngredientInput { MenuItem = "overnight oats" }]));
        Assert.Contains("can't contain itself", ex.Message);
    }

    [Fact]
    public void Serving_ingredients_scale_but_never_turn_into_weight()
    {
        var chili = new RecipeIngredient { Name = "Chili", Amount = 1.5, Unit = Units.Serving, CaloriesPer100 = 71600 };
        Assert.Equal(1074, chili.Calories);
        Assert.Equal("1.5 servings", chili.AmountDisplay);
        Assert.Equal(Units.Serving, chili.BaseUnit);

        var data = InboxTests.Menu();
        data.Recipes.Add(new Recipe { Name = "Chili dog", Ingredients = { chili } });
        Assert.Null(data.UpdateRecipe("Chili dog", new RecipePatch { IngredientAmounts = [new IngredientAmount { Name = "Chili", Amount = 1, Unit = "serving" }] }));
        Assert.Equal(716, data.FindRecipe("Chili dog")!.ToMenuItem().Calories);
        Assert.Contains("measured in servings", data.UpdateRecipe("Chili dog",
            new RecipePatch { IngredientAmounts = [new IngredientAmount { Name = "Chili", Amount = 100, Unit = "g" }] }));
    }

    [Fact]
    public void The_tools_a_model_loads_first_all_point_to_add_recipe_for_dishes()
    {
        // With tools loaded on demand, the model only reads the descriptions of the tools it picked:
        // in testing, these four. Each must lead to add_recipe for a dish no single food matches.
        foreach (var tool in new[] { "list_menu_items", "search_usda", "log_adhoc", "add_menu_item" })
        {
            var method = typeof(CalTrackTools).GetMethods().Single(m =>
                m.GetCustomAttributes(typeof(ModelContextProtocol.Server.McpServerToolAttribute), false)
                 .Cast<ModelContextProtocol.Server.McpServerToolAttribute>().Any(a => a.Name == tool));
            var description = ((System.ComponentModel.DescriptionAttribute)method.GetCustomAttributes(typeof(System.ComponentModel.DescriptionAttribute), false).Single()).Description;
            Assert.Contains("add_recipe", description);
        }
        Assert.Contains("add_recipe", ServerSetup.Instructions);
    }

    private static InboxOp RecipeOp(string name, double servings, params RecipeIngredient[] ingredients) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CreatedUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc),
        Kind = InboxOp.AddRecipe,
        Recipe = new Recipe { Name = name, Servings = servings, Ingredients = ingredients.ToList() },
    };

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
        var js = TestRepo.Read("wwwroot", "js", "request-worker.js");
        return new Regex(Regex.Match(js, @"const ALLOWED = /(.+)/;").Groups[1].Value);
    });

    /// <summary>
    /// Stands in for the CalTrack app's side of the request channel (request-worker.js):
    /// watches requests/, "fetches" from the captured USDA replies, writes responses/ and
    /// removes the request. Error and FoodStatus simulate an app without a key and USDA
    /// failing; Paths records what was asked for.
    /// </summary>
    internal sealed class FakeApp : IDisposable
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
                            if (ReadLive(File.ReadAllText(path)) is { } request)
                            {
                                Directory.CreateDirectory(responses);
                                File.WriteAllText(Path.Combine(responses, AppRequests.FileNameFor(request.Id)), JsonSerializer.Serialize(Answer(request)));
                                Interlocked.Increment(ref Answered);
                            }
                            File.Delete(path);
                        }
                    try { await Task.Delay(25, _stop.Token); } catch (OperationCanceledException) { }
                }
            });
        }

        /// <summary>The worker's rules: skip unreadable requests and those past their asker's timeout.</summary>
        private static AppRequest? ReadLive(string text)
        {
            try
            {
                var r = JsonSerializer.Deserialize<AppRequest>(text);
                return r is { Id.Length: > 0 } && DateTime.UtcNow - r.CreatedUtc <= TimeSpan.FromSeconds(Math.Max(1, r.TimeoutSeconds)) ? r : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private AppResponse Answer(AppRequest r)
        {
            var path = r.Path ?? "";
            lock (Paths) Paths.Add(path);
            var response = new AppResponse { Id = r.Id, Error = Error };
            if (Error is not null) return response;
            if (path.StartsWith("foods/search?"))
            {
                // Real USDA replies: a Reuben search, generic (FNDDS) or branded, else the ice cream sandwich one.
                var fixture = !path.Contains("query=reuben") ? "usda-search-ice-cream-sandwich.json"
                    : path.EndsWith("dataType=" + UsdaClient.BrandedTypes) ? "usda-search-reuben-branded.json"
                    : "usda-search-reuben-generic.json";
                (response.Status, response.Body) = (200, File.ReadAllText(Path.Combine(Fixtures, fixture)));
            }
            else if (FoodStatus is { } status)
                (response.Status, response.Body) = (status, "");
            else if (path == UsdaClient.FoodPath(172226))
                (response.Status, response.Body) = (200, File.ReadAllText(Path.Combine(Fixtures, "usda-food-172226.json")));
            else if (path == UsdaClient.FoodPath(2105271)) // a Branded full record that lost its servingSize
                (response.Status, response.Body) = (200, File.ReadAllText(Path.Combine(Fixtures, "usda-food-2105271.json")));
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
