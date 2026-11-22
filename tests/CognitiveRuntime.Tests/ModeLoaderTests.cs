using System.Text.Json.Nodes;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Exceptions;
using CognitiveRuntime.Core.Modes;

namespace CognitiveRuntime.Tests;

public sealed class ModeLoaderTests
{
    [Fact]
    public async Task LoadAsync_LoadsManifestDocumentationAndPrompts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode();
        var loader = new FileModeLoader(workspace.ModesRoot);

        var mode = await loader.LoadAsync("frame");

        Assert.Equal("frame", mode.Manifest.Name);
        Assert.Contains("# frame", mode.Documentation);
        Assert.Collection(
            mode.Phases,
            phase =>
            {
                Assert.Equal("main", phase.Name);
                Assert.Equal(PhaseKind.Main, phase.Kind);
                Assert.NotEmpty(phase.Prompt);
            },
            phase =>
            {
                Assert.Equal("critic", phase.Name);
                Assert.Equal(PhaseKind.Critic, phase.Kind);
                Assert.NotEmpty(phase.Prompt);
            },
            phase =>
            {
                Assert.Equal("revision", phase.Name);
                Assert.Equal(PhaseKind.Revision, phase.Kind);
                Assert.NotEmpty(phase.Prompt);
            });
    }

    [Theory]
    [InlineData("../frame")]
    [InlineData("frame/child")]
    [InlineData("frame\\child")]
    public async Task LoadAsync_RejectsUnsafeModeNames(string modeName)
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode();
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync(modeName));

        Assert.Contains("unsupported characters", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RequiresRevisionPromptFile()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        File.Delete(Path.Combine(modeDirectory, "prompts", "revision.md"));
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame"));

        Assert.Contains("prompts", exception.Message);
        Assert.Contains("revision.md", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsOutOfOrderRevisionPhase()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(
            phases:
            [
                CreatePhase("main", PhaseKind.Main),
                CreatePhase("revision", PhaseKind.Revision),
                CreatePhase("critic", PhaseKind.Critic)
            ]);
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame"));

        Assert.Contains("Phase 2", exception.Message);
        Assert.Contains("critic", exception.Message);
        Assert.Contains("revision", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_WithLens_LoadsLensSpecificPrompts()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        var lensDirectory = Path.Combine(modeDirectory, "prompts", "warcraft");
        Directory.CreateDirectory(lensDirectory);
        File.WriteAllText(Path.Combine(lensDirectory, "main.md"), "Lens main prompt.");
        File.WriteAllText(Path.Combine(lensDirectory, "critic.md"), "Lens critic prompt.");
        File.WriteAllText(Path.Combine(lensDirectory, "revision.md"), "Lens revision prompt.");
        var loader = new FileModeLoader(workspace.ModesRoot);

        var mode = await loader.LoadAsync("frame", "warcraft");

        Assert.Collection(
            mode.Phases,
            phase => Assert.Equal("Lens main prompt.", phase.Prompt),
            phase => Assert.Equal("Lens critic prompt.", phase.Prompt),
            phase => Assert.Equal("Lens revision prompt.", phase.Prompt));
    }

    [Fact]
    public async Task LoadAsync_WithUnknownLens_ThrowsModeLoadException()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode();
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame", "nonexistent"));

        Assert.Contains("Lens 'nonexistent'", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsUnsafeLensNames()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode();
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame", "../escape"));

        Assert.Contains("unsupported characters", exception.Message);
    }

    private static ModePhaseManifest Phase(string name, PhaseKind kind, string prompt) =>
        new() { Name = name, Kind = kind, Prompt = prompt };

    private static ModePhaseManifest[] Canonical(
        string main = "prompts/main.md",
        string critic = "prompts/critic.md",
        string revision = "prompts/revision.md") =>
    [
        Phase("main", PhaseKind.Main, main),
        Phase("critic", PhaseKind.Critic, critic),
        Phase("revision", PhaseKind.Revision, revision)
    ];

    private static async Task<ModeLoadException> LoadFailureAsync(
        TestWorkspace workspace,
        string? lens = null) =>
        await Assert.ThrowsAsync<ModeLoadException>(
            () => new FileModeLoader(workspace.ModesRoot).LoadAsync("frame", lens));

    private static bool FilesystemIsCaseSensitive(string directory)
    {
        var probe = Path.Combine(directory, "case-probe");
        Directory.CreateDirectory(probe);
        return !Directory.Exists(Path.Combine(directory, "CASE-PROBE"));
    }

    private static bool TryCreateSymlink(string link, string target, bool directory)
    {
        try
        {
            if (directory)
            {
                Directory.CreateSymbolicLink(link, target);
            }
            else
            {
                File.CreateSymbolicLink(link, target);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ma in")]
    [InlineData("main.v2")]
    [InlineData("-main")]
    [InlineData("ma/in")]
    [InlineData("ma\\in")]
    [InlineData("ma_in")]
    [InlineData("m\u00e4in")]
    public async Task LoadAsync_RejectsInvalidPhaseNames(string? name)
    {
        using var workspace = new TestWorkspace();
        var phases = Canonical();
        phases[0] = phases[0] with { Name = name! };
        workspace.CreateMode(phases: phases);

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("Phase 1 name", exception.Message);
        Assert.Contains("ASCII letters, digits, or hyphens", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_AcceptsSafeCustomPhaseNames()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(
            phases:
            [
                Phase("Draft-1", PhaseKind.Main, "prompts/main.md"),
                Phase("review", PhaseKind.Critic, "prompts/critic.md"),
                Phase("final2", PhaseKind.Revision, "prompts/revision.md")
            ]);

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame");

        Assert.Equal(["Draft-1", "review", "final2"], mode.Phases.Select(phase => phase.Name));
    }

    [Fact]
    public async Task LoadAsync_RejectsCaseInsensitiveDuplicatePhaseNames()
    {
        using var workspace = new TestWorkspace();
        var phases = Canonical();
        phases[1] = phases[1] with { Name = "MAIN" };
        workspace.CreateMode(phases: phases);

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("duplicated", exception.Message);
        Assert.Contains("case-insensitively", exception.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LoadAsync_RejectsOmittedRequiredPhase(int index)
    {
        using var workspace = new TestWorkspace();
        var phases = Canonical().ToList();
        phases.RemoveAt(index);
        workspace.CreateMode(phases: phases);

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("exactly three phases", exception.Message);
    }

    [Theory]
    [InlineData(PhaseKind.Main, 1)]
    [InlineData(PhaseKind.Critic, 2)]
    [InlineData(PhaseKind.Revision, 1)]
    public async Task LoadAsync_RejectsRepeatedRequiredPhaseKind(PhaseKind repeated, int index)
    {
        using var workspace = new TestWorkspace();
        var phases = Canonical();
        phases[index] = phases[index] with { Kind = repeated };
        workspace.CreateMode(phases: phases);

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains($"Phase {index + 1} must be", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsOmittedPhaseKind()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        var path = Path.Combine(modeDirectory, "mode.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["phases"]![0]!.AsObject().Remove("kind");
        File.WriteAllText(path, root.ToJsonString());

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("kind", exception.Message);
        Assert.Contains(path, exception.Message);
    }

    [Theory]
    [InlineData("prompts/main.md", "prompts/main.md")]
    [InlineData("prompts/main.md", "prompts/./main.md")]
    [InlineData("prompts/main.md", "prompts\\main.md")]
    [InlineData("prompts/main.md", "prompts/x/../main.md")]
    [InlineData("prompts/main.md", "prompts/Main.md")]
    [InlineData("prompts/main.md", "PROMPTS/main.md")]
    public async Task LoadAsync_RejectsDuplicateResolvedPromptPaths(string first, string second)
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(phases: Canonical(main: first, critic: second));

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("must be unique", exception.Message);
        Assert.Contains("'main'", exception.Message);
        Assert.Contains("'critic'", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_AcceptsNormalizedRequiredPromptReferences()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(
            phases: Canonical(
                main: "prompts\\main.md",
                critic: "./prompts/critic.md",
                revision: "prompts/sub/../revision.md"));

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame");

        Assert.Equal(3, mode.Phases.Count);
    }

    [Fact]
    public async Task LoadAsync_WithLens_AppliesLensToCanonicalPromptPaths()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode(
            phases: Canonical(
                main: "./prompts/main.md",
                critic: "prompts/../prompts/critic.md",
                revision: "prompts\\revision.md"));
        var lensDirectory = Path.Combine(modeDirectory, "prompts", "warcraft");
        Directory.CreateDirectory(lensDirectory);
        File.WriteAllText(Path.Combine(lensDirectory, "main.md"), "Lens main prompt.");
        File.WriteAllText(Path.Combine(lensDirectory, "critic.md"), "Lens critic prompt.");
        File.WriteAllText(Path.Combine(lensDirectory, "revision.md"), "Lens revision prompt.");

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame", "warcraft");

        Assert.Collection(
            mode.Phases,
            phase => Assert.Equal("Lens main prompt.", phase.Prompt),
            phase => Assert.Equal("Lens critic prompt.", phase.Prompt),
            phase => Assert.Equal("Lens revision prompt.", phase.Prompt));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\main.md")]
    [InlineData("\\prompts\\main.md")]
    [InlineData("C:\\prompts\\main.md")]
    [InlineData("C:/prompts/main.md")]
    [InlineData("C:main.md")]
    public async Task LoadAsync_RejectsRootedPromptPaths(string reference)
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(phases: Canonical(main: reference));

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("must be relative", exception.Message);
        Assert.Contains(reference, exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsNativeAbsolutePromptPath()
    {
        using var workspace = new TestWorkspace();
        var absolute = Path.Combine(workspace.ModesRoot, "frame", "prompts", "main.md");
        workspace.CreateMode(phases: Canonical(main: absolute));

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("must be relative", exception.Message);
    }

    [Theory]
    [InlineData("../main.md")]
    [InlineData("prompts/../../main.md")]
    [InlineData("prompts\\..\\..\\frame2\\prompts\\main.md")]
    [InlineData("../frame2/prompts/main.md")]
    [InlineData("../Frame/prompts/main.md")]
    [InlineData("prompts/..")]
    public async Task LoadAsync_RejectsEscapingPromptPaths(string reference)
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(phases: Canonical(main: reference));
        Directory.CreateDirectory(Path.Combine(workspace.ModesRoot, "frame2", "prompts"));
        File.WriteAllText(Path.Combine(workspace.ModesRoot, "frame2", "prompts", "main.md"), "x");

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("escapes the mode directory", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsEscapingPromptPathUnderLens()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(phases: Canonical(main: "prompts/../../main.md"));
        Directory.CreateDirectory(Path.Combine(workspace.ModesRoot, "frame", "prompts", "warcraft"));

        var exception = await LoadFailureAsync(workspace, "warcraft");

        Assert.Contains("escapes the mode directory", exception.Message);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("critic")]
    [InlineData("revision")]
    public async Task LoadAsync_RejectsUnreferencedRequiredPrompt(string unreferenced)
    {
        using var workspace = new TestWorkspace();
        var phases = Canonical();
        var index = unreferenced switch { "main" => 0, "critic" => 1, _ => 2 };
        phases[index] = phases[index] with { Prompt = "prompts/extra.md" };
        var modeDirectory = workspace.CreateMode(phases: phases);
        File.WriteAllText(Path.Combine(modeDirectory, "prompts", "extra.md"), "Substitute.");

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains($"prompts/{unreferenced}.md", exception.Message);
        Assert.Contains("not referenced by any phase", exception.Message);
        Assert.True(File.Exists(Path.Combine(modeDirectory, "prompts", $"{unreferenced}.md")));
    }

    [Fact]
    public async Task LoadAsync_RejectsUnreferencedRequiredPromptUnderLens()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode(phases: Canonical(critic: "prompts/extra.md"));
        var lensDirectory = Path.Combine(modeDirectory, "prompts", "warcraft");
        Directory.CreateDirectory(lensDirectory);
        foreach (var file in new[] { "main", "critic", "revision", "extra" })
        {
            File.WriteAllText(Path.Combine(lensDirectory, $"{file}.md"), "Lens prompt.");
        }

        var exception = await LoadFailureAsync(workspace, "warcraft");

        Assert.Contains("prompts/warcraft/critic.md", exception.Message);
        Assert.Contains("not referenced by any phase", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsCanonicalSpellingChange()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode(phases: Canonical(main: "prompts/Main.md"));
        File.WriteAllText(Path.Combine(modeDirectory, "prompts", "Main.md"), "Cased.");

        // On a case-insensitive volume Main.md and main.md are the same file, but
        // the manifest must still use the canonical spelling.
        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("not referenced by any phase", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsLinkedPromptFileOutsideMode()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        var outside = Path.Combine(workspace.Root, "outside.md");
        File.WriteAllText(outside, "Outside prompt.");
        var link = Path.Combine(modeDirectory, "prompts", "main.md");
        File.Delete(link);
        if (!TryCreateSymlink(link, outside, directory: false))
        {
            return;
        }

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("symbolic link or junction", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsLinkedPromptDirectoryOutsideMode()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        var outside = Path.Combine(workspace.Root, "outside-prompts");
        Directory.CreateDirectory(outside);
        foreach (var file in new[] { "main", "critic", "revision" })
        {
            File.WriteAllText(Path.Combine(outside, $"{file}.md"), "Outside prompt.");
        }

        var prompts = Path.Combine(modeDirectory, "prompts");
        Directory.Delete(prompts, recursive: true);
        if (!TryCreateSymlink(prompts, outside, directory: true))
        {
            return;
        }

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("symbolic link or junction", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsCaseOnlySiblingDirectoryOnCaseSensitiveFilesystem()
    {
        using var workspace = new TestWorkspace();
        if (!FilesystemIsCaseSensitive(workspace.Root))
        {
            return;
        }

        workspace.CreateMode("frame", phases: Canonical(main: "../Frame/prompts/main.md"));
        workspace.CreateMode("Frame");

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("escapes the mode directory", exception.Message);
    }

    private const string ValidManifestJson = """
        {
          "name": "frame",
          "description": "A test mode.",
          "version": 1,
          "phases": [
            { "name": "main", "kind": "main", "prompt": "prompts/main.md" },
            { "name": "critic", "kind": "critic", "prompt": "prompts/critic.md" },
            { "name": "revision", "kind": "revision", "prompt": "prompts/revision.md" }
          ],
          "outputContract": {
            "requiredHeadings": ["## Problem"],
            "minimumLength": 10
          }
        }
        """;

    [Theory]
    [InlineData("\"version\": 1", "\"version\": 1, \"descripton\": \"typo\"", "descripton")]
    [InlineData("\"prompt\": \"prompts/main.md\"", "\"prompt\": \"prompts/main.md\", \"promt\": \"x\"", "promt")]
    [InlineData("\"minimumLength\": 10", "\"minimumLength\": 10, \"minimumLenght\": 5", "minimumLenght")]
    public async Task LoadAsync_RejectsUnknownManifestProperty(
        string target,
        string replacement,
        string offendingProperty)
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        var manifestPath = Path.Combine(modeDirectory, "mode.json");
        var json = ValidManifestJson.Replace(target, replacement, StringComparison.Ordinal);
        Assert.Contains(offendingProperty, json);
        File.WriteAllText(manifestPath, json);
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame"));

        Assert.Contains(manifestPath, exception.Message);
        Assert.Contains(offendingProperty, exception.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsMalformedManifestJson()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        var manifestPath = Path.Combine(modeDirectory, "mode.json");
        File.WriteAllText(manifestPath, "{ \"name\": \"frame\", \"phases\": [");
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame"));

        Assert.Contains(manifestPath, exception.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(exception.InnerException);
    }

    [Fact]
    public async Task LoadAsync_AcceptsManifestCommentsAndTrailingCommas()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        File.WriteAllText(
            Path.Combine(modeDirectory, "mode.json"),
            """
            // line comment
            {
              "name": "frame", /* block comment */
              "description": "A test mode.",
              "version": 1,
              "phases": [
                { "name": "main", "kind": "main", "prompt": "prompts/main.md", },
                { "name": "critic", "kind": "critic", "prompt": "prompts/critic.md" },
                { "name": "revision", "kind": "revision", "prompt": "prompts/revision.md" },
              ],
              "outputContract": {
                "requiredHeadings": ["## Problem",],
                "minimumLength": 10,
              },
            }
            """);
        var loader = new FileModeLoader(workspace.ModesRoot);

        var mode = await loader.LoadAsync("frame");

        Assert.Equal("frame", mode.Manifest.Name);
        Assert.Equal(
            [PhaseKind.Main, PhaseKind.Critic, PhaseKind.Revision],
            mode.Phases.Select(phase => phase.Kind));
        Assert.Equal(10, mode.Manifest.OutputContract.MinimumLength);
        Assert.Equal(["## Problem"], mode.Manifest.OutputContract.RequiredHeadings);
    }

    [Fact]
    public void CheckedInInventory_ContainsShippedModesAndLensVariant()
    {
        var cases = CheckedInModeCases.All;

        Assert.Contains(("frame", (string?)null), cases);
        Assert.Contains(("challenge", (string?)null), cases);
        Assert.Contains(("synthesize", (string?)null), cases);
        Assert.Contains(("lens", (string?)"warcraft"), cases);
    }

    public static TheoryData<string, string?> CheckedInCases => CheckedInModeCases.Data();

    [Theory]
    [MemberData(nameof(CheckedInCases))]
    public async Task LoadAsync_ValidatesCheckedInModeAndPromptVariant(string modeName, string? lens)
    {
        var loader = new FileModeLoader(CheckedInModeCases.ModesRoot);

        var mode = await loader.LoadAsync(modeName, lens);

        Assert.Equal(modeName, mode.Manifest.Name);
        Assert.False(string.IsNullOrWhiteSpace(mode.Manifest.Description));
        Assert.False(string.IsNullOrWhiteSpace(mode.Documentation));
        Assert.Equal(
            [PhaseKind.Main, PhaseKind.Critic, PhaseKind.Revision],
            mode.Phases.Select(phase => phase.Kind));

        // Exact binding: each phase body must come from the expected directory.
        var promptDirectory = CheckedInModeCases.PromptDirectory(modeName, lens);
        foreach (var phase in mode.Phases)
        {
            Assert.False(string.IsNullOrWhiteSpace(phase.Prompt));
            var expected = await File.ReadAllTextAsync(
                Path.Combine(promptDirectory, phase.Kind.ToString().ToLowerInvariant() + ".md"));
            Assert.Equal(expected, phase.Prompt);
        }

        Assert.True(mode.Manifest.OutputContract.MinimumLength >= 1);
        Assert.NotEmpty(mode.Manifest.OutputContract.RequiredHeadings);
        Assert.All(
            mode.Manifest.OutputContract.RequiredHeadings,
            heading => Assert.False(string.IsNullOrWhiteSpace(heading)));
    }

    [Theory]
    [InlineData("frame", new[] { "## Problem", "## Objective", "## Constraints", "## Unknowns", "## Next Actions" })]
    [InlineData("challenge", new[] { "## Target Claim", "## Assumptions", "## Failure Modes", "## Counterarguments", "## Tests" })]
    [InlineData("synthesize", new[] { "## Shared Ground", "## Tensions", "## Synthesis", "## Tradeoffs", "## Recommendation" })]
    [InlineData("lens", new[] { "## The Concept", "## The Lens", "## The Mapping", "## Where It Breaks Down", "## Plain Translation" })]
    public async Task LoadAsync_CheckedInOutputContracts_MatchExpectations(
        string modeName,
        string[] headings)
    {
        var lens = modeName == "lens" ? "warcraft" : null;
        var mode = await new FileModeLoader(CheckedInModeCases.ModesRoot).LoadAsync(modeName, lens);

        Assert.Equal(headings, mode.Manifest.OutputContract.RequiredHeadings);
        Assert.Equal(200, mode.Manifest.OutputContract.MinimumLength);
    }

    [Fact]
    public async Task LoadAsync_WarcraftLens_UsesWarcraftPromptsForEveryPhase()
    {
        var mode = await new FileModeLoader(CheckedInModeCases.ModesRoot)
            .LoadAsync("lens", "warcraft");

        Assert.Equal(3, mode.Phases.Count);
        Assert.All(
            mode.Phases,
            phase => Assert.Contains("warcraft", phase.Prompt, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(0, "## Problem")]
    [InlineData(-1, "## Problem")]
    [InlineData(10, "")]
    [InlineData(10, "   ")]
    public async Task LoadAsync_RejectsInvalidOutputContract(int minimumLength, string heading)
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame", requiredHeadings: [heading]);
        var path = Path.Combine(dir, "mode.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["outputContract"]!["minimumLength"] = minimumLength;
        File.WriteAllText(path, root.ToJsonString());

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => new FileModeLoader(workspace.ModesRoot).LoadAsync("frame"));

        Assert.Contains("output contract", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_VariantOnlyModeWithoutLens_ListsLensesInOrder()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        MoveDefaultPromptsToVariants(dir, "zeta", "alpha");

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => new FileModeLoader(workspace.ModesRoot).LoadAsync("lens"));

        Assert.Contains("--lens", exception.Message);
        Assert.Contains("alpha, zeta", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_PartialDefaultPrompts_ReportsMissingFileNotLens()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        Directory.CreateDirectory(Path.Combine(dir, "prompts", "extra"));
        File.Delete(Path.Combine(dir, "prompts", "critic.md"));

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => new FileModeLoader(workspace.ModesRoot).LoadAsync("frame"));

        Assert.Contains("prompts/critic.md", exception.Message);
        Assert.DoesNotContain("--lens", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_DefaultPromptsWithVariants_LoadsWithoutLens()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        Directory.CreateDirectory(Path.Combine(dir, "prompts", "extra"));

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame");

        Assert.Equal("frame", mode.Manifest.Name);
    }

    internal static void MoveDefaultPromptsToVariants(string modeDirectory, params string[] lenses)
    {
        var prompts = Path.Combine(modeDirectory, "prompts");
        string[] files = ["main.md", "critic.md", "revision.md"];
        foreach (var lens in lenses)
        {
            var target = Directory.CreateDirectory(Path.Combine(prompts, lens)).FullName;
            foreach (var file in files)
            {
                File.Copy(Path.Combine(prompts, file), Path.Combine(target, file));
            }
        }

        foreach (var file in files)
        {
            File.Delete(Path.Combine(prompts, file));
        }
    }

    private static ModePhaseManifest CreatePhase(
        string name,
        PhaseKind kind) =>
        new()
        {
            Name = name,
            Kind = kind,
            Prompt = $"prompts/{name.Replace("-copy", string.Empty)}.md"
        };
}
