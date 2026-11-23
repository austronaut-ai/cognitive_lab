using System.Text.Json;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Evaluation;
using CognitiveRuntime.Core.Tracing;

namespace CognitiveRuntime.Tests;

/// <summary>
/// Keeps the checked-in corpus under <c>examples/evals</c> honest: every case loads, and every case's
/// expectations hold against the eval report of a real mock-provider run, so the corpus cannot drift from
/// the runtime. This is a test, not the reusable suite runner.
/// </summary>
public sealed class EvalCaseCorpusTests
{
    private static readonly string RepositoryRoot = Path.GetDirectoryName(CheckedInModeCases.ModesRoot)!;

    private static readonly string CasesRoot = Path.Combine(RepositoryRoot, "examples", "evals");

    private static Task<IReadOnlyList<EvalCase>> LoadCorpusAsync() =>
        new EvalCaseLoader(CasesRoot, CheckedInModeCases.ModesRoot).LoadDirectoryAsync();

    [Fact]
    public async Task LoadDirectoryAsync_LoadsEveryCheckedInCase()
    {
        var cases = await LoadCorpusAsync();

        var fixturesPrefix = EvalCaseLoader.FixturesDirectoryName + Path.DirectorySeparatorChar;
        var caseFiles = Directory
            .EnumerateFiles(CasesRoot, "*.json", SearchOption.AllDirectories)
            .Where(path => !Path.GetRelativePath(CasesRoot, path).StartsWith(fixturesPrefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(caseFiles, cases.Select(evalCase => evalCase.SourcePath).Order(StringComparer.Ordinal));

        var passing = cases.Where(evalCase => evalCase.Expect.Evaluation == EvalCaseOutcome.Pass).ToArray();
        Assert.All(
            new (string Mode, string? Lens)[]
            {
                ("frame", null), ("challenge", null), ("synthesize", null), ("lens", "warcraft")
            },
            shipped => Assert.Contains(
                passing,
                evalCase => evalCase.Mode == shipped.Mode &&
                    evalCase.Lens == shipped.Lens &&
                    evalCase.ModesRoot == CheckedInModeCases.ModesRoot));
        Assert.True(
            cases.Count(evalCase => evalCase.Expect.Evaluation == EvalCaseOutcome.Fail) >= 2,
            "The corpus needs at least two intentionally failing cases.");
        // Layout: examples/evals/<mode>/<case-name>.json.
        Assert.All(
            cases,
            evalCase => Assert.Equal(Path.Combine(CasesRoot, evalCase.Mode), Path.GetDirectoryName(evalCase.SourcePath)));
    }

    [Fact]
    public async Task RunAsync_CheckedInCasesMatchEvalReports()
    {
        foreach (var evalCase in await LoadCorpusAsync())
        {
            await RunAndAssertAsync(evalCase);
        }
    }

    private static async Task RunAndAssertAsync(EvalCase evalCase)
    {
        using var workspace = new TestWorkspace();
        var orchestrator = RuntimeTestHarness.CreateOrchestrator(
            workspace,
            new FixedTimeProvider(new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero)),
            modesRoot: evalCase.ModesRoot);

        var result = await orchestrator.RunAsync(
            new RunRequest(
                evalCase.Mode,
                evalCase.Input,
                "mock",
                workspace.OutputRoot,
                InputSource: evalCase.InputFile,
                Lens: evalCase.Lens,
                Pattern: evalCase.Pattern));

        var report = await File.ReadAllTextAsync(result.EvalReportPath);
        var context = $"Case '{evalCase.SourcePath}'. Report:{Environment.NewLine}{report}";
        var expectPass = evalCase.Expect.Evaluation == EvalCaseOutcome.Pass;

        Assert.True(result.Outcome == (expectPass ? RunOutcome.Success : RunOutcome.EvalFailed), context);
        Assert.True(
            report.Contains($"Overall: **{(expectPass ? "PASS" : "FAIL")}**", StringComparison.Ordinal),
            context);
        foreach (var (name, expected) in evalCase.Expect.Checks)
        {
            var rows = FindCheckRows(report, name);
            Assert.True(rows.Count == 1, $"Expected exactly one '{name}' row, found {rows.Count}. {context}");
            Assert.True(
                rows[0] == (expected == EvalCaseOutcome.Pass),
                $"Check '{name}' was expected to {expected}. {context}");
        }

        if (!expectPass)
        {
            AssertFailsOnlyForTheMissingFixtureHeading(report, context);
        }

        await AssertCompletedRunArtifactsAsync(result, context);
    }

    private static string[] ReportLines(string report) =>
        report.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

    /// <summary>The pass or fail state of every report row named exactly <paramref name="name"/> (ordinal).</summary>
    private static List<bool> FindCheckRows(string report, string name) =>
        ReportLines(report)
            .Where(line => line.StartsWith($"- [x] {name}: ", StringComparison.Ordinal) ||
                line.StartsWith($"- [ ] {name}: ", StringComparison.Ordinal))
            .Select(line => line[3] == 'x')
            .ToList();

    /// <summary>
    /// An intentional failure must fail for its known reason and no other: the contract check is the only
    /// failed row, and the fixture mode's extra heading is the only thing it reports.
    /// </summary>
    private static void AssertFailsOnlyForTheMissingFixtureHeading(string report, string context)
    {
        var failedRows = ReportLines(report)
            .Where(line => line.StartsWith("- [ ] ", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            failedRows is ["- [ ] output contract satisfied: Missing headings: ## Risks."],
            context);
    }

    /// <summary>A failed evaluation is still a completed run: every artifact, a manifest, and a sound trace.</summary>
    private static async Task AssertCompletedRunArtifactsAsync(RunResult result, string context)
    {
        RuntimeTestHarness.AssertRequiredArtifactsExist(result.OutputDirectory);
        Assert.True(File.Exists(Path.Combine(result.OutputDirectory, "pattern.md")), context);
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "run.json")));
        Assert.Equal(JsonValueKind.Object, manifest.RootElement.ValueKind);

        var trace = await new JsonTraceReader().ReadAsync(result.TracePath);
        var integrity = new TerminalTraceIntegrityEvaluator().Evaluate(trace.Events);
        Assert.True(integrity.Passed, $"{integrity.Details} {context}");
        Assert.Contains(trace.Events, traceEvent => traceEvent.Type == TraceEventNames.RunCompleted);
    }
}
