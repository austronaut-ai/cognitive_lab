using System.Text.Json;
using CognitiveRuntime.Core.Contracts;

namespace CognitiveRuntime.Tests;

internal sealed class TestWorkspace : IDisposable
{
    public TestWorkspace()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "CognitiveRuntime.Tests",
            Guid.NewGuid().ToString("N"));
        ModesRoot = Path.Combine(Root, "modes");
        OutputRoot = Path.Combine(Root, "outputs");

        Directory.CreateDirectory(ModesRoot);
        Directory.CreateDirectory(OutputRoot);
    }

    public string Root { get; }

    public string ModesRoot { get; }

    public string OutputRoot { get; }

    public string CreateMode(
        string name = "frame",
        IReadOnlyList<string>? requiredHeadings = null,
        IReadOnlyList<ModePhaseManifest>? phases = null)
    {
        requiredHeadings ??=
            ["## Problem", "## Objective", "## Constraints", "## Unknowns", "## Next Actions"];

        var modeDirectory = Path.Combine(ModesRoot, name);
        var promptsDirectory = Path.Combine(modeDirectory, "prompts");
        Directory.CreateDirectory(promptsDirectory);

        File.WriteAllText(
            Path.Combine(promptsDirectory, "main.md"),
            "Produce the declared output contract.");
        File.WriteAllText(
            Path.Combine(promptsDirectory, "critic.md"),
            "Critique the previous output.");
        File.WriteAllText(
            Path.Combine(promptsDirectory, "revision.md"),
            "Revise the draft using the critic while preserving the output contract.");

        var manifest = new ModeManifest
        {
            Name = name,
            Description = "Test mode.",
            Version = 1,
            Phases = phases?.ToList() ??
                [
                    new ModePhaseManifest
                    {
                        Name = "main",
                        Kind = PhaseKind.Main,
                        Prompt = "prompts/main.md"
                    },
                    new ModePhaseManifest
                    {
                        Name = "critic",
                        Kind = PhaseKind.Critic,
                        Prompt = "prompts/critic.md"
                    },
                    new ModePhaseManifest
                    {
                        Name = "revision",
                        Kind = PhaseKind.Revision,
                        Prompt = "prompts/revision.md"
                    }
                ],
            OutputContract = new OutputContract
            {
                RequiredHeadings = [.. requiredHeadings],
                MinimumLength = 100
            }
        };

        File.WriteAllText(Path.Combine(modeDirectory, "MODE.md"), DescribeMode(name, manifest));
        File.WriteAllText(
            Path.Combine(modeDirectory, "mode.json"),
            JsonSerializer.Serialize(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
                {
                    WriteIndented = true,
                    Converters =
                    {
                        new System.Text.Json.Serialization.JsonStringEnumConverter(
                            JsonNamingPolicy.CamelCase)
                    }
                }));

        return modeDirectory;
    }

    /// <summary>
    /// Writes documentation that satisfies the loader's MODE.md rules for <paramref name="manifest"/>: every
    /// phase name and every heading of the resolved revision contract. Tolerates deliberately invalid
    /// manifests so their own diagnostics still surface.
    /// </summary>
    public static string DescribeMode(string name, ModeManifest manifest)
    {
        var phases = (manifest.Phases ?? []).Where(phase => phase is not null).ToList();
        var revisionContract = phases.FirstOrDefault(phase => phase.Kind == PhaseKind.Revision)?.OutputContract
            ?? manifest.OutputContract;
        var headings = revisionContract?.RequiredHeadings ?? [];
        var lines = new List<string> { $"# {name}", string.Empty, "Phases:" };
        lines.AddRange(phases.Select(phase => $"- {phase.Name}"));
        lines.AddRange([string.Empty, "## Output", string.Empty, "```text"]);
        lines.AddRange(headings.Where(heading => heading is not null));
        lines.Add("```");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed class FixedTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow;

    public FixedTimeProvider(DateTimeOffset utcNow)
    {
        _utcNow = utcNow;
    }

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _utcNow += duration;
    }
}
