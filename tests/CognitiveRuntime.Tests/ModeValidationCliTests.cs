using System.Diagnostics;
using CognitiveRuntime.Cli;
using Xunit;

namespace CognitiveRuntime.Tests;

public sealed class ModeValidationCliTests
{
    private sealed record Result(int ExitCode, string Out, string Err);

    private static async Task<Result> RunWithTokenAsync(
        CancellationToken token,
        string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(args, output, error, token);
        return new Result(code, output.ToString(), error.ToString());
    }

    private static Task<Result> RunAsync(params string[] args) =>
        RunWithTokenAsync(CancellationToken.None, args);

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new string[0],
        new[] { "validate" },
        new[] { "validate", "a", "b" },
        new[] { "validate", "frame", "--input", "x.txt" },
        new[] { "validate", "frame", "--modes-root" },
        new[] { "validate", "frame", "--modes-root", "a", "--modes-root", "b" },
        new[] { "validate", "--modes-root", "a" },
        new[] { "validate", " " },
        new[] { "frobnicate" },
        new[] { "validate", "frame", "--help" },
        new[] { "validate", "frame", "--modes-root", "--help" },
        new[] { "validate", "frame", "--modes-root", "-h" },
        new[] { "validate", "frame", "--modes-root", "-x" },
        new[] { "validate", "lens", "--lens" },
        new[] { "validate", "lens", "--lens", " " },
        new[] { "validate", "lens", "--lens", "--modes-root" },
        new[] { "validate", "lens", "--lens", "a", "--LENS", "b" },
    };

    public static TheoryData<string[]> HelpArguments => new()
    {
        new[] { "modes", "validate", "--help" },
        new[] { "modes", "validate", "-h" },
        new[] { "modes", "--help" },
        new[] { "modes", "list", "--help" },
        new[] { "modes", "LIST", "-h" },
    };

    private static string[] Snapshot(string root) =>
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Order()
            .ToArray();

    [Fact]
    public async Task Validate_ValidMode_ReturnsZeroAndReportsModeWithoutRunOutput()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("frame");
        var before = Snapshot(workspace.Root);

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("'frame'", result.Out);
        Assert.Equal(string.Empty, result.Err);
        Assert.Equal(before, Snapshot(workspace.Root));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.OutputRoot));
    }

    public static TheoryData<string, string?> CheckedInCases => CheckedInModeCases.Data();

    [Theory]
    [MemberData(nameof(CheckedInCases))]
    public async Task Validate_CheckedInModeAndPromptVariant_ReturnsZero(string mode, string? lens)
    {
        string[] args = lens is null
            ? ["modes", "validate", mode, "--modes-root", CheckedInModeCases.ModesRoot]
            : ["modes", "validate", mode, "--LENS", lens, "--modes-root", CheckedInModeCases.ModesRoot];

        var result = await RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"'{mode}'", result.Out);
        if (lens is not null)
        {
            Assert.Contains($"'{lens}'", result.Out);
        }

        Assert.Equal(string.Empty, result.Err);
    }

    [Fact]
    public async Task Validate_RequiredLensMissing_ReturnsOneAndListsAvailableLenses()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "zeta", "alpha");
        var before = Snapshot(workspace.Root);

        var result = await RunAsync(
            "modes", "validate", "lens", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains("--lens", result.Err);
        Assert.Contains("alpha, zeta", result.Err);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task Validate_VariantOnlyMode_WithLens_ReturnsZero()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "alpha");

        var result = await RunAsync(
            "modes", "validate", "lens", "--lens", "alpha", "--modes-root", workspace.ModesRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("'alpha'", result.Out);
    }

    // A lens name starts with a letter or digit, so a dash-led directory is never offered as a lens and a dash-led
    // value is never taken as one: no listed lens can read as an option or a help flag (-h) on the command line.
    [Fact]
    public async Task Validate_DashLedVariantDirectories_AreNotLenses()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "-alpha", "--alpha", "-h", "zeta");

        var listed = await RunAsync(
            "modes", "validate", "lens", "--modes-root", workspace.ModesRoot);
        Assert.Equal(1, listed.ExitCode);
        Assert.Contains("Available lenses: zeta.", listed.Err);

        foreach (var value in new[] { "-alpha", "--alpha", "-h", "-H" })
        {
            var selected = await RunAsync(
                "modes", "validate", "lens", "--lens", value, "--modes-root", workspace.ModesRoot);
            Assert.Equal(2, selected.ExitCode);
            Assert.Contains("requires a lens name", selected.Err);
        }

        var zeta = await RunAsync(
            "modes", "validate", "lens", "--lens", "zeta", "--modes-root", workspace.ModesRoot);
        Assert.Equal(0, zeta.ExitCode);
    }

    [Fact]
    public async Task Validate_PartialDefaultPrompts_ReportsMissingFile()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        Directory.CreateDirectory(Path.Combine(dir, "prompts", "alpha"));
        File.Delete(Path.Combine(dir, "prompts", "revision.md"));

        var result = await RunAsync(
            "modes", "validate", "lens", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("prompts/revision.md", result.Err);
        Assert.DoesNotContain("Available lenses", result.Err);
    }

    [Fact]
    public async Task Validate_DefaultPromptsWithVariants_SucceedsWithoutLens()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        Directory.CreateDirectory(Path.Combine(dir, "prompts", "alpha"));

        var result = await RunAsync(
            "modes", "validate", "lens", "--modes-root", workspace.ModesRoot);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Validate_UnknownLens_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("lens");

        var result = await RunAsync(
            "modes", "validate", "lens", "--lens", "nope", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Lens 'nope' was not found", result.Err);
    }

    [Fact]
    public async Task Validate_UnsafeLens_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("lens");

        var result = await RunAsync(
            "modes", "validate", "lens", "--lens", "a.b", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("unsupported characters", result.Err);
    }

    [Fact]
    public async Task Validate_IncompleteSelectedLens_ReportsMissingPrompt()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "alpha");
        File.Delete(Path.Combine(dir, "prompts", "alpha", "critic.md"));

        var result = await RunAsync(
            "modes", "validate", "lens", "--lens", "alpha", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("prompts/alpha/critic.md", result.Err);
    }

    [Fact]
    public async Task Validate_LensVariant_SucceedsInChildProcessWithBogusProvider()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "alpha");
        Directory.Delete(workspace.OutputRoot);
        var before = Snapshot(workspace.Root);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(
            Path.Combine(AppContext.BaseDirectory, "CognitiveRuntime.Cli.dll"));
        foreach (var argument in new[] { "modes", "validate", "lens", "--lens", "alpha" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["MODEL_PROVIDER"] = "bogus-provider";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Contains("'alpha'", await stdout);
        Assert.Equal(before, Snapshot(workspace.Root));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "outputs")));
    }

    [Fact]
    public async Task Validate_CommandWordsAreCaseInsensitive()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("frame");

        var result = await RunAsync(
            "MODES", "Validate", "frame", "--MODES-ROOT", workspace.ModesRoot);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Validate_RootWithSpaces_Succeeds()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("frame");
        var spaced = Path.Combine(workspace.Root, "my modes");
        Directory.Move(workspace.ModesRoot, spaced);

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", spaced);

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task Validate_DefaultRoot_SucceedsInChildProcessWithBogusProvider()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("frame");
        Directory.Delete(workspace.OutputRoot);
        var before = Snapshot(workspace.Root);

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add(
            Path.Combine(AppContext.BaseDirectory, "CognitiveRuntime.Cli.dll"));
        startInfo.ArgumentList.Add("modes");
        startInfo.ArgumentList.Add("validate");
        startInfo.ArgumentList.Add("frame");
        startInfo.Environment["MODEL_PROVIDER"] = "bogus-provider";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Contains("'frame'", await stdout);
        Assert.Equal(before, Snapshot(workspace.Root));
        Assert.False(Directory.Exists(Path.Combine(workspace.Root, "outputs")));
    }

    [Fact]
    public async Task Validate_PromptPathWithNul_ReturnsOneNamingPrompt()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        var path = Path.Combine(dir, "mode.json");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("prompts/main.md", "prompts/ma\\u0000in.md"));

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("prompts/ma", result.Err);
        Assert.Equal(string.Empty, result.Out);
    }

    [Fact]
    public async Task Validate_ModeDirectlyUnderFilesystemRoot_IsNotRejectedAsEscape()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        var result = await RunAsync(
            "modes", "validate", "zz-no-such-mode-" + Guid.NewGuid().ToString("N"),
            "--modes-root", root);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("escapes", result.Err);
        Assert.Contains("was not found", result.Err);
    }

    [Fact]
    public async Task TopLevelHelp_ListsModesValidate()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("modes validate", result.Out);
        Assert.Contains("modes list", result.Out);
        Assert.Contains("--lens", result.Out);
    }

    [Fact]
    public async Task Validate_UnknownMode_ReturnsOneNamingModeAndRoot()
    {
        using var workspace = new TestWorkspace();

        var result = await RunAsync(
            "modes", "validate", "nope", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains("'nope'", result.Err);
        Assert.Contains(workspace.ModesRoot, result.Err);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.OutputRoot));
    }

    [Fact]
    public async Task Validate_MalformedJson_ReturnsOneNamingManifest()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        File.WriteAllText(Path.Combine(dir, "mode.json"), "{ not json");

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains("mode.json", result.Err);
        Assert.Contains("'frame'", result.Err);
    }

    [Fact]
    public async Task Validate_MistypedManifestField_ReturnsOneNamingField()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        var path = Path.Combine(dir, "mode.json");
        File.WriteAllText(
            path,
            System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(path),
                "\"minimumLength\":\\s*100",
                "\"minimumLength\": \"long\""));

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains("minimumLength", result.Err);
        Assert.Contains("line", result.Err);
        Assert.DoesNotContain("not valid JSON", result.Err);
    }

    [Fact]
    public async Task Validate_PromptInCaseDifferingSiblingDirectory_IsRejectedOnCaseSensitiveFilesystem()
    {
        using var workspace = new TestWorkspace();
        var probe = Path.Combine(workspace.Root, "case-probe");
        Directory.CreateDirectory(probe);
        if (Directory.Exists(Path.Combine(workspace.Root, "CASE-PROBE")))
        {
            return; // Case-insensitive volume: no distinct sibling can exist.
        }

        var dir = workspace.CreateMode("frame");
        workspace.CreateMode("Frame");
        ReplaceInManifest(dir, "prompts/main.md", "../Frame/prompts/main.md");
        var before = Snapshot(workspace.Root);

        var result = await RunValidateAsync(workspace);

        AssertRejected(result, "escapes");
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task Validate_CaseOnlySiblingTraversal_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        ReplaceInManifest(dir, "prompts/main.md", "../Frame/prompts/main.md");

        var result = await RunValidateAsync(workspace);

        AssertRejected(result, "escapes");
    }

    [Fact]
    public async Task Validate_InvalidPhaseName_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        ReplaceInManifest(dir, "\"name\": \"main\"", "\"name\": \"ma_in\"");
        var before = Snapshot(workspace.Root);

        var result = await RunValidateAsync(workspace);

        AssertRejected(result, "Phase 1 name 'ma_in'");
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task Validate_DuplicateNormalizedPrompt_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        ReplaceInManifest(dir, "prompts/critic.md", "prompts/./main.md");
        var before = Snapshot(workspace.Root);

        var result = await RunValidateAsync(workspace);

        AssertRejected(result, "must be unique");
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    [Fact]
    public async Task Validate_UnreferencedRequiredPrompt_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        File.WriteAllText(Path.Combine(dir, "prompts", "extra.md"), "Substitute.");
        ReplaceInManifest(dir, "prompts/revision.md", "prompts/extra.md");
        var before = Snapshot(workspace.Root);

        var result = await RunValidateAsync(workspace);

        AssertRejected(result, "prompts/revision.md");
        Assert.Contains("not referenced by any phase", result.Err);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    private static void ReplaceInManifest(string modeDirectory, string target, string replacement)
    {
        var path = Path.Combine(modeDirectory, "mode.json");
        var json = File.ReadAllText(path);
        Assert.Contains(target, json);
        File.WriteAllText(path, json.Replace(target, replacement, StringComparison.Ordinal));
    }

    private static Task<Result> RunValidateAsync(TestWorkspace workspace) =>
        RunAsync("modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

    private static void AssertRejected(Result result, string diagnostic)
    {
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains(diagnostic, result.Err);
    }

    [Fact]
    public async Task Validate_MissingPrompt_ReturnsOneNamingFile()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        File.Delete(Path.Combine(dir, "prompts", "critic.md"));

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("critic.md", result.Err);
    }

    [Fact]
    public async Task Validate_EmptyPrompt_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        File.WriteAllText(Path.Combine(dir, "prompts", "main.md"), "  ");

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is empty", result.Err);
    }

    [Fact]
    public async Task Validate_InvalidPhaseOrder_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        var path = Path.Combine(dir, "mode.json");
        var json = File.ReadAllText(path);
        Assert.Contains("\"kind\": \"main\"", json);
        File.WriteAllText(
            path,
            json.Replace("\"kind\": \"main\"", "\"kind\": \"TMP\"")
                .Replace("\"kind\": \"critic\"", "\"kind\": \"main\"")
                .Replace("\"kind\": \"TMP\"", "\"kind\": \"critic\""));

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Phase 1 must be 'main'", result.Err);
    }

    [Theory]
    [InlineData("phases", "'phases'")]
    [InlineData("outputContract", "'outputContract'")]
    [InlineData("outputContract.requiredHeadings", "'outputContract.requiredHeadings'")]
    public async Task Validate_NullManifestFields_ReturnsOneWithFieldDiagnostic(
        string field,
        string expected)
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        var path = Path.Combine(dir, "mode.json");
        // Set the field to null in the parsed manifest: an extra property to keep the JSON valid would now be
        // rejected as unknown (MO-002) before the null check this test is about.
        var root = System.Text.Json.Nodes.JsonNode.Parse(
            File.ReadAllText(path),
            documentOptions: new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = System.Text.Json.JsonCommentHandling.Skip,
            })!.AsObject();
        var parts = field.Split('.');
        var parent = parts[..^1].Aggregate(root, (node, part) => node[part]!.AsObject());
        Assert.True(parent.ContainsKey(parts[^1]));
        parent[parts[^1]] = null;
        File.WriteAllText(path, root.ToJsonString());

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(expected, result.Err);
        Assert.DoesNotContain("Object reference", result.Err);
    }

    [Fact]
    public async Task Validate_NullPhaseEntry_ReturnsOneWithPhaseIndex()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        var path = Path.Combine(dir, "mode.json");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace("\"phases\": [", "\"phases\": [ null,"));

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("phase 1", result.Err);
        Assert.DoesNotContain("Object reference", result.Err);
    }

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task Validate_InvalidArguments_ReturnTwoWithUsage(string[] rest)
    {
        var result = await RunAsync(["modes", .. rest]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains("modes validate <mode>", result.Err);
    }

    [Theory]
    [MemberData(nameof(HelpArguments))]
    public async Task Validate_Help_ReturnsZeroAndShowsSyntax(string[] args)
    {
        var result = await RunAsync(args);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(args.Contains("list", StringComparer.OrdinalIgnoreCase)
            ? "modes list [--modes-root"
            : "modes validate <mode>", result.Out);
        Assert.Contains("130", result.Out);
        Assert.Equal(string.Empty, result.Err);
    }

    [Fact]
    public async Task Validate_CancelledToken_Returns130WithoutRunOutput()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("frame");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await RunWithTokenAsync(
            cts.Token,
            ["modes", "validate", "frame", "--modes-root", workspace.ModesRoot]);

        Assert.Equal(130, result.ExitCode);
        Assert.Contains("cancelled", result.Err);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.OutputRoot));
    }
}
