using System.Text.Json;
using CalorieTracker.Models;

namespace CalorieTracker.Data;

/// <summary>
/// The one JSON shape of <see cref="AppData"/> — used for browser storage, exports, the
/// connected data folder, and the MCP server. Default System.Text.Json conventions
/// (PascalCase, numeric enums) are what every existing export and localStorage blob
/// already uses, so they must not change.
/// </summary>
public static class AppDataJson
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Parse stored data; blank input is an empty dataset, malformed input throws.</summary>
    public static AppData Parse(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new AppData()
            : JsonSerializer.Deserialize<AppData>(json, Compact) ?? new AppData();

    /// <summary>Compact form for browser storage; indented for files a person may open.</summary>
    public static string Serialize(AppData data, bool indented = false) =>
        JsonSerializer.Serialize(data, indented ? Indented : Compact);

    /// <summary>Deep copy via the persisted shape — guarantees a copy carries exactly what a save would.</summary>
    public static AppData Clone(AppData data) => Parse(Serialize(data));
}
