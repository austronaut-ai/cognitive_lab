using System.Text.Json;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Evaluation;
using CognitiveRuntime.Core.Tracing;

namespace CognitiveRuntime.Tests;

/// <summary>
/// Keeps the checked-in corpus under <c>examples/evals</c> honest: every case loads, and every case's
/// expectations hold when <see cref="EvalSuiteRunner"/> runs it through the normal orchestrator, so the
/// corpus cannot drift from the runtime.
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
        using var workspace = new TestWorkspace();
        var cases = await LoadCorpusAsync();

        var suite = await CreateRunner(workspace, workspace.OutputRoot).RunAsync(cases);

        Assert.Equal(cases.Count, suite.Cases.Count);
        Assert.All(suite.Cases, result => Assert.True(result.Status == EvalCaseStatus.Passed, Describe(result)));
        Assert.Equal(cases.Count, suite.Passed);
        Assert.Equal(0, suite.Failed);
        Assert.Equal(0, suite.Errored);
        Assert.True(
            suite.Cases.Count(result => result.ActualEvaluation == EvalCaseOutcome.Fail) >= 2,
            "The corpus needs at least two intentionally failing evaluations.");

        foreach (var result in suite.Cases)
        {
            var context = Describe(result);
            Assert.NotNull(result.OutputDirectory);
            Assert.Equal(
                Path.Combine(workspace.OutputRoot, result.Case.Mode, result.Case.Name),
                Path.GetDirectoryName(result.OutputDirectory));
            Assert.Equal(result.Case.Expect.Evaluation, result.ActualEvaluation);
            if (result.ActualEvaluation == EvalCaseOutcome.Fail)
            {
                var report = await File.ReadAllTextAsync(Path.Combine(result.OutputDirectory, "eval_report.md"));
                AssertFailsOnlyForTheMissingFixtureHeading(report, $"{context} Report:{Environment.NewLine}{report}");
            }

            await AssertCompletedRunArtifactsAsync(result.OutputDirectory, context);
        }
    }

    [Fact]
    public async Task RunAsync_CheckedInCorpusProducesEqualResultsAcrossRuns()
    {
        using var workspace = new TestWorkspace();
        var cases = await LoadCorpusAsync();

        var first = await CreateRunner(workspace, Path.Combine(workspace.OutputRoot, "first")).RunAsync(cases);
        var second = await CreateRunner(workspace, Path.Combine(workspace.OutputRoot, "second")).RunAsync(cases);

        Assert.Equal(first.Passed, second.Passed);
        Assert.Equal(first.Failed, second.Failed);
        Assert.Equal(first.Errored, second.Errored);
        Assert.Equal(first.Cases.Count, second.Cases.Count);
        // Record equality compares a list member by reference, so the comparisons are compared as sequences.
        Assert.All(
            first.Cases.Zip(second.Cases),
            pair =>
            {
                Assert.Equal(pair.First.Case, pair.Second.Case);
                Assert.Equal(pair.First.Status, pair.Second.Status);
                Assert.Equal(pair.First.ActualEvaluation, pair.Second.ActualEvaluation);
                Assert.Equal(pair.First.Checks.AsEnumerable(), pair.Second.Checks);
                Assert.Equal(pair.First.ErrorMessage, pair.Second.ErrorMessage);
                Assert.NotEqual(pair.First.OutputDirectory, pair.Second.OutputDirectory);
            });
    }

    private static EvalSuiteRunner CreateRunner(TestWorkspace workspace, string outputRoot)
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero));
        return new EvalSuiteRunner(
            modesRoot => RuntimeTestHarness.CreateOrchestrator(workspace, clock, modesRoot: modesRoot),
            outputRoot);
    }

    private static string Describe(EvalCaseResult result) =>
        $"Case '{result.Case.SourcePath}' was {result.Status} (evaluation {result.ActualEvaluation}, " +
        $"error '{result.ErrorMessage}'). Checks: " +
        string.Join("; ", result.Checks.Select(check => $"{check.Name}: expected {check.Expected}, actual {check.Actual}")) +
        ".";

    private static string[] ReportLines(string report) =>
        report.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();

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
    private static async Task AssertCompletedRunArtifactsAsync(string outputDirectory, string context)
    {
        RuntimeTestHarness.AssertRequiredArtifactsExist(outputDirectory);
        Assert.True(File.Exists(Path.Combine(outputDirectory, "pattern.md")), context);
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(outputDirectory, "run.json")));
        Assert.Equal(JsonValueKind.Object, manifest.RootElement.ValueKind);

        var trace = await new JsonTraceReader().ReadAsync(Path.Combine(outputDirectory, "trace.json"));
        var integrity = new TerminalTraceIntegrityEvaluator().Evaluate(trace.Events);
        Assert.True(integrity.Passed, $"{integrity.Details} {context}");
        Assert.Contains(trace.Events, traceEvent => traceEvent.Type == TraceEventNames.RunCompleted);
    }
}
