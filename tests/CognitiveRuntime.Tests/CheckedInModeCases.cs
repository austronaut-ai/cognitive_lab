using Xunit;

namespace CognitiveRuntime.Tests;

/// <summary>
/// Discovers every checked-in mode and prompt variant from the repository
/// <c>modes</c> directory so a new one is covered without editing tests.
/// Discovery is by directory layout only, so a broken mode still produces a
/// case and fails validation instead of silently disappearing.
/// </summary>
internal static class CheckedInModeCases
{
    private static readonly string[] PhasePrompts = ["main.md", "critic.md", "revision.md"];

    public static string ModesRoot { get; } = FindModesRoot();

    public static IReadOnlyList<(string Mode, string? Lens)> All { get; } = Discover();

    public static TheoryData<string, string?> Data()
    {
        var data = new TheoryData<string, string?>();
        foreach (var (mode, lens) in All)
        {
            data.Add(mode, lens);
        }

        return data;
    }

    public static string PromptDirectory(string mode, string? lens) =>
        lens is null
            ? Path.Combine(ModesRoot, mode, "prompts")
            : Path.Combine(ModesRoot, mode, "prompts", lens);

    private static List<(string, string?)> Discover()
    {
        var cases = new List<(string, string?)>();
        foreach (var modeDirectory in SortedDirectories(ModesRoot))
        {
            var mode = Path.GetFileName(modeDirectory);
            var prompts = Path.Combine(modeDirectory, "prompts");
            var variants = Directory.Exists(prompts)
                ? SortedDirectories(prompts).Select(Path.GetFileName).ToList()
                : [];
            var hasDefaults = PhasePrompts.Any(file => File.Exists(Path.Combine(prompts, file)));

            if (variants.Count == 0 || hasDefaults)
            {
                cases.Add((mode, null));
            }

            cases.AddRange(variants.Select(variant => (mode, (string?)variant)));
        }

        return cases;
    }

    private static string[] SortedDirectories(string path) =>
        Directory.GetDirectories(path).Order(StringComparer.Ordinal).ToArray();

    private static string FindModesRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "CognitiveRuntime.slnx")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Could not locate CognitiveRuntime.slnx.")
            : Path.Combine(directory.FullName, "modes");
    }
}
