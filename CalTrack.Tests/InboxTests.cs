using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;

namespace CalTrack.Tests;

/// <summary>The data-folder protocol shared by the app and the MCP server.</summary>
public class InboxTests
{
    private static readonly DateOnly Day = new(2026, 10, 7);

    internal static AppData Menu() => new()
    {
        Items =
        {
            new FoodItem { Name = "Large egg", Calories = 72, Nutrients = { ["protein"] = 6.3 } },
            new FoodItem { Name = "Whole wheat toast", Calories = 80 },
        },
        Recipes =
        {
            new Recipe
            {
                Name = "Overnight oats", Servings = 2,
                Ingredients = { new RecipeIngredient { Name = "Oats", Amount = 100, Unit = "g", CaloriesPer100 = 380 } },
            },
        },
    };

    private static int seq;

    internal static InboxOp Food(string name, double servings, string date = "2026-10-07", FoodItem? snapshot = null) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CreatedUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(++seq),
        Kind = InboxOp.LogFood,
        Date = date,
        Servings = servings,
        ItemName = name,
        ItemSnapshot = snapshot,
        Source = "mcp",
    };

    internal static InboxOp AdHoc(string name, double calories, double servings = 1) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        CreatedUtc = new DateTime(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc).AddMilliseconds(++seq),
        Kind = InboxOp.LogAdHoc,
        Date = "2026-10-07",
        Servings = servings,
        AdHoc = new FoodItem { Name = name, Calories = calories },
    };

    internal static InboxFile File(InboxOp op) => new(Inbox.FileNameFor(op), Inbox.Serialize(op));

    [Fact]
    public void LogFood_merges_into_existing_entry_with_canonical_name()
    {
        var data = Menu();
        data.AddEntry(Day, "Large egg", 1);

        var (summary, error) = Inbox.Apply(data, Food("LARGE EGG", 2));

        Assert.Null(error);
        Assert.NotNull(summary);
        var entry = Assert.Single(data.GetDay(Day));
        Assert.Equal("Large egg", entry.ItemName);
        Assert.Equal(3, entry.Servings);
    }

    [Fact]
    public void LogFood_resolves_recipes_per_serving()
    {
        var data = Menu();
        Inbox.Apply(data, Food("overnight oats", 1));
        Assert.Equal(190, data.TotalsForDay(Day).Calories); // 380 kcal recipe / 2 servings
    }

    [Fact]
    public void LogFood_for_an_item_deleted_since_falls_back_to_the_snapshot_as_a_one_off()
    {
        var data = Menu();
        var op = Food("Large egg", 2, snapshot: data.FindItem("Large egg")!.Clone());
        data.Items.RemoveAll(i => i.Name == "Large egg"); // user deleted it before the app ingested

        var (summary, error) = Inbox.Apply(data, op);

        Assert.Null(error);
        Assert.Contains("one-off", summary);
        var entry = Assert.Single(data.GetDay(Day));
        Assert.NotNull(entry.AdHoc);
        Assert.Equal(144, data.TotalsForDay(Day).Calories); // the food and its numbers survive
    }

    [Theory]
    [InlineData("10/07/2026", 1)]
    [InlineData("2026-10-07", 0)]
    [InlineData("2026-10-07", -1)]
    [InlineData("2026-10-07", double.NaN)]
    public void Invalid_ops_are_rejected_without_touching_data(string date, double servings)
    {
        var data = Menu();
        var before = AppDataJson.Serialize(data);

        var (summary, error) = Inbox.Apply(data, Food("Large egg", servings, date));

        Assert.Null(summary);
        Assert.NotNull(error);
        Assert.Equal(before, AppDataJson.Serialize(data));
    }

    [Fact]
    public void Ingest_applies_in_creation_order_and_marks_files_for_deletion()
    {
        var data = Menu();
        var first = Food("Large egg", 1);
        var second = AdHoc("Gas station burrito", 450);

        var r = Inbox.Ingest(data, new[] { File(second), File(first) }); // listing order is arbitrary

        Assert.True(r.DataChanged);
        Assert.Equal(2, r.Applied.Count);
        Assert.Contains("Large egg", r.Applied[0]);
        Assert.Equal(new[] { "Large egg", "Gas station burrito" }, data.GetDay(Day).Select(e => e.ItemName));
        Assert.Equal(2, r.FilesToDelete.Count);
    }

    [Fact]
    public void Crash_between_save_and_delete_never_applies_an_op_twice()
    {
        var data = Menu();
        var op = Food("Large egg", 2);
        var files = new[] { File(op) };

        Inbox.Ingest(data, files);
        // ...main file saved, then the app crashed before deleting the op file.
        var saved = AppDataJson.Parse(AppDataJson.Serialize(data));
        var again = Inbox.Ingest(saved, files);

        Assert.Empty(again.Applied);
        Assert.Equal(2, Assert.Single(saved.GetDay(Day)).Servings);
        Assert.Equal(files.Select(f => f.Name), again.FilesToDelete); // the leftover file now gets deleted
    }

    [Fact]
    public void Processed_ids_are_forgotten_once_their_files_are_gone()
    {
        var data = Menu();
        var op = Food("Large egg", 1);
        Inbox.Ingest(data, new[] { File(op) });
        Assert.Contains(op.Id, data.ProcessedOpIds);

        var r = Inbox.Ingest(data, Array.Empty<InboxFile>()); // files were deleted after the save

        Assert.True(r.DataChanged);
        Assert.Empty(data.ProcessedOpIds);
        Assert.Single(data.GetDay(Day)); // forgetting the id doesn't undo the entry
    }

    [Fact]
    public void Unreadable_and_unfinished_files_are_left_alone()
    {
        var data = Menu();
        var r = Inbox.Ingest(data, new[]
        {
            new InboxFile("20261007T120000000-bad.json", "{ not json"),
            new InboxFile(".abc.tmp", Inbox.Serialize(Food("Large egg", 1))), // still being written
        });

        Assert.Equal(new[] { "20261007T120000000-bad.json" }, r.Unreadable);
        Assert.Empty(r.FilesToDelete);
        Assert.Empty(data.GetDay(Day));
    }

    /// <summary>
    /// THE invariant: what the MCP server shows the model (main + pending ops) is exactly
    /// what the app will show once it ingests. If these ever differ, the model reports a
    /// day total the user will never see.
    /// </summary>
    [Fact]
    public void Mcp_view_equals_what_the_app_ends_up_with()
    {
        var main = Menu();
        main.AddEntry(Day, "Whole wheat toast", 1);
        var already = Food("Whole wheat toast", 1);
        Inbox.Ingest(main, new[] { File(already) }); // processed, file not yet deleted

        var files = new[]
        {
            File(already),
            File(Food("large egg", 2)),
            File(Food("Large egg", 1)),
            File(AdHoc("Gas station burrito", 450)),
            File(Food("Overnight oats", 0.5)),
            File(Food("Not on menu", 1)), // rejected identically on both sides
        };

        var view = Inbox.View(main, files, out var pending);
        var app = AppDataJson.Clone(main);
        Inbox.Ingest(app, files);

        Assert.Equal(5, pending.Count);
        Assert.Equal(
            AppDataJson.Serialize(new AppData { Days = view.Days }),
            AppDataJson.Serialize(new AppData { Days = app.Days }));
        Assert.Equal(view.TotalsForDay(Day).Calories, app.TotalsForDay(Day).Calories);
    }

    [Fact]
    public void Data_saved_before_the_inbox_existed_still_loads()
    {
        const string legacy = """{"SchemaVersion":1,"Items":[{"Name":"Apple","Category":2,"Calories":95,"Nutrients":{}}],"Days":{"2026-10-07":[{"ItemName":"Apple","Servings":1,"AdHoc":null}]}}""";
        var data = AppDataJson.Parse(legacy);
        Assert.Empty(data.ProcessedOpIds);
        Assert.Equal(95, data.TotalsForDay(Day).Calories);
        Assert.Equal(FoodCategory.Snack, data.Items[0].Category);
    }
}
