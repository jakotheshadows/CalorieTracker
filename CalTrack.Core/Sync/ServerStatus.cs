using System.Text.Json;

namespace CalorieTracker.Sync;

/// <summary>
/// What the MCP server says about itself in the data folder (<see cref="FileName"/>), written
/// each time Claude starts it. The app reads it to show whether Claude is set up, and compares
/// it with <see cref="CurrentVersion"/> — the server built alongside this app and offered as
/// its download — to offer the update.
/// </summary>
public sealed class ServerStatus
{
    /// <summary>
    /// The server version this build ships. The Claude Desktop bundle's manifest
    /// (CalTrack.Mcp/mcpb/manifest.json) carries the same number; a test keeps them equal.
    /// </summary>
    public const string CurrentVersion = "0.6.1";

    public const string FileName = "caltrack-mcp.json";

    public string Version { get; set; } = "";
    public DateTime StartedUtc { get; set; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Serialize(ServerStatus s) => JsonSerializer.Serialize(s, Json);

    public static ServerStatus? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<ServerStatus>(text, Json);
            return s is null || string.IsNullOrWhiteSpace(s.Version) ? null : s;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when an installed server is older than the one this app offers.</summary>
    public static bool IsOutdated(string installedVersion) =>
        System.Version.TryParse(installedVersion, out var installed) &&
        System.Version.TryParse(CurrentVersion, out var current) &&
        installed < current;
}
