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
    public async Task LoadAsync_RejectsMissingRevisionPhase()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(
            phases:
            [
                CreatePhase("main", PhaseKind.Main),
                CreatePhase("critic", PhaseKind.Critic)
            ]);
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame"));

        Assert.Contains("exactly three phases", exception.Message);
        Assert.Contains("main, critic, revision", exception.Message);
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
    public async Task LoadAsync_RejectsDuplicateRequiredPhase()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(
            phases:
            [
                CreatePhase("main", PhaseKind.Main),
                CreatePhase("critic", PhaseKind.Critic),
                CreatePhase("critic-copy", PhaseKind.Critic)
            ]);
        var loader = new FileModeLoader(workspace.ModesRoot);

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("frame"));

        Assert.Contains("Phase 3", exception.Message);
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

    [Theory]
    [InlineData("challenge", null)]
    [InlineData("frame", null)]
    [InlineData("synthesize", null)]
    [InlineData("lens", "warcraft")]
    public async Task LoadAsync_LoadsCheckedInManifest(string modeName, string? lens)
    {
        var loader = new FileModeLoader(FindRepositoryModesRoot());

        var mode = await loader.LoadAsync(modeName, lens);

        Assert.Equal(modeName, mode.Manifest.Name);
        Assert.Equal(3, mode.Phases.Count);
        Assert.All(mode.Phases, phase => Assert.NotEmpty(phase.Prompt));
    }

    private static string FindRepositoryModesRoot()
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
