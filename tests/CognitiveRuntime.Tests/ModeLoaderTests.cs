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
        var modeDirectory = workspace.CreateMode();
        // A content version that differs from the schema version: the two fields are independent.
        EditManifest(modeDirectory, root => root["version"] = 7);
        var loader = new FileModeLoader(workspace.ModesRoot);

        var mode = await loader.LoadAsync("frame");

        Assert.Equal("frame", mode.Manifest.Name);
        Assert.Equal(FileModeLoader.SupportedSchemaVersion, mode.Manifest.SchemaVersion);
        Assert.Equal(1, mode.Manifest.SchemaVersion);
        Assert.Equal(7, mode.Manifest.Version);
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
          "schemaVersion": 1,
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
    [InlineData(
        "\"prompt\": \"prompts/critic.md\"",
        "\"prompt\": \"prompts/critic.md\", \"outputContract\": { \"requiredHeadings\": [\"## Findings\"] }, \"outputContact\": {}",
        "outputContact")]
    [InlineData(
        "\"prompt\": \"prompts/critic.md\"",
        "\"prompt\": \"prompts/critic.md\", \"outputContract\": { \"requiredHeadings\": [\"## Findings\"], \"requiredHedings\": [] }",
        "requiredHedings")]
    [InlineData(
        "\"prompt\": \"prompts/critic.md\"",
        "\"prompt\": \"prompts/critic.md\", \"outputContractSpecified\": true",
        "outputContractSpecified")]
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

    [Theory]
    [InlineData(null, "is missing")]
    [InlineData("0", "is 0")]
    [InlineData("2", "is 2")]
    [InlineData("-1", "is -1")]
    [InlineData("\"1\"", "is \"1\"")]
    [InlineData("null", "is null")]
    [InlineData("true", "is true")]
    [InlineData("[1]", "is [1]")]
    [InlineData("{\"major\": 1}", "is {\"major\": 1}")]
    [InlineData("1.0", "is 1.0")]
    [InlineData("1e0", "is 1e0")]
    [InlineData("4294967297", "is 4294967297")]
    public async Task LoadAsync_RejectsUnsupportedManifestSchemaVersion(string? value, string found)
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        var manifestPath = Path.Combine(modeDirectory, "mode.json");
        const string declaration = "\"schemaVersion\": 1,";
        Assert.Contains(declaration, ValidManifestJson);
        File.WriteAllText(
            manifestPath,
            ValidManifestJson.Replace(
                declaration,
                value is null ? string.Empty : $"\"schemaVersion\": {value},",
                StringComparison.Ordinal));
        WriteDocumentation(modeDirectory, "# frame\n\nmain -> critic -> revision\n\n`## Problem`\n");

        var exception = await LoadFailureAsync(workspace);

        Assert.Equal(
            $"Mode manifest '{manifestPath}' field 'schemaVersion' {found}; this runtime supports only " +
            "schemaVersion 1 (an integer, separate from the mode's 'version').",
            exception.Message);
    }

    [Fact]
    public async Task LoadAsync_ManifestSchemaVersionFixtureLoadsWhenSupported()
    {
        // Guards the theory above: its fixture is valid apart from the schema version.
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        File.WriteAllText(Path.Combine(modeDirectory, "mode.json"), ValidManifestJson);
        WriteDocumentation(modeDirectory, "# frame\n\nmain -> critic -> revision\n\n`## Problem`\n");

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame");

        Assert.Equal(1, mode.Manifest.SchemaVersion);
    }

    [Theory]
    [InlineData("\"SchemaVersion\": 2,", "is 2")]
    [InlineData("\"SCHEMAVERSION\": 3,", "is 3")]
    public async Task LoadAsync_SchemaVersionCheckMatchesSerializerPropertyLookup(
        string declarations,
        string found)
    {
        // The serializer binds the field ignoring case; the gate must find it the same way.
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        File.WriteAllText(
            Path.Combine(modeDirectory, "mode.json"),
            ValidManifestJson.Replace("\"schemaVersion\": 1,", declarations, StringComparison.Ordinal));

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains($"field 'schemaVersion' {found};", exception.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("[1]")]
    public async Task LoadAsync_RejectsASchemaVersionDeclaredMoreThanOnce(string earlier)
    {
        // One declaration, compared ignoring case as the serializer binds it: a second one is an error rather
        // than a rule about which of them wins.
        foreach (var lastName in new[] { "SchemaVersion", "schemaVersion" })
        {
            using var workspace = new TestWorkspace();
            var modeDirectory = workspace.CreateMode();
            File.WriteAllText(
                Path.Combine(modeDirectory, "mode.json"),
                ValidManifestJson.Replace(
                    "\"schemaVersion\": 1,",
                    $"\"schemaVersion\": {earlier}, \"{lastName}\": 1,",
                    StringComparison.Ordinal));

            var exception = await LoadFailureAsync(workspace);

            Assert.Contains("field 'schemaVersion' is declared 2 times", exception.Message);
            Assert.Contains("declare it once, as schemaVersion 1", exception.Message);
        }
    }

    [Fact]
    public async Task LoadAsync_UnsupportedSchemaVersionIsReportedBeforeUnknownProperties()
    {
        using var workspace = new TestWorkspace();
        var modeDirectory = workspace.CreateMode();
        File.WriteAllText(
            Path.Combine(modeDirectory, "mode.json"),
            ValidManifestJson.Replace(
                "\"schemaVersion\": 1,",
                "\"schemaVersion\": 2, \"futureField\": true,",
                StringComparison.Ordinal));

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("field 'schemaVersion' is 2;", exception.Message);
        Assert.DoesNotContain("futureField", exception.Message);
    }

    private static void EditManifest(string modeDirectory, Action<JsonObject> edit)
    {
        var path = Path.Combine(modeDirectory, "mode.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root);
        File.WriteAllText(path, root.ToJsonString());
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
              "schemaVersion": 1, // the manifest format, not the mode content
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
        WriteDocumentation(modeDirectory, "# frame\n\nmain -> critic -> revision\n\n`## Problem`\n");
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

    [Theory]
    [MemberData(nameof(CheckedInCases))]
    public async Task LoadAsync_CheckedInModesResolveLegacyPhaseContractDefaults(
        string modeName,
        string? lens)
    {
        var mode = await new FileModeLoader(CheckedInModeCases.ModesRoot).LoadAsync(modeName, lens);

        Assert.All(mode.Manifest.Phases, phase => Assert.Null(phase.OutputContract));
        Assert.All(mode.Phases, phase => Assert.False(phase.DeclaresOutputContract));
        Assert.Same(mode.Manifest.OutputContract, PhaseOf(mode, PhaseKind.Main).OutputContract);
        Assert.Same(mode.Manifest.OutputContract, PhaseOf(mode, PhaseKind.Revision).OutputContract);
        Assert.Null(PhaseOf(mode, PhaseKind.Critic).OutputContract);
    }

    [Fact]
    public async Task LoadAsync_ResolvesDistinctPhaseOutputContracts()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode(
            phases:
            [
                CreatePhase("main", PhaseKind.Main) with { OutputContract = Contract(10, "## Draft") },
                CreatePhase("critic", PhaseKind.Critic) with { OutputContract = Contract(20, "## Findings") },
                CreatePhase("revision", PhaseKind.Revision) with
                {
                    OutputContract = Contract(30, "## Decision", "## Rationale")
                }
            ]);

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame");

        Assert.All(mode.Phases, phase => Assert.True(phase.DeclaresOutputContract));
        Assert.Equal(["## Draft"], PhaseOf(mode, PhaseKind.Main).OutputContract!.RequiredHeadings);
        Assert.Equal(10, PhaseOf(mode, PhaseKind.Main).OutputContract!.MinimumLength);
        Assert.Equal(["## Findings"], PhaseOf(mode, PhaseKind.Critic).OutputContract!.RequiredHeadings);
        Assert.Equal(20, PhaseOf(mode, PhaseKind.Critic).OutputContract!.MinimumLength);
        Assert.Equal(
            ["## Decision", "## Rationale"],
            PhaseOf(mode, PhaseKind.Revision).OutputContract!.RequiredHeadings);
        Assert.Equal(30, PhaseOf(mode, PhaseKind.Revision).OutputContract!.MinimumLength);
        // The mode-level contract is unchanged by phase declarations.
        Assert.Equal(100, mode.Manifest.OutputContract.MinimumLength);
    }

    [Fact]
    public async Task LoadAsync_PhaseContractOmittingMinimumLengthDefaultsToOne()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode();
        EditPhase(dir, 1, phase => phase["outputContract"] = JsonNode.Parse(
            """{ "requiredHeadings": ["## Findings"] }"""));

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame");

        var critic = PhaseOf(mode, PhaseKind.Critic);
        Assert.True(critic.DeclaresOutputContract);
        Assert.Equal(1, critic.OutputContract!.MinimumLength);
        // Undeclared phases keep the runtime defaults.
        Assert.False(PhaseOf(mode, PhaseKind.Main).DeclaresOutputContract);
        Assert.Same(mode.Manifest.OutputContract, PhaseOf(mode, PhaseKind.Main).OutputContract);
    }

    [Theory]
    [InlineData("null", "not null")]
    [InlineData("\"## Findings\"", "outputContract")]
    [InlineData("[]", "outputContract")]
    [InlineData("{}", "at least one heading")]
    [InlineData("""{ "requiredHeadings": null }""", "requiredHeadings")]
    [InlineData("""{ "requiredHeadings": [] }""", "at least one heading")]
    [InlineData("""{ "requiredHeadings": [null] }""", "at least one heading")]
    [InlineData("""{ "requiredHeadings": ["  "] }""", "at least one heading")]
    [InlineData("""{ "requiredHeadings": ["## Findings"], "minimumLength": 0 }""", "positive minimum length")]
    [InlineData("""{ "requiredHeadings": ["## Findings"], "minimumLength": -3 }""", "positive minimum length")]
    [InlineData("""{ "requiredHeadings": "## Findings" }""", "requiredHeadings")]
    [InlineData("""{ "requiredHeadings": ["## Findings"], "minimumLength": true }""", "minimumLength")]
    public async Task LoadAsync_RejectsInvalidPhaseOutputContract(string contractJson, string expected)
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode();
        EditPhase(dir, 1, phase => phase["outputContract"] = JsonNode.Parse(contractJson));

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => new FileModeLoader(workspace.ModesRoot).LoadAsync("frame"));

        Assert.Contains(expected, exception.Message);
    }

    [Fact]
    public async Task LoadAsync_InvalidPhaseContractDiagnosticNamesPhase()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode();
        EditPhase(dir, 2, phase => phase["outputContract"] = JsonNode.Parse(
            """{ "requiredHeadings": [] }"""));

        var exception = await Assert.ThrowsAsync<ModeLoadException>(
            () => new FileModeLoader(workspace.ModesRoot).LoadAsync("frame"));

        Assert.Contains("Phase 3 ('revision') field 'outputContract'", exception.Message);
    }

    private static void EditPhase(string modeDirectory, int index, Action<JsonObject> edit)
    {
        var path = Path.Combine(modeDirectory, "mode.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        edit(root["phases"]![index]!.AsObject());
        File.WriteAllText(path, root.ToJsonString());
    }

    private static OutputContract Contract(int minimumLength, params string[] headings) =>
        new() { RequiredHeadings = [.. headings], MinimumLength = minimumLength };

    private static LoadedPhase PhaseOf(LoadedMode mode, PhaseKind kind) =>
        Assert.Single(mode.Phases, phase => phase.Kind == kind);

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

    [Fact]
    public async Task LoadAsync_RequiresModeDocumentation()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode();
        File.Delete(Path.Combine(dir, "MODE.md"));

        var exception = await LoadFailureAsync(workspace);

        Assert.Equal("Required mode file 'MODE.md' was not found.", exception.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t\r\n  \n")]
    public async Task LoadAsync_RejectsBlankModeDocumentation(string documentation)
    {
        using var workspace = new TestWorkspace();
        WriteDocumentation(workspace.CreateMode(), documentation);

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("Mode 'frame' documentation 'MODE.md'", exception.Message);
        Assert.Contains("empty or contains only whitespace", exception.Message);
        Assert.Contains("missing phase names 'main', 'critic', 'revision'", exception.Message);
        Assert.Contains(
            "missing output headings '## Problem', '## Objective', '## Constraints', " +
            "'## Unknowns', '## Next Actions'",
            exception.Message);
    }

    [Fact]
    public async Task LoadAsync_AcceptsExactDocumentationMentions()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode(
            phases:
            [
                Phase("Draft-1", PhaseKind.Main, "prompts/main.md"),
                Phase("review", PhaseKind.Critic, "prompts/critic.md"),
                Phase("final2", PhaseKind.Revision, "prompts/revision.md") with
                {
                    OutputContract = Contract(1, "## Decision", "## Rationale")
                }
            ]);
        // Terse, unstructured text with no Output section: a partial token (pre-draft-1) precedes
        // the standalone mention, names differ in case and touch punctuation and both document
        // boundaries, and headings are split between inline code and a CRLF fenced block.
        WriteDocumentation(
            dir,
            "final2 pre-draft-1 then DRAFT-1, (Review) `## Decision`\r\n```text\r\n## Rationale\r\n```\r\nfinal2");

        var mode = await new FileModeLoader(workspace.ModesRoot).LoadAsync("frame");

        Assert.Equal(["Draft-1", "review", "final2"], mode.Phases.Select(phase => phase.Name));
    }

    [Fact]
    public async Task LoadAsync_ReportsAllMissingDocumentationMentions()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode(
            phases:
            [
                Phase("draft", PhaseKind.Main, "prompts/main.md"),
                Phase("review", PhaseKind.Critic, "prompts/critic.md"),
                Phase("final", PhaseKind.Revision, "prompts/revision.md")
            ]);
        // Phase kinds are decoys, names appear only inside larger tokens, and headings differ
        // in case, spacing, or hashes; only '## Unknowns' is present exactly.
        WriteDocumentation(
            dir,
            "# frame\n\nKinds: main, critic, revision.\n" +
            "pre-draft draft_extra reviewer review2 finalize -final\n" +
            "## problem\n##  Objective\nConstraints\n## Unknowns\n");

        var exception = await LoadFailureAsync(workspace);

        Assert.Contains("Mode 'frame' documentation 'MODE.md'", exception.Message);
        Assert.DoesNotContain("empty", exception.Message);
        Assert.Contains("missing phase names 'draft', 'review', 'final';", exception.Message);
        Assert.Contains(
            "missing output headings '## Problem', '## Objective', '## Constraints', '## Next Actions'.",
            exception.Message);
        Assert.DoesNotContain("'## Unknowns'", exception.Message);
    }

    [Fact]
    public async Task LoadAsync_DocumentationUsesResolvedRevisionContract()
    {
        const string names = "main critic revision\n";
        const string modeHeadings =
            "## Problem\n## Objective\n## Constraints\n## Unknowns\n## Next Actions\n";
        using var workspace = new TestWorkspace();
        var withOverride = workspace.CreateMode(
            "override",
            phases:
            [
                CreatePhase("main", PhaseKind.Main) with { OutputContract = Contract(1, "## Draft") },
                CreatePhase("critic", PhaseKind.Critic) with { OutputContract = Contract(1, "## Findings") },
                CreatePhase("revision", PhaseKind.Revision) with { OutputContract = Contract(1, "## Decision") }
            ]);
        var inherited = workspace.CreateMode(
            "inherited",
            phases:
            [
                CreatePhase("main", PhaseKind.Main) with { OutputContract = Contract(1, "## Draft") },
                CreatePhase("critic", PhaseKind.Critic) with { OutputContract = Contract(1, "## Findings") },
                CreatePhase("revision", PhaseKind.Revision)
            ]);
        var loader = new FileModeLoader(workspace.ModesRoot);

        // The revision override is the only heading requirement; main and critic add none.
        WriteDocumentation(withOverride, names + "## Decision\n");
        await loader.LoadAsync("override");

        WriteDocumentation(withOverride, names + modeHeadings + "## Draft\n## Findings\n");
        var overrideFailure = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("override"));
        Assert.Contains("missing output headings '## Decision'.", overrideFailure.Message);
        Assert.DoesNotContain("## Problem", overrideFailure.Message);

        // Without an override the revision falls back to the mode-level contract.
        WriteDocumentation(inherited, names + modeHeadings);
        await loader.LoadAsync("inherited");

        WriteDocumentation(inherited, names + "## Draft\n## Findings\n");
        var inheritedFailure = await Assert.ThrowsAsync<ModeLoadException>(
            () => loader.LoadAsync("inherited"));
        Assert.Contains(
            "missing output headings '## Problem', '## Objective', '## Constraints', " +
            "'## Unknowns', '## Next Actions'.",
            inheritedFailure.Message);
        Assert.DoesNotContain("## Draft", inheritedFailure.Message);
        Assert.DoesNotContain("## Findings", inheritedFailure.Message);
    }

    internal static void WriteDocumentation(string modeDirectory, string documentation) =>
        File.WriteAllText(Path.Combine(modeDirectory, "MODE.md"), documentation);

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
