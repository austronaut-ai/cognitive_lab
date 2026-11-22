using CognitiveRuntime.Core.Artifacts;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Evaluation;
using CognitiveRuntime.Core.Modes;

namespace CognitiveRuntime.Tests;

public sealed class EvalRunnerTests
{
    [Fact]
    public async Task EvaluateAsync_FailsWhenOutputContractHeadingIsMissing()
    {
        using var workspace = new TestWorkspace();
        var writer = new ArtifactWriter(TimeProvider.System);
        var artifacts = await writer.PrepareRunAsync(
            workspace.OutputRoot,
            "frame",
            "12345678");

        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            await writer.WriteAsync(artifacts, kind, "content");
        }

        await File.WriteAllTextAsync(artifacts.TracePath, "{}");
        await writer.WriteAsync(
            artifacts,
            ArtifactKind.Result,
            """
            # Frame Result

            ## Authoritative Revision

            ## Problem

            A sufficiently long revision without the objective heading.

            ## Critic Review

            ## Objective

            This heading appears only in the critic appendix.
            """);

        var mode = CreateMode(
            workspace.Root,
            new OutputContract { RequiredHeadings = ["## Problem", "## Objective"], MinimumLength = 20 });
        var events = new[]
        {
            CreateEvent(TraceEventNames.RunStarted),
            CreateEvent(TraceEventNames.CriticCompleted),
            CreateEvent(TraceEventNames.RevisionCompleted),
            CreateEvent(TraceEventNames.RunCompleted)
        };
        var phaseResults = new[]
        {
            CreatePhaseResult(PhaseKind.Main, "draft"),
            CreatePhaseResult(PhaseKind.Critic, "## Objective\n\nCritic appendix."),
            CreatePhaseResult(
                PhaseKind.Revision,
                "## Problem\n\nA sufficiently long revision without the objective heading.")
        };
        var runner = new EvalRunner(new OutputContractValidator());

        var report = await runner.EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                events,
                phaseResults,
                CreateCriticRevisionPlan()));

        Assert.False(report.Passed);
        var contractCheck = Assert.Single(
            report.Checks,
            check => check.Name == "output contract satisfied");
        Assert.False(contractCheck.Passed);
        Assert.Contains("## Objective", contractCheck.Details);
    }

    [Fact]
    public async Task EvaluateAsync_FailsWhenRevisionIsEmpty()
    {
        using var workspace = new TestWorkspace();
        var writer = new ArtifactWriter(TimeProvider.System);
        var artifacts = await writer.PrepareRunAsync(
            workspace.OutputRoot,
            "frame",
            "12345678");

        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            await writer.WriteAsync(artifacts, kind, "content");
        }

        await File.WriteAllTextAsync(artifacts.TracePath, "{}");

        var mode = CreateMode(
            workspace.Root,
            new OutputContract { RequiredHeadings = ["## Problem"], MinimumLength = 20 });
        var events = new[]
        {
            CreateEvent(TraceEventNames.RunStarted),
            CreateEvent(TraceEventNames.CriticCompleted),
            CreateEvent(TraceEventNames.RevisionCompleted),
            CreateEvent(TraceEventNames.RunCompleted)
        };
        var phaseResults = new[]
        {
            CreatePhaseResult(PhaseKind.Main, "draft"),
            CreatePhaseResult(PhaseKind.Critic, "critic"),
            CreatePhaseResult(PhaseKind.Revision, string.Empty)
        };
        var runner = new EvalRunner(new OutputContractValidator());

        var report = await runner.EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                events,
                phaseResults,
                CreateCriticRevisionPlan()));

        Assert.False(report.Passed);
        var revisionCheck = Assert.Single(
            report.Checks,
            check => check.Name == "revision is not empty");
        Assert.False(revisionCheck.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_SinglePassValidatesMainAsAuthoritativeOutput()
    {
        using var workspace = new TestWorkspace();
        var writer = new ArtifactWriter(TimeProvider.System);
        var artifacts = await writer.PrepareRunAsync(
            workspace.OutputRoot,
            "frame",
            "12345678");

        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            await writer.WriteAsync(artifacts, kind, "content");
        }

        await File.WriteAllTextAsync(artifacts.TracePath, "{}");
        const string mainContent = """
            ## Problem

            Bound the runtime problem.

            ## Objective

            Produce an inspectable result.
            """;
        var mode = CreateMode(
            workspace.Root,
            new OutputContract { RequiredHeadings = ["## Problem", "## Objective"], MinimumLength = 20 });
        var events = new[]
        {
            CreateEvent(TraceEventNames.RunStarted),
            CreateEvent(TraceEventNames.ModelCompleted, "main"),
            CreateEvent(TraceEventNames.RunCompleted)
        };
        var phaseResults = new[]
        {
            CreatePhaseResult(PhaseKind.Main, mainContent)
        };
        var runner = new EvalRunner(new OutputContractValidator());

        var report = await runner.EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                events,
                phaseResults,
                new EvalPlan(
                    [PhaseKind.Main],
                    PhaseKind.Main,
                    EvaluateLoopEfficacy: false)));

        Assert.True(report.Passed);
        Assert.Contains(
            report.Checks,
            check => check.Name == "main phase ran" && check.Passed);
        Assert.Contains(
            report.Checks,
            check => check.Name == "main is not empty" && check.Passed);
        Assert.DoesNotContain(
            report.Checks,
            check => check.Name == "loop responded to critic findings");
    }

    [Fact]
    public async Task EvaluateAsync_RequiredArtifactsCheckUsesLedgerAndFailsOnFailedArtifact()
    {
        using var workspace = new TestWorkspace();
        var writer = new ArtifactWriter(TimeProvider.System);
        var artifacts = await writer.PrepareRunAsync(
            workspace.OutputRoot,
            "frame",
            "12345678");

        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            await writer.WriteAsync(artifacts, kind, "content");
        }

        await File.WriteAllTextAsync(artifacts.TracePath, "{}");
        var mode = CreateMode(
            workspace.Root,
            new OutputContract { RequiredHeadings = ["## Problem"], MinimumLength = 5 });
        var events = new[]
        {
            CreateEvent(TraceEventNames.RunStarted),
            CreateEvent(TraceEventNames.ModelCompleted, "main"),
            CreateEvent(TraceEventNames.RunCompleted)
        };
        var phaseResults = new[]
        {
            CreatePhaseResult(PhaseKind.Main, "## Problem\n\nBound the problem.")
        };
        // A planned eval report is acceptable (produced after this check), but a
        // failed artifact write makes the check fail without any placeholder text.
        var ledger = new[]
        {
            new RunArtifactState("result.md", RunArtifactStatus.Written),
            new RunArtifactState("eval_report.md", RunArtifactStatus.Planned),
            new RunArtifactState("run_summary.md", RunArtifactStatus.Failed)
        };
        var runner = new EvalRunner(new OutputContractValidator());

        var report = await runner.EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                events,
                phaseResults,
                new EvalPlan(
                    [PhaseKind.Main],
                    PhaseKind.Main,
                    EvaluateLoopEfficacy: false),
                ArtifactLedger: ledger));

        var artifactCheck = Assert.Single(
            report.Checks,
            check => check.Name == "required artifacts exist");
        Assert.False(artifactCheck.Passed);
        Assert.Contains("run_summary.md", artifactCheck.Details);
    }

    private static readonly OutputContract ModeContract = new()
    {
        RequiredHeadings = ["## Problem"],
        MinimumLength = 5
    };

    private static readonly OutputContract DraftContract = new()
    {
        RequiredHeadings = ["## Draft", "## Decision"],
        MinimumLength = 5
    };

    private static readonly OutputContract CriticContract = new()
    {
        RequiredHeadings = ["## Findings"],
        MinimumLength = 5
    };

    private static readonly OutputContract RevisionContract = new()
    {
        RequiredHeadings = ["## Decision"],
        MinimumLength = 5
    };

    private const string DraftContent =
        "## Draft\n\nInitial framing.\n\n## Decision\n\nUndecided for now.";

    private const string CriticFindingContent =
        "## Findings\n\n- [Decision] State a concrete decision.";

    private const string RevisionContent =
        "## Decision\n\nAdopt the boring runtime primitive.";

    [Fact]
    public async Task EvaluateAsync_UsesRevisionContractForFinalOutputAndLoopEfficacy()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var mode = CreateMode(
            workspace.Root,
            ModeContract,
            main: DraftContract,
            critic: CriticContract,
            revision: RevisionContract);

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                CriticRevisionEvents(),
                CriticRevisionResults(DraftContent, CriticFindingContent, RevisionContent),
                CreateCriticRevisionPlan()));

        // The revision lacks the mode-level "## Problem", so passing proves the
        // final check used the revision's own contract.
        Assert.True(report.Passed, EvalRunner.RenderMarkdown(report));
        Assert.Contains(report.Checks, check => check.Name == "output contract satisfied" && check.Passed);
        Assert.Contains(report.Checks, check => check.Name == "loop responded to critic findings" && check.Passed);
        Assert.Contains(report.Checks, check => check.Name == "output contract satisfied for phase main" && check.Passed);
        Assert.Contains(report.Checks, check => check.Name == "output contract satisfied for phase critic" && check.Passed);
        Assert.Contains(report.Checks, check => check.Name == "output contract satisfied for phase revision" && check.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsPhaseContractFailuresIndependently()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var mode = CreateMode(workspace.Root, ModeContract, critic: CriticContract);

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                CriticRevisionEvents(),
                CriticRevisionResults(
                    "## Problem\n\nInitial framing.",
                    "No findings heading at all.",
                    "## Problem\n\nRevised framing of the problem."),
                new EvalPlan(
                    [PhaseKind.Main, PhaseKind.Critic, PhaseKind.Revision],
                    PhaseKind.Revision,
                    EvaluateLoopEfficacy: false)));

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, check => check.Name == "output contract satisfied" && check.Passed);
        var criticCheck = Assert.Single(
            report.Checks,
            check => check.Name == "output contract satisfied for phase critic");
        Assert.False(criticCheck.Passed);
        Assert.Contains("## Findings", criticCheck.Details);
        // Inherited contracts get no standalone check.
        Assert.DoesNotContain(report.Checks, check => check.Name == "output contract satisfied for phase main");
        Assert.DoesNotContain(report.Checks, check => check.Name == "output contract satisfied for phase revision");
    }

    [Fact]
    public async Task EvaluateAsync_DeclaredRevisionViolationFailsStandaloneAndFinalChecks()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var mode = CreateMode(workspace.Root, ModeContract, revision: RevisionContract);

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                CriticRevisionEvents(),
                CriticRevisionResults(
                    "## Problem\n\nInitial framing.",
                    "critic",
                    "## Problem\n\nThe revision dropped its decision."),
                new EvalPlan(
                    [PhaseKind.Main, PhaseKind.Critic, PhaseKind.Revision],
                    PhaseKind.Revision,
                    EvaluateLoopEfficacy: false)));

        Assert.False(report.Passed);
        var finalCheck = Assert.Single(report.Checks, check => check.Name == "output contract satisfied");
        var revisionCheck = Assert.Single(
            report.Checks,
            check => check.Name == "output contract satisfied for phase revision");
        Assert.False(finalCheck.Passed);
        Assert.False(revisionCheck.Passed);
        Assert.Contains("## Decision", finalCheck.Details);
        Assert.Contains("## Decision", revisionCheck.Details);
    }

    [Fact]
    public async Task EvaluateAsync_UsesOwningPhaseContractForEachExecutedNode()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var stageZeroContract = new OutputContract { RequiredHeadings = ["## Stage Zero"], MinimumLength = 5 };
        var finalContract = new OutputContract { RequiredHeadings = ["## Final"], MinimumLength = 5 };
        var stageZero = CreateMode(workspace.Root, ModeContract, main: stageZeroContract);
        var finalMode = CreateMode(
            workspace.Root,
            ModeContract,
            main: finalContract,
            critic: CriticContract);
        var stageZeroResult = new PhaseResult(
            "main", PhaseKind.Main, "## Stage Zero\n\nFirst stage.", "test", null);
        var finalResult = new PhaseResult(
            "main", PhaseKind.Main, "## Final\n\nLast stage output.", "test", null);

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                finalMode,
                SinglePassEvents(),
                [finalResult],
                new EvalPlan(
                    [PhaseKind.Main],
                    PhaseKind.Main,
                    EvaluateLoopEfficacy: false,
                    new EvalExecutionPlan(
                        [
                            new EvalExecutionNode("stage-0-main", [], [], "stage-0"),
                            new EvalExecutionNode("stage-1-main", ["stage-0-main"], [], "stage-1")
                        ],
                        "stage-1-main")),
                ExecutedPhases:
                [
                    new EvalPhaseInput("stage-0-main", stageZero.Phases[0], stageZeroResult),
                    new EvalPhaseInput("stage-1-main", finalMode.Phases[0], finalResult)
                ]));

        // The authoritative main uses its declared contract, not the mode-level "## Problem".
        Assert.Contains(report.Checks, check => check.Name == "output contract satisfied" && check.Passed);
        // Each node is checked against its owning phase; repeated names carry the node ID.
        Assert.Contains(
            report.Checks,
            check => check.Name == "output contract satisfied for phase main (node stage-0-main)" &&
                check.Passed);
        Assert.Contains(
            report.Checks,
            check => check.Name == "output contract satisfied for phase main (node stage-1-main)" &&
                check.Passed);
        // The final mode declares a critic contract, but no critic ran.
        Assert.DoesNotContain(
            report.Checks,
            check => check.Name.StartsWith(
                "output contract satisfied for phase critic",
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task EvaluateAsync_StageNodeViolationFailsOnlyItsOwnCheck()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var stageZeroContract = new OutputContract { RequiredHeadings = ["## Stage Zero"], MinimumLength = 5 };
        var stageZeroPhase = CreateMode(workspace.Root, ModeContract, main: stageZeroContract)
            .Phases[0] with { Name = "frame-main" };
        var finalMode = CreateMode(workspace.Root, ModeContract);
        var stageZeroResult = new PhaseResult(
            "frame-main", PhaseKind.Main, "## Problem\n\nWrong heading.", "test", null);
        var finalResult = new PhaseResult(
            "main", PhaseKind.Main, "## Problem\n\nLast stage output.", "test", null);

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                finalMode,
                SinglePassEvents(),
                [finalResult],
                new EvalPlan([PhaseKind.Main], PhaseKind.Main, EvaluateLoopEfficacy: false),
                ExecutedPhases:
                [
                    new EvalPhaseInput("stage-0-main", stageZeroPhase, stageZeroResult),
                    new EvalPhaseInput("stage-1-main", finalMode.Phases[0], finalResult)
                ]));

        Assert.False(report.Passed);
        // The final main inherits the mode contract: baseline check, no standalone check.
        Assert.Contains(report.Checks, check => check.Name == "output contract satisfied" && check.Passed);
        Assert.DoesNotContain(report.Checks, check => check.Name == "output contract satisfied for phase main");
        var stageCheck = Assert.Single(
            report.Checks,
            check => check.Name == "output contract satisfied for phase frame-main");
        Assert.False(stageCheck.Passed);
        Assert.Contains("## Stage Zero", stageCheck.Details);
    }

    [Fact]
    public async Task EvaluateAsync_FinalCheckUsesAuthoritativeNodeOutputAndContract()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var earlierContract = new OutputContract { RequiredHeadings = ["## Earlier"], MinimumLength = 5 };
        var earlierPhase = CreateMode(workspace.Root, ModeContract, revision: earlierContract)
            .Phases[2] with { Name = "first-revision" };
        var finalMode = CreateMode(workspace.Root, ModeContract, revision: RevisionContract);
        var earlierResult = new PhaseResult(
            "first-revision", PhaseKind.Revision, "## Earlier\n\nFirst pass revision.", "test", null);
        var finalResult = CreatePhaseResult(PhaseKind.Revision, RevisionContent);

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                finalMode,
                CriticRevisionEvents(),
                // The earlier revision comes first, so a kind-based lookup would
                // pair its output with the authoritative node's contract.
                [earlierResult, finalResult],
                new EvalPlan(
                    [],
                    PhaseKind.Revision,
                    EvaluateLoopEfficacy: false,
                    new EvalExecutionPlan(
                        [
                            new EvalExecutionNode("stage-0-revision", [], [], "stage-0"),
                            new EvalExecutionNode("stage-1-revision", ["stage-0-revision"], [], "stage-1")
                        ],
                        "stage-1-revision")),
                ExecutedPhases:
                [
                    new EvalPhaseInput("stage-0-revision", earlierPhase, earlierResult),
                    new EvalPhaseInput("stage-1-revision", finalMode.Phases[2], finalResult)
                ]));

        var finalCheck = Assert.Single(report.Checks, check => check.Name == "output contract satisfied");
        Assert.True(finalCheck.Passed, finalCheck.Details);
        Assert.Contains(report.Checks, check => check.Name == "revision is not empty" && check.Passed);
        Assert.Contains(
            report.Checks,
            check => check.Name == "output contract satisfied for phase first-revision" && check.Passed);
        Assert.Contains(
            report.Checks,
            check => check.Name == "output contract satisfied for phase revision" && check.Passed);
    }

    [Fact]
    public async Task EvaluateAsync_FinalCheckFailsWhenAuthoritativeNodeOutputViolatesItsContract()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var finalMode = CreateMode(workspace.Root, ModeContract, revision: RevisionContract);
        // The earlier result satisfies the authoritative contract; the authoritative one does not.
        var earlierResult = new PhaseResult(
            "first-revision", PhaseKind.Revision, RevisionContent, "test", null);
        var finalResult = CreatePhaseResult(PhaseKind.Revision, "## Problem\n\nNo decision here.");

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                finalMode,
                CriticRevisionEvents(),
                [earlierResult, finalResult],
                new EvalPlan(
                    [],
                    PhaseKind.Revision,
                    EvaluateLoopEfficacy: false,
                    new EvalExecutionPlan(
                        [
                            new EvalExecutionNode("stage-0-revision", [], [], "stage-0"),
                            new EvalExecutionNode("stage-1-revision", ["stage-0-revision"], [], "stage-1")
                        ],
                        "stage-1-revision")),
                ExecutedPhases:
                [
                    new EvalPhaseInput("stage-0-revision", finalMode.Phases[2] with { Name = "first-revision" }, earlierResult),
                    new EvalPhaseInput("stage-1-revision", finalMode.Phases[2], finalResult)
                ]));

        var finalCheck = Assert.Single(report.Checks, check => check.Name == "output contract satisfied");
        Assert.False(finalCheck.Passed);
        Assert.Contains("## Decision", finalCheck.Details);
    }

    [Fact]
    public async Task EvaluateAsync_UsesNodeResultMapForAuthoritativeRevision()
    {
        using var workspace = new TestWorkspace();
        var artifacts = await PrepareArtifactsAsync(workspace);
        var mode = CreateMode(workspace.Root, ModeContract);
        var main = CreatePhaseResult(PhaseKind.Main, "## Problem\n\nDraft output.");
        var critic = CreatePhaseResult(PhaseKind.Critic, CriticFindingContent);
        var compliantRevision = CreatePhaseResult(PhaseKind.Revision, "## Problem\n\nCompliant revision.");
        var authoritativeRevision = CreatePhaseResult(PhaseKind.Revision, "## Elsewhere\n\nViolating revision.");

        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            new EvalContext(
                artifacts,
                mode,
                CriticRevisionEvents(),
                [main, critic, compliantRevision, authoritativeRevision],
                new EvalPlan(
                    [PhaseKind.Main, PhaseKind.Critic, PhaseKind.Revision],
                    PhaseKind.Revision,
                    EvaluateLoopEfficacy: false,
                    new EvalExecutionPlan(
                        [
                            new EvalExecutionNode("main", [], [], null),
                            new EvalExecutionNode("critic-1", ["main"], [], null),
                            new EvalExecutionNode("revision-1", ["critic-1"], [], null),
                            new EvalExecutionNode("critic-2", ["revision-1"], [], null),
                            new EvalExecutionNode("revision-2", ["critic-2"], [], null)
                        ],
                        "revision-2")),
                NodeResultsById: new Dictionary<string, PhaseResult>(StringComparer.Ordinal)
                {
                    ["main"] = main,
                    ["critic-1"] = critic,
                    ["revision-1"] = compliantRevision,
                    ["critic-2"] = critic,
                    ["revision-2"] = authoritativeRevision
                }));

        // The plan names revision-2 as authoritative; the earlier compliant
        // revision must not satisfy the final check on its behalf.
        var finalCheck = Assert.Single(report.Checks, check => check.Name == "output contract satisfied");
        Assert.False(finalCheck.Passed);
        Assert.Contains("## Problem", finalCheck.Details);
        Assert.False(report.Passed);
    }

    private static async Task<RunArtifactPaths> PrepareArtifactsAsync(TestWorkspace workspace)
    {
        var writer = new ArtifactWriter(TimeProvider.System);
        var artifacts = await writer.PrepareRunAsync(workspace.OutputRoot, "frame", "12345678");
        foreach (var kind in Enum.GetValues<ArtifactKind>())
        {
            await writer.WriteAsync(artifacts, kind, "content");
        }

        await File.WriteAllTextAsync(artifacts.TracePath, "{}");
        return artifacts;
    }

    private static TraceEvent[] SinglePassEvents() =>
    [
        CreateEvent(TraceEventNames.RunStarted),
        CreateEvent(TraceEventNames.ModelCompleted, "main"),
        CreateEvent(TraceEventNames.RunCompleted)
    ];

    private static TraceEvent[] CriticRevisionEvents() =>
    [
        CreateEvent(TraceEventNames.RunStarted),
        CreateEvent(TraceEventNames.ModelCompleted, "main"),
        CreateEvent(TraceEventNames.CriticCompleted, "critic"),
        CreateEvent(TraceEventNames.RevisionCompleted, "revision"),
        CreateEvent(TraceEventNames.RunCompleted)
    ];

    private static PhaseResult[] CriticRevisionResults(string main, string critic, string revision) =>
    [
        CreatePhaseResult(PhaseKind.Main, main),
        CreatePhaseResult(PhaseKind.Critic, critic),
        CreatePhaseResult(PhaseKind.Revision, revision)
    ];

    /// <summary>
    /// Builds a mode whose phases carry contracts already resolved the way the
    /// loader resolves them, so EvalRunner never recomputes the defaults.
    /// </summary>
    private static LoadedMode CreateMode(
        string root,
        OutputContract modeContract,
        OutputContract? main = null,
        OutputContract? critic = null,
        OutputContract? revision = null) =>
        new(
            root,
            "# Test",
            new ModeManifest
            {
                Name = "frame",
                Description = "Test.",
                OutputContract = modeContract
            },
            [
                ResolvedPhase(PhaseKind.Main, modeContract, main),
                ResolvedPhase(PhaseKind.Critic, modeContract, critic),
                ResolvedPhase(PhaseKind.Revision, modeContract, revision)
            ]);

    private static LoadedPhase ResolvedPhase(
        PhaseKind kind,
        OutputContract modeContract,
        OutputContract? declared)
    {
        var name = kind.ToString().ToLowerInvariant();
        return new LoadedPhase(name, kind, $"{name} prompt")
        {
            OutputContract = declared ?? PhaseContractDefaults.Default(kind, modeContract),
            DeclaresOutputContract = declared is not null
        };
    }

    private static PhaseResult CreatePhaseResult(
        PhaseKind phaseKind,
        string content) =>
        new(
            phaseKind.ToString().ToLowerInvariant(),
            phaseKind,
            content,
            "test",
            "test-model");

    private static TraceEvent CreateEvent(string type, string? phase = null) =>
        new(
            1,
            DateTimeOffset.UtcNow,
            type,
            "run",
            phase is null
                ? new Dictionary<string, object?>()
                : new Dictionary<string, object?> { ["phase"] = phase });

    private static EvalPlan CreateCriticRevisionPlan() =>
        new(
            [PhaseKind.Main, PhaseKind.Critic, PhaseKind.Revision],
            PhaseKind.Revision,
            EvaluateLoopEfficacy: true);
}
