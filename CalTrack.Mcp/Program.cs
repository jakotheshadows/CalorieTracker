using CalorieTracker.Sync;
using CalTrack.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// CalTrack MCP server (stdio). The MCP client launches this process; stdout carries the
// protocol, so all logging goes to stderr.
//
//   caltrack-mcp --data-dir "C:\Users\me\Documents\CalTrack" [--trace-protocol true]
//   (or set CALTRACK_DATA_DIR, CALTRACK_TRACE_PROTOCOL)

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

var dataDir = ArgValue(args, "--data-dir") ?? Environment.GetEnvironmentVariable("CALTRACK_DATA_DIR");
var store = new DataFolderStore(dataDir);
builder.Services.AddSingleton(store);
builder.Services.AddSingleton(TimeProvider.System);

// Opt-in: record every protocol message, and each USDA lookup made for it, in the data
// folder's mcp-trace/ (Claude Desktop's "Record protocol traffic" setting passes --trace-protocol true).
ProtocolTrace? trace = null;
Exception? traceFailure = null;
if (ProtocolTrace.IsOn(ArgValue(args, "--trace-protocol") ?? Environment.GetEnvironmentVariable("CALTRACK_TRACE_PROTOCOL"))
    && Directory.Exists(dataDir))
{
    try { trace = ProtocolTrace.Start(dataDir); }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { traceFailure = ex; }
}

// USDA lookups are asked of the CalTrack app through the data folder; the app holds the
// user's USDA key, so the server has none.
builder.Services.AddSingleton(new UsdaGateway(store, trace: trace));

var mcp = builder.Services.AddMcpServer(ServerSetup.Configure);
if (trace is null)
    mcp.WithStdioServerTransport();
else
    mcp.WithStreamServerTransport(trace.Incoming(Console.OpenStandardInput()), trace.Outgoing(Console.OpenStandardOutput()));
mcp.WithToolsFromAssembly();
mcp.WithRequestFilters(ServerSetup.Filters);

var app = builder.Build();
var log = app.Services.GetRequiredService<ILogger<ProtocolTrace>>();
if (trace is not null) log.LogInformation("Recording protocol traffic to {File}", trace.FilePath);
if (traceFailure is not null) log.LogWarning(traceFailure, "Couldn't start the protocol trace; running without it");

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
trace?.Dispose();

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
