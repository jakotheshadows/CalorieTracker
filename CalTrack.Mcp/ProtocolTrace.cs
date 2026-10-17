using System.Globalization;
using System.Text;

namespace CalTrack.Mcp;

/// <summary>
/// Opt-in record of the protocol traffic: every JSON-RPC message between the MCP client and
/// this server, exactly as sent, one per line — <c>time direction message</c> — in
/// <c>mcp-trace/&lt;start time&gt;-&lt;process id&gt;.log</c> in the data folder. For seeing what
/// a client really sends, and for troubleshooting. Off unless asked for (Claude Desktop's
/// "Record protocol traffic" setting passes <c>--trace-protocol true</c>): the messages carry
/// what the user logs.
/// </summary>
/// <remarks>
/// Between the messages are lines of the server's own, direction <see cref="UsdaLookup"/>: one
/// per USDA fetch it asks the app for (<see cref="UsdaGateway"/>), since those never cross
/// the protocol. A tool result shows only how a lookup ended; these show each attempt.
/// </remarks>
/// <remarks>
/// It taps the transport's streams rather than the SDK's messages, so what it records is the
/// bytes on the wire, not a re-serialization. A failure to write the trace never reaches the
/// protocol: the bytes pass through first.
/// </remarks>
public sealed class ProtocolTrace : IDisposable
{
    public const string FolderName = "mcp-trace";
    public const string ClientToServer = "C->S";
    public const string ServerToClient = "S->C";
    public const string UsdaLookup = "USDA";

    private readonly StreamWriter _writer;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();

    private ProtocolTrace(string path, StreamWriter writer, TimeProvider time) =>
        (FilePath, _writer, _time) = (path, writer, time);

    public string FilePath { get; }

    /// <summary>Whether a <c>--trace-protocol</c> value turns tracing on. Claude Desktop passes booleans as "true"/"false".</summary>
    public static bool IsOn(string? value) => bool.TryParse(value?.Trim(), out var on) && on;

    /// <summary>Start a new trace file in <paramref name="dataDir"/>'s <see cref="FolderName"/> folder.</summary>
    public static ProtocolTrace Start(string dataDir, TimeProvider? time = null)
    {
        time ??= TimeProvider.System;
        var folder = Directory.CreateDirectory(Path.Combine(dataDir, FolderName)).FullName;
        // One file per process: Claude Desktop runs a server per connection, and they'd interleave.
        var path = Path.Combine(folder, $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
        var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        return new ProtocolTrace(path, new StreamWriter(file, new UTF8Encoding(false)) { AutoFlush = true }, time);
    }

    /// <summary>The client's messages: the server reads them from this stream.</summary>
    public Stream Incoming(Stream input) => new Tap(input, this, ClientToServer);

    /// <summary>The server's messages: the server writes them to this stream.</summary>
    public Stream Outgoing(Stream output) => new Tap(output, this, ServerToClient);

    /// <summary>One USDA fetch made for this server: a JSON object on one line (see <see cref="UsdaGateway"/>).</summary>
    public void RecordUsda(string json) => Record(UsdaLookup, json);

    private void Record(string direction, ReadOnlySpan<byte> line) => Record(direction, Encoding.UTF8.GetString(line));

    private void Record(string direction, string text)
    {
        text = text.TrimEnd('\r');
        if (text.Length == 0) return;
        var stamp = _time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        lock (_gate)
        {
            try { _writer.WriteLine($"{stamp} {direction} {text}"); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { } // the trace is best effort
        }
    }

    public void Dispose()
    {
        lock (_gate) _writer.Dispose();
    }

    /// <summary>
    /// Passes bytes through untouched and cuts a copy into lines — over stdio each JSON-RPC
    /// message is one line. A line is decoded only once complete, so a character split across
    /// two reads survives.
    /// </summary>
    private sealed class Tap(Stream inner, ProtocolTrace trace, string direction) : Stream
    {
        private readonly MemoryStream _line = new();

        private void See(ReadOnlySpan<byte> bytes)
        {
            lock (_line)
            {
                while (!bytes.IsEmpty)
                {
                    var end = bytes.IndexOf((byte)'\n');
                    if (end < 0) { _line.Write(bytes); return; }
                    _line.Write(bytes[..end]);
                    trace.Record(direction, _line.GetBuffer().AsSpan(0, (int)_line.Length));
                    _line.SetLength(0);
                    bytes = bytes[(end + 1)..];
                }
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var n = inner.Read(buffer);
            See(buffer[..n]);
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            // Console stdin ignores cancellation; stop waiting on it when asked, as the SDK's own
            // stdio transport does, so the server can shut down.
            var n = await inner.ReadAsync(buffer, ct).AsTask().WaitAsync(ct);
            See(buffer.Span[..n]);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            inner.Write(buffer);
            See(buffer);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await inner.WriteAsync(buffer, ct);
            See(buffer.Span);
        }

        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override bool CanRead => inner.CanRead;
        public override bool CanWrite => inner.CanWrite;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
