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
builder.Services.AddSingleton(new DataFolderStore(dataDir));
builder.Services.AddSingleton(TimeProvider.System);

builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "caltrack", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
