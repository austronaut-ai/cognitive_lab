using System.Diagnostics;
using CognitiveRuntime.Cli;
using CognitiveRuntime.Core.Contracts;
using Xunit;

namespace CognitiveRuntime.Tests;

public sealed class ModeListCliTests
{
    private sealed record Result(int ExitCode, string Out, string Err);

    private static async Task<Result> RunWithTokenAsync(CancellationToken token, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await CliApplication.RunAsync(["modes", .. args], output, error, token);
        return new Result(code, output.ToString(), error.ToString());
    }

    private static Task<Result> RunAsync(params string[] args) =>
        RunWithTokenAsync(CancellationToken.None, args);

    private static string[] Snapshot(string root) =>
        Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order().ToArray();

    [Fact]
    public async Task List_CheckedInRoot_ReturnsZeroAndShowsAllModesAndWarcraft()
    {
        var result = await RunAsync("list", "--modes-root", CheckedInModeCases.ModesRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Err);
        foreach (var mode in new[] { "challenge", "frame", "lens", "synthesize" })
        {
            Assert.Contains($"Mode: {mode}{Environment.NewLine}", result.Out);
        }

        var lensEntry = result.Out[result.Out.IndexOf("Mode: lens", StringComparison.Ordinal)..];
        Assert.Contains("Lenses: warcraft", lensEntry);
        Assert.Contains("Version: ", result.Out);
        Assert.Contains("Phases: main (main), critic (critic), revision (revision)", result.Out);
    }

    [Fact]
    public async Task List_MixedDirectories_PrintsValidMetadataAndInvalidReasons_ReturnsOne()
    {
        using var workspace = new TestWorkspace();
        var custom = workspace.CreateMode(
            "zeta",
            phases:
            [
                new ModePhaseManifest { Name = "zz-draft", Kind = PhaseKind.Main, Prompt = "prompts/main.md" },
                new ModePhaseManifest { Name = "aa-review", Kind = PhaseKind.Critic, Prompt = "prompts/critic.md" },
                new ModePhaseManifest { Name = "mm-final", Kind = PhaseKind.Revision, Prompt = "prompts/revision.md" }
            ]);
        var manifestPath = Path.Combine(custom, "mode.json");
        File.WriteAllText(
            manifestPath,
            System.Text.RegularExpressions.Regex.Replace(
                File.ReadAllText(manifestPath), @"""version"":\s*1", "\"version\": 7"));
        var broken = workspace.CreateMode("alpha");
        File.WriteAllText(Path.Combine(broken, "mode.json"), "{ not json");
        var missing = workspace.CreateMode("beta");
        File.Delete(Path.Combine(missing, "prompts", "critic.md"));
        File.WriteAllText(Path.Combine(workspace.ModesRoot, "stray.txt"), "not a mode");
        var before = Snapshot(workspace.Root);

        var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Mode: zeta", result.Out);
        Assert.Contains("Version: 7", result.Out);
        Assert.Contains("Description: Test mode.", result.Out);
        Assert.Contains("Phases: zz-draft (main), aa-review (critic), mm-final (revision)", result.Out);
        Assert.DoesNotContain("alpha", result.Out);
        Assert.DoesNotContain("stray", result.Err);
        Assert.Contains("Mode 'alpha' is invalid:", result.Err);
        Assert.Contains("mode.json", result.Err);
        Assert.Contains("Mode 'beta' is invalid:", result.Err);
        Assert.Contains("prompts/critic.md", result.Err);
        Assert.Equal(before, Snapshot(workspace.Root));
        Assert.Empty(Directory.GetFileSystemEntries(workspace.OutputRoot));
    }

    [Fact]
    public async Task List_InconsistentDocumentation_ReportsInvalidModeAndListsValidModes()
    {
        using var workspace = new TestWorkspace();
        var invalid = workspace.CreateMode("alpha");
        ModeLoaderTests.WriteDocumentation(invalid, "# alpha\n\nmain critic revision\n");
        workspace.CreateMode("beta");

        var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("alpha", result.Out);
        Assert.Contains("Mode: beta", result.Out);
        Assert.Contains("Mode 'alpha' is invalid:", result.Err);
        Assert.Contains("MODE.md", result.Err);
        Assert.Contains("missing output headings '## Problem'", result.Err);
    }

    [Fact]
    public async Task List_UnsupportedManifestSchemaVersion_ReportsInvalidModeAndListsValidModes()
    {
        using var workspace = new TestWorkspace();
        var invalid = workspace.CreateMode("alpha");
        var manifestPath = Path.Combine(invalid, "mode.json");
        var json = File.ReadAllText(manifestPath);
        Assert.Contains("\"schemaVersion\": 1", json);
        File.WriteAllText(manifestPath, json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2"));
        workspace.CreateMode("beta");

        var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Out.Split("Mode: ").Length - 1);
        Assert.Contains("Mode: beta", result.Out);
        Assert.DoesNotContain("alpha", result.Out);
        Assert.Contains("Mode 'alpha' is invalid:", result.Err);
        Assert.Contains(
            "field 'schemaVersion' is 2; this runtime supports only schemaVersion 1",
            result.Err);
        Assert.DoesNotContain("beta", result.Err);
    }

    [Fact]
    public async Task List_DirectoryWithoutManifest_IsReportedAndLaterModesStillListed()
    {
        using var workspace = new TestWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.ModesRoot, "empty-dir"));
        workspace.CreateMode("later");

        var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Mode: later", result.Out);
        Assert.Contains("Mode 'empty-dir' is invalid:", result.Err);
    }

    [Fact]
    public async Task List_WhitespaceOnlyDirectoryName_IsReportedAndValidModesStillListed()
    {
        using var workspace = new TestWorkspace();
        // Windows trims trailing spaces from ordinary paths; the extended-length prefix keeps the name literal.
        var blank = OperatingSystem.IsWindows()
            ? @"\\?\" + Path.Combine(Path.GetFullPath(workspace.ModesRoot), " ")
            : Path.Combine(workspace.ModesRoot, " ");
        Directory.CreateDirectory(blank);
        try
        {
            Assert.Contains(" ", Directory.EnumerateDirectories(workspace.ModesRoot).Select(Path.GetFileName));
            workspace.CreateMode("valid");

            var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("Mode: valid", result.Out);
            Assert.Contains("Mode ' ' is invalid:", result.Err);
            Assert.Contains("contains unsupported characters", result.Err);
        }
        finally
        {
            Directory.Delete(blank);
        }
    }

    [Fact]
    public async Task List_RequiredLensWithMixedVariants_ListsUsableLensesAndReportsBrokenVariant()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "zeta", "alpha", "broken");
        File.Delete(Path.Combine(dir, "prompts", "broken", "critic.md"));
        Directory.CreateDirectory(Path.Combine(dir, "prompts", "bad.name"));

        var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(1, result.Out.Split("Mode: ").Length - 1);
        Assert.Contains("Lenses: alpha, zeta", result.Out);
        Assert.Contains("Mode 'lens' with lens 'broken' is invalid:", result.Err);
        Assert.Contains("prompts/broken/critic.md", result.Err);
        Assert.DoesNotContain("bad.name", result.Err);
    }

    [Fact]
    public async Task List_RequiredLensWithNoUsableVariants_ReturnsOneWithoutListingMode()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "alpha");
        File.Delete(Path.Combine(dir, "prompts", "alpha", "main.md"));

        var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Mode: lens", result.Out);
        Assert.Contains("with lens 'alpha'", result.Err);
    }

    [Fact]
    public async Task List_PartialDefaults_ReportsMissingPromptEvenWithValidLens()
    {
        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("lens");
        ModeLoaderTests.MoveDefaultPromptsToVariants(dir, "alpha");
        File.WriteAllText(Path.Combine(dir, "prompts", "main.md"), "default main");

        var result = await RunAsync("list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.DoesNotContain("Mode: lens", result.Out);
        Assert.Contains("prompts/critic.md", result.Err);
        Assert.DoesNotContain("Available lenses", result.Err);
    }

    [Fact]
    public async Task List_EmptyRootWithOnlyAFile_ReturnsZeroWithNoModesMessage()
    {
        using var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.ModesRoot, "notes.txt"), "x");

        var result = await RunAsync("LIST", "--MODES-ROOT", workspace.ModesRoot);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("No valid modes found", result.Out);
        Assert.Equal(string.Empty, result.Err);
    }

    [Fact]
    public async Task List_RootWithSpaces_Succeeds()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("frame");
        var spaced = Path.Combine(workspace.Root, "my modes");
        Directory.Move(workspace.ModesRoot, spaced);

        var result = await RunAsync("list", "--modes-root", spaced);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Mode: frame", result.Out);
    }

    [Fact]
    public async Task List_MissingRootAndFileRoot_ReturnOneNamingRoot()
    {
        using var workspace = new TestWorkspace();
        var missing = Path.Combine(workspace.Root, "nope");
        var file = Path.Combine(workspace.Root, "file.txt");
        File.WriteAllText(file, "x");

        foreach (var root in new[] { missing, file })
        {
            var result = await RunAsync("list", "--modes-root", root);

            Assert.Equal(1, result.ExitCode);
            Assert.Equal(string.Empty, result.Out);
            Assert.Contains("Modes root", result.Err);
            Assert.Contains(root, result.Err);
        }
    }

    [Fact]
    public async Task List_MalformedRootPath_ReturnsTwo()
    {
        var result = await RunAsync("list", "--modes-root", "bad\0path");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Invalid --modes-root", result.Err);
        Assert.Contains("modes list", result.Err);
    }

    [Fact]
    public async Task List_DefaultRoot_SucceedsInChildProcessWithBogusProvider()
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
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CognitiveRuntime.Cli.dll"));
        startInfo.ArgumentList.Add("modes");
        startInfo.ArgumentList.Add("list");
        startInfo.Environment["MODEL_PROVIDER"] = "bogus-provider";

        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(process.ExitCode == 0, await stderr);
        Assert.Contains("Mode: frame", await stdout);
        Assert.Equal(before, Snapshot(workspace.Root));
    }

    public static TheoryData<string[]> InvalidArguments => new()
    {
        new[] { "extra" },
        new[] { "--lens", "warcraft" },
        new[] { "--bogus" },
        new[] { "--modes-root" },
        new[] { "--modes-root", " " },
        new[] { "--modes-root", "--help" },
        new[] { "--modes-root", "-h" },
        new[] { "--modes-root", "a", "--modes-root", "b" },
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public async Task List_InvalidArguments_ReturnsTwoWithUsage(string[] rest)
    {
        var result = await RunAsync(["list", .. rest]);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains("Usage: modes list [--modes-root <path>]", result.Err);
        Assert.DoesNotContain("modes validate <mode>", result.Err);
    }

    [Fact]
    public async Task List_Help_ReturnsZeroAndDocumentsPartialFailure()
    {
        var result = await RunAsync("list", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Err);
        Assert.Contains("1    Some directories or lenses are invalid", result.Out);
        Assert.Contains("still listed", result.Out);
        Assert.DoesNotContain("modes validate <mode>", result.Out);
    }

    [Fact]
    public async Task ModesGroupHelp_ShowsBothSubcommands()
    {
        var result = await RunAsync("--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("modes validate <mode>", result.Out);
        Assert.Contains("modes list [--modes-root", result.Out);
    }

    [Fact]
    public async Task List_CancelledToken_Returns130WithoutRunOutput()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode("frame");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await RunWithTokenAsync(
            cts.Token, "list", "--modes-root", workspace.ModesRoot);

        Assert.Equal(130, result.ExitCode);
        Assert.Contains("cancelled", result.Err);
        Assert.DoesNotContain("is invalid", result.Err);
        Assert.Empty(Directory.GetFileSystemEntries(workspace.OutputRoot));
    }
}
