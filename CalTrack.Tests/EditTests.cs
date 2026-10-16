using System.Reflection;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;

namespace CalTrack.Tests;

/// <summary>
/// The edits the app and the inbox share (AppDataOps), and the inbox op for each: what the
/// app does when the user taps it is what an MCP request does when the app folds it in.
/// </summary>
public class EditTests
{
    private static readonly DateOnly Day = new(2026, 10, 7);
    private static readonly DateOnly Day2 = new(2026, 10, 8);
    private static int seq;

    /// <summary>The test menu with history: eggs and oats logged on two days, a template, a weight and goals.</summary>
    internal static AppData WithHistory()
    {
        var data = InboxTests.Menu();
        data.AddEntry(Day, "Large egg", 2);
        data.AddEntry(Day, "Overnight oats", 1);
        data.AddAdHoc(Day, new FoodItem { Name = "Cookie", Calories = 150 }, 1);
        data.AddAdHoc(Day, new FoodItem { Name = "Cookie", Calories = 200 }, 1);
        data.AddEntry(Day2, "Large egg", 3);
        data.Templates["Breakfast"] = new() { new ScheduleEntry { ItemName = "Large egg", Servings = 2 }, new ScheduleEntry { ItemName = "Whole wheat toast", Servings = 1 } };
        data.Weights[AppData.DayKey(Day)] = 82;
        data.Goals = new GoalSettings { Sex = Sex.Female, BirthDate = new(1990, 5, 1), HeightCm = 170, TargetWeightKg = 70, Activity = ActivityLevel.Light };
        data.InboxKinds = Inbox.SupportedKinds.ToList();
        return data;
    }

    internal static InboxOp Op(string kind, Action<InboxOp>? set = null)
    {
        var op = new InboxOp
        {
            Id = Guid.NewGuid().ToString("N"),
            CreatedUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(++seq),
            Kind = kind,
            Date = AppData.DayKey(Day),
            Source = "mcp",
        };
        set?.Invoke(op);
        return op;
    }

    // ---------- deletes keep history ----------

    [Fact]
    public void Deleting_a_menu_item_keeps_its_logged_days_as_one_offs_with_the_same_totals()
    {
        var data = WithHistory();
        var before = (data.TotalsForDay(Day).Calories, data.TotalsForDay(Day2).Calories);

        Assert.Equal(3, data.DeleteItem("large EGG")); // two days + one template entry
        Assert.Null(data.FindItem("Large egg"));
        Assert.Equal(before, (data.TotalsForDay(Day).Calories, data.TotalsForDay(Day2).Calories));
        var kept = data.GetDay(Day2).Single();
        Assert.Equal(("Large egg", 72.0, 3.0), (kept.AdHoc!.Name, kept.AdHoc.Calories!.Value, kept.Servings));
        Assert.NotNull(data.Templates["Breakfast"][0].AdHoc);
        Assert.Null(data.DeleteItem("Large egg")); // gone
    }

    [Fact]
    public void Deleting_a_recipe_keeps_its_per_serving_numbers_in_the_log()
    {
        var data = WithHistory();
        var before = data.TotalsForDay(Day).Calories;

        Assert.Equal(1, data.DeleteRecipe("Overnight oats"));
        Assert.Equal(before, data.TotalsForDay(Day).Calories);
        Assert.Equal(190, data.GetDay(Day).Single(e => e.ItemName == "Overnight oats").AdHoc!.Calories); // 380 / 2 servings
    }

    // ---------- menu edits ----------

    [Fact]
    public void An_item_patch_changes_only_what_it_names_and_a_rename_carries_the_log_along()
    {
        var data = WithHistory();
        data.FindItem("Large egg")!.Description = "edited in the app meanwhile";

        var error = data.UpdateItem("Large egg", new FoodItemPatch
        {
            NewName = "Egg",
            Calories = 78,
            Nutrients = new() { ["protein"] = null, ["fat"] = 5.3 },
        });

        Assert.Null(error);
        var egg = data.FindItem("Egg")!;
        Assert.Equal((78.0, "edited in the app meanwhile"), (egg.Calories!.Value, egg.Description));
        Assert.Equal(new Dictionary<string, double> { ["fat"] = 5.3 }, egg.Nutrients);
        Assert.Equal("Egg", data.GetDay(Day2).Single().ItemName);
        Assert.Equal("Egg", data.Templates["Breakfast"][0].ItemName);
    }

    [Theory]
    [InlineData("Overnight oats", "already exists")] // names are unique across items and recipes
    [InlineData("WHOLE wheat toast", "already exists")]
    public void A_rename_onto_another_name_is_refused(string newName, string message)
    {
        var data = WithHistory();
        Assert.Contains(message, data.UpdateItem("Large egg", new FoodItemPatch { NewName = newName }));
        Assert.NotNull(data.FindItem("Large egg"));
    }

    [Fact]
    public void A_recipe_patch_removes_reamounts_and_adds_ingredients()
    {
        var data = WithHistory();
        data.FindRecipe("Overnight oats")!.Ingredients.Add(new RecipeIngredient { Name = "Milk", Amount = 200, Unit = "ml", CaloriesPer100 = 60 });

        Assert.Null(data.UpdateRecipe("overnight oats", new RecipePatch
        {
            RemoveIngredients = new() { "oats" },
            IngredientAmounts = new() { new IngredientAmount { Name = "Milk", Amount = 300, Unit = "ml" } },
            AddIngredients = new() { new RecipeIngredient { Name = "Honey", Amount = 21, Unit = "g", CaloriesPer100 = 304 } },
            Servings = 1,
        }));
        var r = data.FindRecipe("Overnight oats")!;
        Assert.Equal(new[] { "Milk 300 ml", "Honey 21 g" }, r.Ingredients.Select(i => $"{i.Name} {i.AmountDisplay}"));
        Assert.Equal(244, r.ToMenuItem().Calories); // 180 + 63.84

        Assert.Contains("measured by volume", data.UpdateRecipe("Overnight oats", new RecipePatch { IngredientAmounts = new() { new IngredientAmount { Name = "Milk", Amount = 1, Unit = "lb" } } }));
        Assert.Contains("no ingredient", data.UpdateRecipe("Overnight oats", new RecipePatch { RemoveIngredients = new() { "Oats" } }));
    }

    // ---------- entries ----------

    [Fact]
    public void Entries_are_found_by_name_kind_and_which_of_the_same_named_one_offs()
    {
        var data = WithHistory();
        var second = data.FindEntry(Day, "cookie", oneOff: true, occurrence: 2)!;
        Assert.Equal(200, second.AdHoc!.Calories);
        Assert.Equal(2, data.OccurrenceOf(Day, second));
        Assert.Null(data.FindEntry(Day, "Cookie", oneOff: false));
        Assert.Null(data.FindEntry(Day, "Cookie", oneOff: true, occurrence: 3));

        var (summary, error) = Inbox.Apply(data, Op(InboxOp.UpdateEntry, o => { o.ItemName = "Cookie"; o.OneOff = true; o.Occurrence = 2; o.Servings = 0.5; }));
        Assert.Null(error);
        Assert.Contains("1 → 0.5", summary);
        Assert.Equal(new[] { 1.0, 0.5 }, data.GetDay(Day).Where(e => e.ItemName == "Cookie").Select(e => e.Servings));
    }

    [Fact]
    public void Removing_the_last_entry_removes_the_day_and_a_missing_entry_changes_nothing()
    {
        var data = WithHistory();
        Assert.Null(Inbox.Apply(data, Op(InboxOp.RemoveEntry, o => { o.Date = AppData.DayKey(Day2); o.ItemName = "Large egg"; })).Error);
        Assert.False(data.Days.ContainsKey(AppData.DayKey(Day2)));

        var json = AppDataJson.Serialize(data);
        Assert.Contains("any more", Inbox.Apply(data, Op(InboxOp.RemoveEntry, o => { o.Date = AppData.DayKey(Day2); o.ItemName = "Large egg"; })).Error);
        Assert.Equal(json, AppDataJson.Serialize(data));
    }

    [Fact]
    public void Clearing_a_day_removes_all_its_entries()
    {
        var data = WithHistory();
        Assert.Contains("4 entries", Inbox.Apply(data, Op(InboxOp.ClearDay)).Summary);
        Assert.Empty(data.GetDay(Day));
    }

    // ---------- templates ----------

    [Fact]
    public void Templates_save_apply_merging_regular_entries_and_delete()
    {
        var data = WithHistory();
        Assert.Contains("already exists", Inbox.Apply(data, Op(InboxOp.SaveTemplate, o => o.TemplateName = "breakfast")).Error);
        Assert.Null(Inbox.Apply(data, Op(InboxOp.SaveTemplate, o => { o.TemplateName = "breakfast"; o.Overwrite = true; })).Error);
        Assert.Equal(4, data.Templates["breakfast"].Count);

        Assert.Null(Inbox.Apply(data, Op(InboxOp.ApplyTemplate, o => { o.TemplateName = "BREAKFAST"; o.Date = AppData.DayKey(Day2); })).Error);
        var day2 = data.GetDay(Day2);
        Assert.Equal(5, day2.Single(e => e.ItemName == "Large egg").Servings); // 3 + 2 merged
        Assert.Equal(2, day2.Count(e => e.ItemName == "Cookie"));               // one-offs land as copies

        Assert.Null(Inbox.Apply(data, Op(InboxOp.DeleteTemplate, o => o.TemplateName = "Breakfast")).Error);
        Assert.Empty(data.Templates);
    }

    // ---------- weights & goals ----------

    [Fact]
    public void Weights_are_one_per_day_and_goals_keep_the_apps_limits()
    {
        var data = WithHistory();
        Assert.Null(Inbox.Apply(data, Op(InboxOp.LogWeight, o => o.WeightKg = 81.456)).Error);
        Assert.Equal(81.46, data.Weights[AppData.DayKey(Day)]);
        Assert.Null(Inbox.Apply(data, Op(InboxOp.RemoveWeight)).Error);
        Assert.Contains("no weight", Inbox.Apply(data, Op(InboxOp.RemoveWeight)).Error);

        Assert.Contains("healthy range", Inbox.Apply(data, Op(InboxOp.SetGoals, o => o.GoalsPatch = new GoalsPatch { TargetWeightKg = 45 })).Error);
        Assert.Null(Inbox.Apply(data, Op(InboxOp.SetGoals, o => o.GoalsPatch = new GoalsPatch { TargetWeightKg = 65, TargetRatePercentPerWeek = 3 })).Error);
        Assert.Equal((65.0, 1.0, 170.0), (data.Goals!.TargetWeightKg, data.Goals.TargetRatePercentPerWeek, data.Goals.HeightCm)); // rate capped, rest kept

        var fresh = InboxTests.Menu();
        Assert.Contains("needs sex, birth date", fresh.UpdateGoals(new GoalsPatch { HeightCm = 170, TargetWeightKg = 65 }));
        Assert.Null(fresh.Goals);
    }

    // ---------- random plans ----------

    [Fact]
    public void A_plan_fills_empty_days_from_the_menu_and_leaves_logged_days_alone()
    {
        var data = WithHistory();
        var plan = Planner.Plan(data, Day, Day2.AddDays(2), new RandomTargets { Calories = 600, MinItemsPerDay = 1, MaxItemsPerDay = 3 }, overwriteExisting: false, new Random(7));
        Assert.Null(plan.Error);
        Assert.Equal(new[] { "2026-10-07", "2026-10-08" }, plan.Skipped);
        Assert.Equal(new[] { "2026-10-09", "2026-10-10" }, plan.Days.Keys);
        Assert.All(plan.Days.Values.SelectMany(d => d), e => Assert.NotNull(data.ResolveItem(e.ItemName)));

        // The op lands days only where they're still empty when the app folds it in.
        data.AddEntry(new DateOnly(2026, 10, 9), "Large egg", 1);
        var (summary, _) = Inbox.Apply(data, Op(InboxOp.SetDays, o => o.Days = new(plan.Days)));
        Assert.Contains("planned 1 day (1 already had entries", summary);
        Assert.Single(data.GetDay(new DateOnly(2026, 10, 9)));
    }

    // ---------- the protocol ----------

    [Fact]
    public void Every_op_kind_is_one_the_app_applies()
    {
        var kinds = typeof(InboxOp).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetValue(null)!);
        Assert.Equal(kinds.Order(), Inbox.SupportedKinds.Order());
    }

    public static TheoryData<string> EditOps() => new()
    {
        InboxOp.UpdateEntry, InboxOp.RemoveEntry, InboxOp.ClearDay, InboxOp.UpdateItem, InboxOp.DeleteItem, InboxOp.UpdateRecipe,
        InboxOp.DeleteRecipe, InboxOp.SaveTemplate, InboxOp.ApplyTemplate, InboxOp.DeleteTemplate, InboxOp.LogWeight,
        InboxOp.RemoveWeight, InboxOp.SetGoals, InboxOp.SetDays,
    };

    /// <summary>A sample op of each kind that applies to <see cref="WithHistory"/>.</summary>
    internal static InboxOp Sample(string kind) => Op(kind, o =>
    {
        switch (kind)
        {
            case InboxOp.UpdateEntry: o.ItemName = "Large egg"; o.Servings = 4; break;
            case InboxOp.RemoveEntry: o.ItemName = "Cookie"; o.OneOff = true; o.Occurrence = 2; break;
            case InboxOp.UpdateItem: o.ItemName = "Large egg"; o.ItemPatch = new FoodItemPatch { NewName = "Egg", Calories = 70 }; break;
            case InboxOp.DeleteItem: o.ItemName = "Large egg"; break;
            case InboxOp.UpdateRecipe: o.ItemName = "Overnight oats"; o.RecipePatch = new RecipePatch { Servings = 4, AddIngredients = new() { new RecipeIngredient { Name = "Milk", Amount = 250, Unit = "ml", CaloriesPer100 = 42 } } }; break;
            case InboxOp.DeleteRecipe: o.ItemName = "Overnight oats"; break;
            case InboxOp.SaveTemplate: o.TemplateName = "Monday"; break;
            case InboxOp.ApplyTemplate: o.TemplateName = "Breakfast"; o.Date = "2026-10-09"; break;
            case InboxOp.DeleteTemplate: o.TemplateName = "Breakfast"; break;
            case InboxOp.LogWeight: o.WeightKg = 81; o.Date = "2026-10-08"; break;
            case InboxOp.SetGoals: o.GoalsPatch = new GoalsPatch { Activity = ActivityLevel.Moderate, Units = UnitSystem.Metric }; break;
            case InboxOp.SetDays: o.Days = new() { ["2026-10-09"] = new() { new ScheduleEntry { ItemName = "Whole wheat toast", Servings = 2 } } }; break;
        }
    });

    [Theory]
    [MemberData(nameof(EditOps))]
    public void Each_edit_survives_the_inbox_file_and_the_servers_preview_is_what_the_app_ends_up_with(string kind)
    {
        var main = WithHistory();
        var op = Sample(kind);
        var file = new InboxFile(Inbox.FileNameFor(op), Inbox.Serialize(op));

        var preview = Inbox.View(main, new[] { file }, out var pending);
        Assert.Single(pending);

        var app = AppDataJson.Clone(main);
        var result = Inbox.Ingest(app, new[] { file });
        Assert.Empty(result.Rejected);
        Assert.Single(result.Applied);
        app.ProcessedOpIds.Clear();
        Assert.NotEqual(AppDataJson.Serialize(main), AppDataJson.Serialize(app)); // it did something
        Assert.Equal(AppDataJson.Serialize(preview), AppDataJson.Serialize(app));
    }

    [Theory]
    [MemberData(nameof(EditOps))]
    public void An_app_too_old_for_an_edit_leaves_it_in_the_inbox(string kind)
    {
        Assert.False(Inbox.AppUnderstands(InboxTests.Menu(), kind)); // an older app advertises no new kinds
    }
}
