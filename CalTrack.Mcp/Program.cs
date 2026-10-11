using CalorieTracker.Sync;
using CalTrack.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// CalTrack MCP server (stdio). The MCP client launches this process; stdout carries the
// protocol, so all logging goes to stderr.
//
//   caltrack-mcp --data-dir "C:\Users\me\Documents\CalTrack"
//   (or set CALTRACK_DATA_DIR)

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var dataDir = ArgValue(args, "--data-dir") ?? Environment.GetEnvironmentVariable("CALTRACK_DATA_DIR");
var store = new DataFolderStore(dataDir);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(TimeProvider.System);

// USDA lookups are asked of the CalTrack app through the data folder; the app holds the
// user's USDA key, so the server has none.
builder.Services.AddSingleton(sp => new UsdaGateway(sp.GetRequiredService<DataFolderStore>()));

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "caltrack", Version = ServerStatus.CurrentVersion })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

var app = builder.Build();

// Let the app's Settings show that Claude is set up, and with which version. Best effort:
// a missing or misconfigured folder is reported by the tools themselves, in words the model
// can pass on.
if (Directory.Exists(dataDir))
{
    try
    {
        store.WriteStatus(new ServerStatus { Version = ServerStatus.CurrentVersion, StartedUtc = DateTime.UtcNow });
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        app.Services.GetRequiredService<ILogger<DataFolderStore>>().LogWarning(ex, "Couldn't write {File}", ServerStatus.FileName);
    }
}

await app.RunAsync();

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
