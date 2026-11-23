using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Evaluation;
using CognitiveRuntime.Core.Runtime;

namespace CognitiveRuntime.Tests;

public sealed class EvalSuiteRunnerTests
{
    private const string ContractCheck = "output contract satisfied";

    [Fact]
    public async Task RunAsync_ExecutesReverseOrderedInputInOrdinalSourcePathOrder()
    {
        using var suite = new Suite();
        var first = suite.Case("a", modesRoot: "root-a");
        var second = suite.Case("b", modesRoot: "root-b");
        var third = suite.Case("c", modesRoot: "root-c");

        var result = await suite.Runner.RunAsync([third, second, first]);

        Assert.Equal(["root-a", "root-b", "root-c"], suite.FactoryCalls);
        Assert.Equal([first, second, third], result.Cases.Select(caseResult => caseResult.Case));
        Assert.All(result.Cases, caseResult => Assert.Equal(EvalCaseStatus.Passed, caseResult.Status));
        Assert.Equal(3, result.Passed);
    }

    [Fact]
    public async Task RunAsync_OrdersBySourcePathWrittenWithForwardSlashesOnEveryOs()
    {
        using var suite = new Suite();
        // '0' sorts before '\' but after '/': ordered as written with '/', a Windows path and its Linux twin agree.
        var first = suite.Case("zero", modesRoot: "root-zero") with { SourcePath = @"a0\case.json" };
        var second = suite.Case("plain", modesRoot: "root-plain") with { SourcePath = @"a\case.json" };
        Assert.True(string.CompareOrdinal(first.SourcePath, second.SourcePath) < 0);
        Assert.True(
            string.CompareOrdinal(first.SourcePath.Replace('\\', '/'), second.SourcePath.Replace('\\', '/')) > 0);

        var result = await suite.Runner.RunAsync([second, first]);

        Assert.Equal(["root-plain", "root-zero"], suite.FactoryCalls);
        Assert.Equal([second, first], result.Cases.Select(caseResult => caseResult.Case));
        Assert.All(result.Cases, caseResult => Assert.Equal(EvalCaseStatus.Passed, caseResult.Status));
    }

    [Fact]
    public async Task RunAsync_ContinuesAfterMismatchAndMissingMode()
    {
        using var suite = new Suite();
        var wrongExpectation = suite.Case("a-wrong", evaluation: EvalCaseOutcome.Fail);
        var missingMode = suite.Case("b-missing") with { Mode = "absent" };
        var passing = suite.Case("c-passing");

        var result = await suite.Runner.RunAsync([wrongExpectation, missingMode, passing]);

        Assert.Equal((1, 1, 1), (result.Passed, result.Failed, result.Errored));

        var failed = result.Cases[0];
        Assert.Equal(EvalCaseStatus.Failed, failed.Status);
        Assert.Equal(EvalCaseOutcome.Pass, failed.ActualEvaluation);
        Assert.Null(failed.ErrorMessage);
        Assert.True(Assert.Single(failed.Checks).Matches);
        RuntimeTestHarness.AssertRequiredArtifactsExist(failed.OutputDirectory!);

        var error = result.Cases[1];
        Assert.Equal(EvalCaseStatus.Error, error.Status);
        Assert.Null(error.ActualEvaluation);
        Assert.Equal(
            new EvalCheckComparison(ContractCheck, EvalCaseOutcome.Pass, Actual: null),
            Assert.Single(error.Checks));
        // The orchestrator's message without its "Run '<id>' failed: " prefix.
        Assert.StartsWith("Mode 'absent' was not found under ", error.ErrorMessage, StringComparison.Ordinal);
        Assert.StartsWith(
            suite.OutputRootOf("absent", "b-missing") + Path.DirectorySeparatorChar,
            error.OutputDirectory,
            StringComparison.Ordinal);

        var passed = result.Cases[2];
        Assert.Equal(EvalCaseStatus.Passed, passed.Status);
        Assert.Equal(EvalCaseOutcome.Pass, passed.ActualEvaluation);
        Assert.Null(passed.ErrorMessage);
        Assert.Equal(suite.OutputRootOf("frame", "c-passing"), Path.GetDirectoryName(passed.OutputDirectory));
        RuntimeTestHarness.AssertRequiredArtifactsExist(passed.OutputDirectory!);
        Assert.True(File.Exists(Path.Combine(passed.OutputDirectory!, "pattern.md")));
    }

    [Fact]
    public async Task RunAsync_MissingAssertedCheckIsAMismatch()
    {
        using var suite = new Suite();
        // Inserted out of order: the comparisons come back in ordinal name order, capitals first.
        var checks = new Dictionary<string, EvalCaseOutcome>
        {
            ["zz not a check"] = EvalCaseOutcome.Fail,
            [ContractCheck] = EvalCaseOutcome.Pass,
            ["Output Contract Satisfied"] = EvalCaseOutcome.Pass
        };

        var result = await suite.Runner.RunAsync([suite.Case("missing-check", checks: checks)]);

        var caseResult = Assert.Single(result.Cases);
        Assert.Equal(EvalCaseStatus.Failed, caseResult.Status);
        Assert.Equal(EvalCaseOutcome.Pass, caseResult.ActualEvaluation);
        Assert.Null(caseResult.ErrorMessage);
        Assert.Equal(
            [
                new EvalCheckComparison("Output Contract Satisfied", EvalCaseOutcome.Pass, Actual: null),
                new EvalCheckComparison(ContractCheck, EvalCaseOutcome.Pass, EvalCaseOutcome.Pass),
                new EvalCheckComparison("zz not a check", EvalCaseOutcome.Fail, Actual: null)
            ],
            caseResult.Checks);
        Assert.Equal((0, 1, 0), (result.Passed, result.Failed, result.Errored));
    }

    [Fact]
    public async Task RunAsync_StopsOnlyForSuiteCancellation()
    {
        using var suite = new Suite();
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        suite.BeforeCreate = modesRoot =>
        {
            if (modesRoot == "root-a")
            {
                throw new OperationCanceledException("Cancelled elsewhere.", unrelated.Token);
            }
        };

        var continued = await suite.Runner.RunAsync(
            [suite.Case("a", modesRoot: "root-a"), suite.Case("b", modesRoot: "root-b")]);

        Assert.Equal(EvalCaseStatus.Error, continued.Cases[0].Status);
        Assert.Equal("Cancelled elsewhere.", continued.Cases[0].ErrorMessage);
        Assert.Null(continued.Cases[0].OutputDirectory);
        Assert.Equal(EvalCaseStatus.Passed, continued.Cases[1].Status);

        using var suiteCancellation = new CancellationTokenSource();
        suite.FactoryCalls.Clear();
        suite.BeforeCreate = _ => suiteCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => suite.Runner.RunAsync(
                [suite.Case("c", modesRoot: "root-c"), suite.Case("d", modesRoot: "root-d")],
                suiteCancellation.Token));

        // The in-flight case was cancelled and the next one never started.
        Assert.Equal(["root-c"], suite.FactoryCalls);
    }

    [Fact]
    public async Task RunAsync_EmptySuiteReturnsNoResultsUnlessAlreadyCancelled()
    {
        using var suite = new Suite();

        var result = await suite.Runner.RunAsync([]);

        Assert.Empty(result.Cases);
        Assert.Equal((0, 0, 0), (result.Passed, result.Failed, result.Errored));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => suite.Runner.RunAsync([], new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData("..", "case")]
    [InlineData("frame", "..")]
    [InlineData("frame", "../escape")]
    [InlineData("frame", "nested\\case")]
    [InlineData("/rooted", "case")]
    [InlineData("frame", "C:case")]
    [InlineData("frame", "NUL.txt")]
    [InlineData("frame", "trailing.")]
    [InlineData("frame", "what?")]
    [InlineData("frame", " ")]
    public async Task RunAsync_RejectsUnsafeOutputComponents(string mode, string name)
    {
        using var suite = new Suite();
        var unsafeCase = suite.Case("a") with { Mode = mode, Name = name };

        var result = await suite.Runner.RunAsync([unsafeCase, suite.Case("b")]);

        var error = result.Cases[0];
        Assert.Equal(EvalCaseStatus.Error, error.Status);
        Assert.Null(error.ActualEvaluation);
        Assert.Null(error.OutputDirectory);
        Assert.Contains("cannot be used as an output directory name", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Equal(EvalCaseStatus.Passed, result.Cases[1].Status);
        // The orchestrator was created for the valid case only, and nothing escaped the suite output root.
        Assert.Single(suite.FactoryCalls);
        Assert.Equal(["frame"], suite.OutputEntries());
    }

    /// <summary>A workspace with a passing <c>frame</c> mode and a runner whose factory records its calls.</summary>
    private sealed class Suite : IDisposable
    {
        private readonly TestWorkspace _workspace = new();
        private readonly FixedTimeProvider _clock = new(new DateTimeOffset(2026, 6, 14, 12, 0, 0, TimeSpan.Zero));

        public Suite()
        {
            _workspace.CreateMode();
            Runner = new EvalSuiteRunner(CreateOrchestrator, _workspace.OutputRoot);
        }

        public EvalSuiteRunner Runner { get; }

        /// <summary>The modes root of every factory call, in call order.</summary>
        public List<string> FactoryCalls { get; } = [];

        public Action<string>? BeforeCreate { get; set; }

        public string OutputRootOf(string mode, string name) => Path.Combine(_workspace.OutputRoot, mode, name);

        public string[] OutputEntries() =>
            Directory.EnumerateFileSystemEntries(_workspace.OutputRoot).Select(Path.GetFileName).ToArray()!;

        /// <summary>
        /// A case built directly, as the loader would never allow some of these. <paramref name="modesRoot"/>
        /// only labels the factory call; every case loads the workspace's modes.
        /// </summary>
        public EvalCase Case(
            string name,
            string modesRoot = "modes",
            EvalCaseOutcome evaluation = EvalCaseOutcome.Pass,
            IReadOnlyDictionary<string, EvalCaseOutcome>? checks = null) =>
            new(
                Path.Combine(_workspace.Root, "cases", "frame", name + ".json"),
                name,
                "Test case.",
                "frame",
                Lens: null,
                EvalCaseLoader.DefaultPattern,
                "Frame a deterministic eval suite runner for the runtime.",
                InputFile: null,
                modesRoot,
                new EvalCaseExpectation(
                    evaluation,
                    checks ?? new Dictionary<string, EvalCaseOutcome> { [ContractCheck] = EvalCaseOutcome.Pass }));

        public void Dispose() => _workspace.Dispose();

        private Orchestrator CreateOrchestrator(string modesRoot)
        {
            FactoryCalls.Add(modesRoot);
            BeforeCreate?.Invoke(modesRoot);
            return RuntimeTestHarness.CreateOrchestrator(_workspace, _clock);
        }
    }
}
