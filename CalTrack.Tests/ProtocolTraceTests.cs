using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using CalorieTracker.Data;
using CalorieTracker.Sync;
using CalTrack.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CalTrack.Tests;

/// <summary>
/// The opt-in protocol trace: it must record exactly the bytes on the wire, one message per
/// line, without changing what the client and server exchange.
/// </summary>
public sealed class ProtocolTraceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("caltrack-trace-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>The trace so far, read the way an editor would while the server still has it open.</summary>
    private static string[] Lines(ProtocolTrace trace)
    {
        using var file = new FileStream(trace.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.UTF8);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("${user_config.trace_protocol}", false)] // a client that didn't substitute it
    public void Only_an_explicit_true_turns_it_on(string? value, bool on) => Assert.Equal(on, ProtocolTrace.IsOn(value));

    [Fact]
    public void Messages_split_across_reads_and_writes_are_recorded_whole_and_passed_through_untouched()
    {
        // "é" is two bytes in UTF-8; a 3-byte chunking splits it, and a CRLF line ending.
        var wire = "{\"name\":\"Crème brûlée\"}\r\n{\"id\":2}\n\n{\"id\":3}\n"u8.ToArray();
        using var trace = ProtocolTrace.Start(_dir);

        using var incoming = trace.Incoming(new TrickleStream(wire, chunk: 3));
        var read = new MemoryStream();
        var buffer = new byte[3];
        int n;
        while ((n = incoming.Read(buffer, 0, buffer.Length)) > 0) read.Write(buffer, 0, n);
        Assert.Equal(wire, read.ToArray());

        var written = new MemoryStream();
        using (var outgoing = trace.Outgoing(written))
            foreach (var chunk in wire.Chunk(5)) outgoing.Write(chunk);
        Assert.Equal(wire, written.ToArray());

        var lines = Lines(trace).Select(l => l[(l.IndexOf(' ') + 1)..]).ToArray(); // drop the timestamp
        Assert.Equal(new[]
        {
            "C->S {\"name\":\"Crème brûlée\"}", "C->S {\"id\":2}", "C->S {\"id\":3}",
            "S->C {\"name\":\"Crème brûlée\"}", "S->C {\"id\":2}", "S->C {\"id\":3}",
        }, lines);
        Assert.Equal(Path.Combine(_dir, ProtocolTrace.FolderName), Path.GetDirectoryName(trace.FilePath));
    }

    [Fact]
    public async Task A_real_session_over_the_traced_streams_is_recorded_as_sent()
    {
        File.WriteAllText(Path.Combine(_dir, Inbox.MainFileName), AppDataJson.Serialize(InboxTests.Menu(), indented: true));
        using var trace = ProtocolTrace.Start(_dir);

        // Two one-way pipes stand in for the process's stdin and stdout.
        using var toServer = new AnonymousPipeServerStream(PipeDirection.Out);
        using var serverIn = new AnonymousPipeClientStream(PipeDirection.In, toServer.ClientSafePipeHandle);
        using var fromServer = new AnonymousPipeServerStream(PipeDirection.In);
        using var serverOut = new AnonymousPipeClientStream(PipeDirection.Out, fromServer.ClientSafePipeHandle);

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        var store = new DataFolderStore(_dir);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(new UsdaGateway(store));
        builder.Services
            .AddMcpServer(ServerSetup.Configure)
            .WithStreamServerTransport(trace.Incoming(serverIn), trace.Outgoing(serverOut))
            .WithToolsFromAssembly(typeof(CalTrackTools).Assembly)
            .WithRequestFilters(ServerSetup.Filters);
        using var host = builder.Build();
        await host.StartAsync();

        var send = new StreamWriter(toServer, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var receive = new StreamReader(fromServer, Encoding.UTF8);
        var sent = new[]
        {
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_menu_items","arguments":{"search":"egg"}}}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"log_food","arguments":{"servings":1}}}""",
        };
        var received = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await send.WriteLineAsync(sent[0]);
        received.Add((await receive.ReadLineAsync(timeout.Token))!);
        await send.WriteLineAsync(sent[1]);
        await send.WriteLineAsync(sent[2]);
        received.Add((await receive.ReadLineAsync(timeout.Token))!);
        await send.WriteLineAsync(sent[3]);
        received.Add((await receive.ReadLineAsync(timeout.Token))!);
        await host.StopAsync(timeout.Token);

        var init = JsonDocument.Parse(received[0]).RootElement.GetProperty("result");
        Assert.Equal("2025-11-25", init.GetProperty("protocolVersion").GetString());
        // The workflow rules reach the model even before it loads a single tool.
        Assert.Equal(ServerSetup.Instructions, init.GetProperty("instructions").GetString());
        Assert.Contains("Large egg", received[1]);
        // A missing required argument is named, not hidden behind the SDK's generic error.
        var missing = JsonDocument.Parse(received[2]).RootElement.GetProperty("result");
        Assert.True(missing.GetProperty("isError").GetBoolean());
        Assert.StartsWith("log_food needs itemName (Exact menu item", missing.GetProperty("content")[0].GetProperty("text").GetString());

        var lines = Lines(trace);
        Assert.Equal(
            new[] { "C->S " + sent[0], "S->C " + received[0], "C->S " + sent[1], "C->S " + sent[2], "S->C " + received[1], "C->S " + sent[3], "S->C " + received[2] },
            lines.Select(l => l[(l.IndexOf(' ') + 1)..]));
        Assert.All(lines, l => Assert.True(DateTime.TryParse(l[..l.IndexOf(' ')], out _), l));
    }

    /// <summary>A read-only stream that hands out at most <paramref name="chunk"/> bytes per read, like a pipe.</summary>
    private sealed class TrickleStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, chunk));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, chunk)]);
    }
}
