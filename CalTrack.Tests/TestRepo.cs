namespace CalTrack.Tests;

/// <summary>Files from the repository itself, for tests that check code and its contracts agree.</summary>
internal static class TestRepo
{
    private static readonly Lazy<string> Root = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CalorieTracker.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found above " + AppContext.BaseDirectory);
    });

    public static string Read(params string[] pathFromRoot) =>
        File.ReadAllText(Path.Combine(new[] { Root.Value }.Concat(pathFromRoot).ToArray()));
}
