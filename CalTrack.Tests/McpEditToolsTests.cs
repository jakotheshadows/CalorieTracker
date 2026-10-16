using System.Text.Json;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using CalTrack.Mcp;
using ModelContextProtocol;

namespace CalTrack.Tests;

/// <summary>
/// The MCP tools that change and read everything beyond logging: against a real temp folder,
/// with the app's side played by Inbox.Ingest.
/// </summary>
public sealed class McpEditToolsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("caltrack-edit-test-").FullName;
    private readonly DataFolderStore _store;
    // "Today" is 2026-10-08 in the user's time zone; the history is on the 7th and 8th.
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 8, 9, 30, 0, TimeSpan.FromHours(-4)));
    private string MainPath => Path.Combine(_dir, Inbox.MainFileName);
    private string InboxDir => Path.Combine(_dir, Inbox.DirName);

    public McpEditToolsTests()
    {
        _store = new DataFolderStore(_dir);
        var data = EditTests.WithHistory();
        data.RequestKinds = AppRequests.SupportedKinds.ToList();
        WriteMain(data);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteMain(AppData data) => File.WriteAllText(MainPath, AppDataJson.Serialize(data, indented: true));
    private AppData ReadMain() => AppDataJson.Parse(File.ReadAllText(MainPath));
    private int InboxCount() => Directory.Exists(InboxDir) ? Directory.GetFiles(InboxDir).Length : 0;

    /// <summary>The app's tick: fold the inbox in, save, delete. Asserts every op applied and the server's preview matched.</summary>
    private void AppIngest()
    {
        var preview = AppDataJson.Serialize(_store.Read().View);
        var data = ReadMain();
        var files = Directory.GetFiles(InboxDir).Select(p => new InboxFile(Path.GetFileName(p), File.ReadAllText(p))).ToList();
        var r = Inbox.Ingest(data, files);
        Assert.Empty(r.Rejected);
        if (r.DataChanged) WriteMain(data);
        foreach (var name in r.FilesToDelete) File.Delete(Path.Combine(InboxDir, name));
        data.ProcessedOpIds.Clear();
        Assert.Equal(preview, AppDataJson.Serialize(data));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private string Refusal(Func<object> call)
    {
        var queued = InboxCount();
        var ex = Assert.Throws<McpException>(call);
        Assert.Equal(queued, InboxCount()); // a refusal never queues anything
        return ex.Message;
    }

    // ---------- log entries ----------

    [Fact]
    public void A_repeated_one_off_needs_its_entry_number_and_the_right_one_changes()
    {
        Assert.Contains("pass entry", Refusal(() => CalTrackTools.UpdateEntry(_store, _clock, 2, name: "Cookie", date: "2026-10-07")));
        Assert.Contains("is \"Cookie\", not \"Large egg\"", Refusal(() => CalTrackTools.UpdateEntry(_store, _clock, 2, name: "Large egg", entry: 4, date: "2026-10-07")));
        Assert.Contains("entry must be 1 to 4", Refusal(() => CalTrackTools.UpdateEntry(_store, _clock, 2, entry: 9, date: "2026-10-07")));

        var result = Parse(CalTrackTools.UpdateEntry(_store, _clock, 2, entry: 4, date: "2026-10-07"));
        Assert.Equal(1, result.GetProperty("updated").GetProperty("servingsBefore").GetDouble());
        Assert.Equal(400, result.GetProperty("updated").GetProperty("calories").GetDouble());
        AppIngest();
        Assert.Equal(new[] { 1.0, 2.0 }, ReadMain().GetDay(new DateOnly(2026, 10, 7)).Where(e => e.ItemName == "Cookie").Select(e => e.Servings));
    }

    [Fact]
    public void Removing_an_entry_reports_what_it_was_so_it_can_be_logged_again()
    {
        var result = Parse(CalTrackTools.RemoveEntry(_store, _clock, entry: 3, date: "2026-10-07"));
        var removed = result.GetProperty("removed");
        Assert.Equal("Cookie", removed.GetProperty("name").GetString());
        Assert.Equal(150, removed.GetProperty("oneOff").GetProperty("caloriesPerServing").GetDouble());
        Assert.Contains("remove Cookie (one-off)", result.GetProperty("day").GetProperty("pendingOps")[0].GetString());
        AppIngest();
        Assert.Single(ReadMain().GetDay(new DateOnly(2026, 10, 7)), e => e.ItemName == "Cookie");

        Assert.Contains("Nothing named \"Toast\"", Refusal(() => CalTrackTools.RemoveEntry(_store, _clock, name: "Toast")));
        Assert.Contains("Nothing is logged on 2026-10-09", Refusal(() => CalTrackTools.ClearDay(_store, _clock, "2026-10-09")));
    }

    [Fact]
    public void Clearing_a_day_lists_what_it_removed()
    {
        var result = Parse(CalTrackTools.ClearDay(_store, _clock, "today"));
        Assert.Equal("Large egg", result.GetProperty("removed")[0].GetProperty("name").GetString());
        AppIngest();
        Assert.Empty(ReadMain().GetDay(new DateOnly(2026, 10, 8)));
    }

    [Fact]
    public void A_summary_covers_the_logged_days_in_a_range()
    {
        var result = Parse(CalTrackTools.GetSummary(_store, _clock, from: "2026-10-01", to: "today"));
        Assert.Equal(2, result.GetProperty("daysLogged").GetInt32());
        Assert.Equal(144 + 190 + 150 + 200 + 216, result.GetProperty("totals").GetProperty("calories").GetDouble());
        Assert.True(result.GetProperty("suggestedDailyCalories").GetDouble() > 1000);
    }

    // ---------- menu items ----------

    [Fact]
    public void Editing_a_menu_item_changes_only_what_was_passed()
    {
        Assert.Contains("use update_recipe", Refusal(() => CalTrackTools.UpdateMenuItem(_store, _clock, "Overnight oats", calories: 1)));
        Assert.Contains("Nothing to change", Refusal(() => CalTrackTools.UpdateMenuItem(_store, _clock, "Large egg")));
        Assert.Contains("Unknown nutrient \"zinc\"", Refusal(() => CalTrackTools.UpdateMenuItem(_store, _clock, "Large egg", nutrients: new() { ["zinc"] = 1 })));
        Assert.Contains("already on the menu", Refusal(() => CalTrackTools.UpdateMenuItem(_store, _clock, "Large egg", newName: "whole wheat toast")));
        Assert.Contains("Closest menu names: \"Large egg\"", Refusal(() => CalTrackTools.UpdateMenuItem(_store, _clock, "large eggs", calories: 1)));

        var result = Parse(CalTrackTools.UpdateMenuItem(_store, _clock, "large egg", newName: "Egg", category: "snack", nutrients: new() { ["fat"] = 5, ["protein"] = null }));
        Assert.Equal(72, result.GetProperty("after").GetProperty("calories").GetDouble());
        Assert.Equal("Snack", result.GetProperty("after").GetProperty("category").GetString());
        AppIngest();
        var egg = ReadMain().FindItem("Egg")!;
        Assert.Equal(new Dictionary<string, double> { ["fat"] = 5 }, egg.Nutrients);
        Assert.Equal("Egg", ReadMain().GetDay(new DateOnly(2026, 10, 8)).Single().ItemName);
    }

    [Fact]
    public void Deleting_a_menu_item_keeps_history_and_returns_the_item()
    {
        Assert.Contains("use delete_recipe", Refusal(() => CalTrackTools.DeleteMenuItem(_store, _clock, "Overnight oats")));
        var result = Parse(CalTrackTools.DeleteMenuItem(_store, _clock, "Large egg"));
        Assert.Equal(72, result.GetProperty("deleted").GetProperty("calories").GetDouble());
        Assert.Equal(2, result.GetProperty("historyKept").GetProperty("loggedDays").GetInt32());
        Assert.Equal("Breakfast", result.GetProperty("historyKept").GetProperty("templates")[0].GetString());
        AppIngest();
        var main = ReadMain();
        Assert.Null(main.FindItem("Large egg"));
        Assert.Equal(216, main.TotalsForDay(new DateOnly(2026, 10, 8)).Calories);
    }

    [Fact]
    public void Get_menu_item_shows_a_recipes_ingredients_and_where_it_is_used()
    {
        var result = Parse(CalTrackTools.GetMenuItem(_store, "overnight oats"));
        Assert.Equal("recipe", result.GetProperty("kind").GetString());
        var oats = result.GetProperty("recipe").GetProperty("ingredients")[0];
        Assert.Equal(("Oats", 380.0), (oats.GetProperty("name").GetString(), oats.GetProperty("calories").GetDouble()));
        Assert.Equal(1, result.GetProperty("usedIn").GetProperty("loggedDays").GetInt32());
    }

    // ---------- recipes ----------

    [Fact]
    public async Task A_recipe_edit_can_remove_reamount_and_add_ingredients_including_from_usda()
    {
        using var app = new UsdaMenuTests.FakeApp(_dir);
        var usda = new UsdaGateway(_store, TimeSpan.FromSeconds(5));
        var result = Parse(await CalTrackTools.UpdateRecipe(_store, usda, _clock, "Overnight oats",
            servings: 1,
            ingredientAmounts: new() { new IngredientAmountInput { Name = "oats", Amount = 50 } },
            addIngredients: new()
            {
                new RecipeIngredientInput { UsdaFdcId = 172226 },
                new RecipeIngredientInput { Name = "Honey", Amount = 21, Unit = "g", Calories = 64 },
            }));
        Assert.Equal(new[] { "Oats", "Ice cream sandwich", "Honey" }, result.GetProperty("after").GetProperty("ingredients").EnumerateArray().Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(190 + 165.9 + 64, result.GetProperty("after").GetProperty("wholeRecipeCalories").GetDouble(), 1);
        AppIngest();
        Assert.Equal(1, ReadMain().FindRecipe("Overnight oats")!.Servings);

        Assert.Contains("no ingredient \"Milk\"", Refusal(() => CalTrackTools.UpdateRecipe(_store, usda, _clock, "Overnight oats", removeIngredients: new() { "Milk" }).GetAwaiter().GetResult()));
        Assert.Contains("use update_menu_item", Refusal(() => CalTrackTools.UpdateRecipe(_store, usda, _clock, "Large egg", servings: 2).GetAwaiter().GetResult()));
    }

    [Fact]
    public void Deleting_a_recipe_returns_its_ingredients_in_add_recipes_form()
    {
        var result = Parse(CalTrackTools.DeleteRecipe(_store, _clock, "Overnight oats"));
        var oats = result.GetProperty("deleted").GetProperty("ingredients")[0];
        Assert.Equal((100.0, "g", 380.0), (oats.GetProperty("amount").GetDouble(), oats.GetProperty("unit").GetString(), oats.GetProperty("calories").GetDouble()));
        AppIngest();
        Assert.Null(ReadMain().FindRecipe("Overnight oats"));
        Assert.Equal(684, ReadMain().TotalsForDay(new DateOnly(2026, 10, 7)).Calories); // unchanged: kept as a one-off
    }

    // ---------- templates ----------

    [Fact]
    public void Templates_can_be_listed_saved_applied_and_deleted()
    {
        Assert.Equal(224, Parse(CalTrackTools.ListTemplates(_store)).GetProperty("templates")[0].GetProperty("calories").GetDouble());
        Assert.Contains("overwrite: true", Refusal(() => CalTrackTools.SaveTemplate(_store, _clock, "breakfast")));
        Assert.Contains("Closest: \"Breakfast\"", Refusal(() => CalTrackTools.ApplyTemplate(_store, _clock, "breakfst")));

        CalTrackTools.SaveTemplate(_store, _clock, "Thursday", date: "2026-10-07");
        var applied = Parse(CalTrackTools.ApplyTemplate(_store, _clock, "breakfast", date: "2026-10-09"));
        Assert.Equal(2, applied.GetProperty("day").GetProperty("entries").GetArrayLength());
        CalTrackTools.DeleteTemplate(_store, _clock, "Breakfast");
        AppIngest();
        var main = ReadMain();
        Assert.Equal(new[] { "Thursday" }, main.Templates.Keys);
        Assert.Equal(224, main.TotalsForDay(new DateOnly(2026, 10, 9)).Calories);
    }

    // ---------- weight & goals ----------

    [Fact]
    public void Weights_and_goals_speak_the_users_units_and_keep_the_apps_limits()
    {
        var goals = Parse(CalTrackTools.GetGoals(_store, _clock));
        Assert.Equal("lb", goals.GetProperty("weightUnit").GetString());
        Assert.Equal(180.8, goals.GetProperty("recentWeights")[0].GetProperty("weight").GetDouble()); // 82 kg

        var logged = Parse(CalTrackTools.LogWeight(_store, _clock, 180)); // lb, from the profile
        Assert.Equal(180, logged.GetProperty("logged").GetProperty("weight").GetDouble());
        Assert.Contains("doesn't look like a body weight", Refusal(() => CalTrackTools.LogWeight(_store, _clock, 1800)));

        Assert.Contains("below the healthy range", Refusal(() => CalTrackTools.SetGoals(_store, _clock, targetWeight: 100)));
        Assert.Contains("must be between 0 and 1", Refusal(() => CalTrackTools.SetGoals(_store, _clock, targetRatePercentPerWeek: 2)));
        var set = Parse(CalTrackTools.SetGoals(_store, _clock, units: "metric", height: 172, targetWeight: 68));
        Assert.Equal("172 cm", set.GetProperty("after").GetProperty("height").GetString());
        Assert.Equal(68, set.GetProperty("after").GetProperty("targetWeight").GetDouble());

        CalTrackTools.RemoveWeight(_store, _clock, "2026-10-07");
        AppIngest();
        var main = ReadMain();
        Assert.Equal(new[] { "2026-10-08" }, main.Weights.Keys);
        Assert.Equal(81.65, main.Weights["2026-10-08"]);
        Assert.Equal((UnitSystem.Metric, 172.0, 68.0, Sex.Female), (main.Goals!.Units, main.Goals.HeightCm, main.Goals.TargetWeightKg, main.Goals.Sex));
    }

    // ---------- random plans ----------

    [Fact]
    public void A_random_plan_fills_only_empty_days_unless_told_to_replace_them()
    {
        Assert.Contains("already has entries", Refusal(() => CalTrackTools.PlanRandomDays(_store, _clock, "2026-10-07", to: "2026-10-08", calories: 1500)));
        var result = Parse(CalTrackTools.PlanRandomDays(_store, _clock, "2026-10-08", to: "2026-10-10", calories: 1500, minItemsPerDay: 1));
        Assert.Equal(new[] { "2026-10-09", "2026-10-10" }, result.GetProperty("planned").EnumerateArray().Select(d => d.GetProperty("date").GetString()));
        Assert.Equal("2026-10-08", result.GetProperty("leftAlone")[0].GetString());
        AppIngest();
        Assert.NotEmpty(ReadMain().GetDay(new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void Edits_made_in_the_same_instant_apply_in_the_order_they_were_made()
    {
        // The clock is frozen: without ordering by the inbox, the delete could land first.
        CalTrackTools.ApplyTemplate(_store, _clock, "Breakfast", date: "2026-10-09");
        CalTrackTools.DeleteTemplate(_store, _clock, "Breakfast");
        CalTrackTools.UpdateEntry(_store, _clock, 5, name: "Large egg", date: "2026-10-09");
        CalTrackTools.RemoveEntry(_store, _clock, name: "Large egg", date: "2026-10-09");
        AppIngest();
        Assert.Equal(new[] { "Whole wheat toast" }, ReadMain().GetDay(new DateOnly(2026, 10, 9)).Select(e => e.ItemName));
    }

    // ---------- an app too old for the new edits ----------

    [Fact]
    public void Every_edit_is_refused_while_the_app_is_too_old_to_apply_it()
    {
        var data = ReadMain();
        data.InboxKinds = new() { InboxOp.LogFood, InboxOp.LogAdHoc, InboxOp.AddItem, InboxOp.AddRecipe }; // a 0.6 app
        WriteMain(data);
        var usda = new UsdaGateway(_store, TimeSpan.FromSeconds(1));
        var calls = new Func<object>[]
        {
            () => CalTrackTools.UpdateEntry(_store, _clock, 1, name: "Large egg"),
            () => CalTrackTools.RemoveEntry(_store, _clock, name: "Large egg"),
            () => CalTrackTools.ClearDay(_store, _clock, "today"),
            () => CalTrackTools.UpdateMenuItem(_store, _clock, "Large egg", calories: 70),
            () => CalTrackTools.DeleteMenuItem(_store, _clock, "Large egg"),
            () => CalTrackTools.UpdateRecipe(_store, usda, _clock, "Overnight oats", servings: 3).GetAwaiter().GetResult(),
            () => CalTrackTools.DeleteRecipe(_store, _clock, "Overnight oats"),
            () => CalTrackTools.SaveTemplate(_store, _clock, "New"),
            () => CalTrackTools.ApplyTemplate(_store, _clock, "Breakfast"),
            () => CalTrackTools.DeleteTemplate(_store, _clock, "Breakfast"),
            () => CalTrackTools.LogWeight(_store, _clock, 180),
            () => CalTrackTools.RemoveWeight(_store, _clock, "2026-10-07"),
            () => CalTrackTools.SetGoals(_store, _clock, activity: "Moderate"),
            () => CalTrackTools.PlanRandomDays(_store, _clock, "2026-10-12", calories: 1500),
        };
        foreach (var call in calls)
            Assert.Contains("update it", Refusal(call));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
    }
}
