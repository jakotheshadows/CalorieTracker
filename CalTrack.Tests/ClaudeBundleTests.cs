using System.Reflection;
using System.Text.Json;
using CalorieTracker.Sync;
using CalTrack.Mcp;
using ModelContextProtocol.Server;

namespace CalTrack.Tests;

/// <summary>
/// CalTrack for Claude: the Claude Desktop bundle's manifest must describe the server that's
/// actually built, and the status file the server leaves must be what the app can read.
/// </summary>
public sealed class ClaudeBundleTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("caltrack-bundle-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static JsonElement Manifest() =>
        JsonDocument.Parse(TestRepo.Read("CalTrack.Mcp", "mcpb", "manifest.json")).RootElement;

    [Fact]
    public void The_manifest_carries_the_server_version_the_app_offers()
    {
        // The app compares the installed server's status with CurrentVersion to offer updates;
        // a bundle labelled differently would never stop asking (or never ask).
        Assert.Equal(ServerStatus.CurrentVersion, Manifest().GetProperty("version").GetString());
    }

    [Fact]
    public void The_manifest_lists_exactly_the_servers_tools()
    {
        var listed = Manifest().GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order();
        var actual = typeof(CalTrackTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .Order();
        Assert.Equal(actual, listed);
    }

    [Fact]
    public void The_manifest_runs_the_bundled_exe_on_the_folder_the_user_picks()
    {
        var m = Manifest();
        var server = m.GetProperty("server");
        var config = server.GetProperty("mcp_config");

        Assert.Equal("binary", server.GetProperty("type").GetString());
        Assert.Equal("${__dirname}/" + server.GetProperty("entry_point").GetString(), config.GetProperty("command").GetString());
        Assert.Equal(
            new[] { "--data-dir", "${user_config.data_dir}", "--trace-protocol", "${user_config.trace_protocol}" },
            config.GetProperty("args").EnumerateArray().Select(a => a.GetString()));

        var folder = m.GetProperty("user_config").GetProperty("data_dir");
        Assert.Equal("directory", folder.GetProperty("type").GetString());
        Assert.True(folder.GetProperty("required").GetBoolean());

        // Recording the traffic is opt-in: it holds what the user logs.
        var trace = m.GetProperty("user_config").GetProperty("trace_protocol");
        Assert.Equal("boolean", trace.GetProperty("type").GetString());
        Assert.False(trace.GetProperty("default").GetBoolean());
        Assert.False(trace.GetProperty("required").GetBoolean());

        // The download is offered on Windows only, and Claude Desktop enforces the same.
        Assert.Equal(new[] { "win32" }, m.GetProperty("compatibility").GetProperty("platforms").EnumerateArray().Select(p => p.GetString()));
    }

    [Fact]
    public void The_status_the_server_writes_is_what_the_app_reads()
    {
        var store = new DataFolderStore(_dir);
        store.WriteStatus(new ServerStatus { Version = "0.4.0", StartedUtc = DateTime.UtcNow });
        store.WriteStatus(new ServerStatus { Version = ServerStatus.CurrentVersion, StartedUtc = DateTime.UtcNow }); // replaces

        var read = ServerStatus.TryParse(File.ReadAllText(Path.Combine(_dir, ServerStatus.FileName)));
        Assert.Equal(ServerStatus.CurrentVersion, read!.Version);
        Assert.Single(Directory.GetFiles(_dir)); // no temp files left behind
    }

    [Theory]
    [InlineData("0.4.0", true)]
    [InlineData(ServerStatus.CurrentVersion, false)]
    [InlineData("99.0.0", false)]  // newer than this app: nothing to offer
    [InlineData("garbage", false)] // unreadable: don't nag
    public void Only_an_older_installed_server_is_offered_the_update(string installed, bool outdated) =>
        Assert.Equal(outdated, ServerStatus.IsOutdated(installed));

    [Fact]
    public void A_missing_or_damaged_status_reads_as_not_set_up()
    {
        Assert.Null(ServerStatus.TryParse(null));
        Assert.Null(ServerStatus.TryParse("{\"Version\":"));
        Assert.Null(ServerStatus.TryParse("{}"));
    }
}
