using System.Text.Json;
using CalorieTracker.Data;
using CalorieTracker.Models;
using CalorieTracker.Sync;
using CalTrack.Mcp;
using ModelContextProtocol;

namespace CalTrack.Tests;

/// <summary>The MCP server against a real temp folder, including the app's side of the protocol.</summary>
public sealed class McpServerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("caltrack-test-").FullName;
    private readonly DataFolderStore _store;
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 10, 7, 9, 30, 0, TimeSpan.FromHours(-4)));
    private string MainPath => Path.Combine(_dir, Inbox.MainFileName);
    private string InboxDir => Path.Combine(_dir, Inbox.DirName);

    public McpServerTests()
    {
        _store = new DataFolderStore(_dir);
        WriteMain(InboxTests.Menu());
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void WriteMain(AppData data) => System.IO.File.WriteAllText(MainPath, AppDataJson.Serialize(data, indented: true));

    private AppData ReadMain() => AppDataJson.Parse(System.IO.File.ReadAllText(MainPath));

    /// <summary>What the app does on each tick: ingest, save main, then delete processed files.</summary>
    private void AppIngest()
    {
        var data = ReadMain();
        var names = Directory.Exists(InboxDir)
            ? Directory.GetFiles(InboxDir).Select(Path.GetFileName).ToList()
            : new List<string?>();
        var files = names.Where(n => Inbox.IsOpFileName(n!))
            .Select(n => new InboxFile(n!, System.IO.File.ReadAllText(Path.Combine(InboxDir, n!)))).ToList();
        var r = Inbox.Ingest(data, files);
        if (r.DataChanged) WriteMain(data);
        foreach (var name in r.FilesToDelete) System.IO.File.Delete(Path.Combine(InboxDir, name));
    }

    [Fact]
    public void Unknown_name_is_refused_with_real_menu_suggestions_and_nothing_is_written()
    {
        var ex = Assert.Throws<McpException>(() => CalTrackTools.LogFood(_store, _clock, "eggs", 2));

        Assert.Contains("\"Large egg\"", ex.Message);
        Assert.Contains("nothing was logged", ex.Message);
        Assert.False(Directory.Exists(InboxDir) && Directory.EnumerateFiles(InboxDir).Any());
    }

    [Fact]
    public void LogFood_queues_one_atomic_op_and_never_touches_the_main_file()
    {
        var mainBefore = System.IO.File.ReadAllText(MainPath);

        var json = CalTrackTools.LogFood(_store, _clock, "large egg", 2);

        Assert.Equal(mainBefore, System.IO.File.ReadAllText(MainPath));
        var file = Assert.Single(Directory.GetFiles(InboxDir));
        Assert.False(Path.GetFileName(file).StartsWith('.')); // temp file was renamed into place
        var op = Inbox.TryParse(System.IO.File.ReadAllText(file))!;
        Assert.Equal("Large egg", op.ItemName); // canonical name
        Assert.Equal(72, op.ItemSnapshot!.Calories);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("queued", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(144, doc.RootElement.GetProperty("logged").GetProperty("calories").GetDouble());
        Assert.Equal(144, doc.RootElement.GetProperty("day").GetProperty("totals").GetProperty("calories").GetDouble());
    }

    [Fact]
    public void Dates_are_the_users_local_day_not_utc()
    {
        // 2026-10-07 09:30 at UTC-4; "yesterday" is the 6th whatever UTC says.
        CalTrackTools.LogFood(_store, _clock, "Large egg", 1, "yesterday");
        var op = Inbox.TryParse(System.IO.File.ReadAllText(Directory.GetFiles(InboxDir).Single()))!;
        Assert.Equal("2026-10-06", op.Date);
    }

    [Fact]
    public void AdHoc_for_a_food_on_the_menu_is_refused()
    {
        var ex = Assert.Throws<McpException>(() => CalTrackTools.LogAdHoc(_store, _clock, "whole WHEAT toast", 120));
        Assert.Contains("use log_food", ex.Message);
    }

    [Fact]
    public void End_to_end_the_app_ingests_what_the_server_reported()
    {
        var reported = JsonDocument.Parse(CalTrackTools.LogFood(_store, _clock, "Large egg", 2))
            .RootElement.GetProperty("day").GetProperty("totals").GetProperty("calories").GetDouble();
        CalTrackTools.LogAdHoc(_store, _clock, "Gas station burrito", 450);

        AppIngest();

        var main = ReadMain();
        var day = new DateOnly(2026, 10, 7);
        Assert.Equal(reported + 450, main.TotalsForDay(day).Calories);
        Assert.Empty(Directory.GetFiles(InboxDir));
        using var view = JsonDocument.Parse(CalTrackTools.GetDay(_store, _clock));
        Assert.False(view.RootElement.TryGetProperty("pendingOps", out _)); // nothing left pending
    }

    /// <summary>
    /// The race the inbox exists for. With a shared JSON file, an MCP write landing between
    /// the app's read and its write is silently lost. Here the app lists the inbox, the MCP
    /// server writes during the app's save, and the late op must survive to the next round.
    /// </summary>
    [Fact]
    public void An_op_written_while_the_app_is_saving_is_not_lost()
    {
        CalTrackTools.LogFood(_store, _clock, "Large egg", 1);

        // App round 1, interleaved with a second MCP write.
        var data = ReadMain();
        var listed = Directory.GetFiles(InboxDir).Select(p => new InboxFile(Path.GetFileName(p), System.IO.File.ReadAllText(p))).ToList();
        var r = Inbox.Ingest(data, listed);
        CalTrackTools.LogFood(_store, _clock, "Whole wheat toast", 1); // lands mid-save
        WriteMain(data);
        foreach (var name in r.FilesToDelete) System.IO.File.Delete(Path.Combine(InboxDir, name));

        AppIngest(); // round 2

        var names = ReadMain().GetDay(new DateOnly(2026, 10, 7)).Select(e => e.ItemName).ToList();
        Assert.Equal(new[] { "Large egg", "Whole wheat toast" }, names);
    }

    [Fact]
    public void Missing_or_wrong_folder_gives_an_actionable_error()
    {
        Assert.Contains("CALTRACK_DATA_DIR", Assert.Throws<McpException>(() => new DataFolderStore(null).Read()).Message);
        Assert.Contains("doesn't exist", Assert.Throws<McpException>(() => new DataFolderStore(Path.Combine(_dir, "nope")).Read()).Message);
    }

    [Theory]
    [InlineData("eggs", "Large egg")]
    [InlineData("toast", "Whole wheat toast")]
    [InlineData("overnite oats", "Overnight oats")]
    public void Suggestions_find_the_intended_menu_item(string query, string expected)
    {
        var names = InboxTests.Menu().AllMenuItems().Select(i => i.Name);
        Assert.Equal(expected, NameMatcher.Closest(query, names, 3).FirstOrDefault());
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone { get; } =
            TimeZoneInfo.CreateCustomTimeZone("test", now.Offset, "test", "test");
    }
}
