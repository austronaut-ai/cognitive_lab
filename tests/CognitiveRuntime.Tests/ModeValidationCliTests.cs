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
        new[] { "list" },
        new[] { "validate", "a", "b" },
        new[] { "validate", "frame", "--input", "x.txt" },
        new[] { "validate", "frame", "--modes-root" },
        new[] { "validate", "frame", "--modes-root", "a", "--modes-root", "b" },
        new[] { "validate", "--modes-root", "a" },
        new[] { "validate", " " },
        new[] { "list", "--help" },
        new[] { "validate", "frame", "--help" },
        new[] { "validate", "frame", "--modes-root", "--help" },
        new[] { "validate", "frame", "--modes-root", "-h" },
        new[] { "validate", "frame", "--modes-root", "-x" },
    };

    public static TheoryData<string[]> HelpArguments => new()
    {
        new[] { "modes", "validate", "--help" },
        new[] { "modes", "validate", "-h" },
        new[] { "modes", "--help" },
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
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return;
        }

        using var workspace = new TestWorkspace();
        var dir = workspace.CreateMode("frame");
        workspace.CreateMode("Frame");
        var path = Path.Combine(dir, "mode.json");
        File.WriteAllText(
            path,
            File.ReadAllText(path).Replace(
                "prompts/main.md",
                "../Frame/prompts/main.md"));

        var result = await RunAsync(
            "modes", "validate", "frame", "--modes-root", workspace.ModesRoot);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("escapes", result.Err);
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
        Assert.Contains("modes validate <mode>", result.Out);
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
