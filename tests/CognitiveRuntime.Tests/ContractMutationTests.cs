using System.Text.Json;
using CognitiveRuntime.Core.Abstractions;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Evaluation;
using CognitiveRuntime.Core.Models;
using CognitiveRuntime.Core.Tracing;

namespace CognitiveRuntime.Tests;

/// <summary>
/// Contract mutation fixtures: each row applies one named defect to an
/// otherwise passing mock frame run and names the only deterministic checks
/// allowed to fail, with the reason each must report. Every check the frame
/// evaluation reports has at least one row that fails it.
/// </summary>
public sealed class ContractMutationTests
{
    private const string RequiredArtifactsCheck = "required artifacts exist";
    private const string RunStartedCheck = "trace contains run.started";
    private const string RunCompletedCheck = "trace contains run.completed";
    private const string ResultNotEmptyCheck = "result is not empty";
    private const string MainPhaseCheck = "main phase ran";
    private const string CriticPhaseCheck = "critic phase ran";
    private const string RevisionPhaseCheck = "revision phase ran";
    private const string RevisionNotEmptyCheck = "revision is not empty";
    private const string OutputContractCheck = "output contract satisfied";
    private const string PlanExecutionCheck = "declared plan execution";
    private const string ModelCallPairingCheck = "model call pairing";
    private const string LoopEfficacyCheck = "loop responded to critic findings";
    private const string TerminalIntegrityCheck = "terminal trace integrity";

    /// <summary>
    /// Every check a frame evaluation reports, in report order: the
    /// <see cref="EvalRunner"/> checks, then the post-run terminal check.
    /// </summary>
    private static readonly string[] AllChecks =
    [
        RequiredArtifactsCheck,
        RunStartedCheck,
        RunCompletedCheck,
        ResultNotEmptyCheck,
        MainPhaseCheck,
        CriticPhaseCheck,
        RevisionPhaseCheck,
        RevisionNotEmptyCheck,
        OutputContractCheck,
        PlanExecutionCheck,
        ModelCallPairingCheck,
        LoopEfficacyCheck,
        TerminalIntegrityCheck
    ];

    private const string TargetHeading = "## Objective";
    private const string SwapHeading = "## Constraints";

    public enum Mutation
    {
        UnmutatedControl,
        MissingObjective,
        SwappedRequiredSections,
        DuplicateObjective,
        EmptyObjective,
        UnchangedRevision,
        MissingTerminalEvent,
        EventAfterTerminal,
        MissingRequiredArtifact,
        MissingRunStarted,
        MissingRunCompleted,
        EmptyResultFile,
        MissingMainCompletion,
        MissingCriticCompletion,
        MissingRevisionCompletion,
        UnpairedModelCall,
        MissingNodeTerminal,
        BlankAuthoritativeOutput
    }

    /// <summary>
    /// Mutation, the check names expected to fail, and (in the same order) a
    /// fragment each failing check's details must contain.
    /// </summary>
    public static TheoryData<Mutation, string[], string[]> Mutations =>
        new()
        {
            { Mutation.UnmutatedControl, [], [] },
            {
                Mutation.MissingObjective,
                [OutputContractCheck],
                ["Missing headings: ## Objective"]
            },
            {
                Mutation.SwappedRequiredSections,
                [OutputContractCheck],
                ["Required headings appear out of order"]
            },
            {
                Mutation.DuplicateObjective,
                [OutputContractCheck],
                ["Duplicate required headings: ## Objective"]
            },
            {
                Mutation.EmptyObjective,
                [OutputContractCheck],
                ["Empty required sections: ## Objective"]
            },
            {
                Mutation.UnchangedRevision,
                [LoopEfficacyCheck],
                ["The revision did not change any required section from the initial draft"]
            },
            {
                Mutation.MissingTerminalEvent,
                [TerminalIntegrityCheck],
                ["Trace has no terminal event"]
            },
            {
                Mutation.EventAfterTerminal,
                [TerminalIntegrityCheck],
                ["1 event(s) after terminal event 'run.finalized'"]
            },
            {
                Mutation.MissingRequiredArtifact,
                [RequiredArtifactsCheck],
                ["Missing artifacts: input.md"]
            },
            {
                Mutation.MissingRunStarted,
                [RunStartedCheck],
                ["Trace does not contain 'run.started'"]
            },
            {
                Mutation.MissingRunCompleted,
                [RunCompletedCheck],
                ["Trace does not contain 'run.completed'"]
            },
            {
                Mutation.EmptyResultFile,
                [ResultNotEmptyCheck],
                ["result.md is empty"]
            },
            {
                // Coupled: the main phase's completion event is its model
                // call's completion, so losing it also unpairs that call.
                Mutation.MissingMainCompletion,
                [MainPhaseCheck, ModelCallPairingCheck],
                [
                    "Trace does not contain 'model.completed' for phase 'main'",
                    ":main:1' has 0 terminal events; expected 1"
                ]
            },
            {
                Mutation.MissingCriticCompletion,
                [CriticPhaseCheck],
                ["Trace does not contain 'critic.completed' for phase 'critic'"]
            },
            {
                Mutation.MissingRevisionCompletion,
                [RevisionPhaseCheck],
                ["Trace does not contain 'revision.completed' for phase 'revision'"]
            },
            {
                Mutation.UnpairedModelCall,
                [ModelCallPairingCheck],
                [":critic:1' has 0 terminal events; expected 1"]
            },
            {
                Mutation.MissingNodeTerminal,
                [PlanExecutionCheck],
                ["Node 'critic' has 0 terminal events; expected 1"]
            },
            {
                // Coupled: a blank authoritative output cannot satisfy any
                // output contract, so both checks must fail together.
                Mutation.BlankAuthoritativeOutput,
                [RevisionNotEmptyCheck, OutputContractCheck],
                ["The authoritative revision is empty", "Result is shorter than 100 characters"]
            }
        };

    /// <summary>
    /// The acceptance rule itself: no check may be left without a fixture
    /// that makes it fail, so a check that always passes cannot stay green.
    /// </summary>
    [Fact]
    public void EveryDeterministicCheck_HasAFailingFixture()
    {
        var failedByFixtures = Mutations
            .SelectMany(row => (string[])row[1])
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(
            AllChecks.Order(StringComparer.Ordinal),
            failedByFixtures.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Mutations))]
    public async Task Evaluation_ReportsExactlyTheExpectedFailures(
        Mutation mutation,
        string[] expectedFailedChecks,
        string[] expectedDetailFragments)
    {
        Assert.Equal(expectedFailedChecks.Length, expectedDetailFragments.Length);
        var expected = expectedFailedChecks
            .Zip(expectedDetailFragments)
            .ToDictionary(pair => pair.First, pair => pair.Second);

        using var workspace = new TestWorkspace();
        workspace.CreateMode();
        var evalRunner = new CapturingEvalRunner();
        var orchestrator = RuntimeTestHarness.CreateOrchestrator(
            workspace,
            TimeProvider.System,
            new RevisionMutatingModelClient(mutation),
            evalRunner);

        var result = await orchestrator.RunAsync(
            new RunRequest(
                "frame",
                "Prove each deterministic eval fails for its intended reason.",
                "mock",
                workspace.OutputRoot));

        Assert.NotNull(result.Evaluation);
        var context = Assert.IsType<EvalContext>(evalRunner.Context);
        var trace = await new JsonTraceReader().ReadAsync(result.TracePath);
        var orchestratorReport = WithTerminalIntegrity(result.Evaluation, trace.Events);

        if (!IsPostRunMutation(mutation))
        {
            // Revision rows and the control: the real orchestrator evaluation
            // judges the authoritative revision the model client returned.
            Assert.Equal(
                expected.Count == 0 ? RunOutcome.Success : RunOutcome.EvalFailed,
                result.Outcome);
            AssertExactFailures(mutation, orchestratorReport, expected);
            foreach (var (checkName, fragment) in expected)
            {
                // The whole diagnostic is the single expected reason, so no
                // second validator complaint hides behind the same check name.
                Assert.Equal(
                    fragment + ".",
                    orchestratorReport.Checks.Single(check => check.Name == checkName).Details);
            }

            if (mutation != Mutation.UnmutatedControl)
            {
                return;
            }
        }

        // Post-run rows (and the control) re-evaluate the completed run from
        // disk. The baseline must be clean so the mutation is the only defect.
        Assert.Equal(RunOutcome.Success, result.Outcome);
        AssertExactFailures(
            Mutation.UnmutatedControl,
            orchestratorReport,
            new Dictionary<string, string>());

        await ApplyPostRunMutationAsync(mutation, context.Artifacts);
        var reevaluated = await ReevaluateAsync(
            mutation == Mutation.BlankAuthoritativeOutput
                ? WithBlankRevision(context)
                : context);

        AssertExactFailures(mutation, reevaluated, expected);
    }

    private static bool IsRevisionMutation(Mutation mutation) =>
        mutation is Mutation.MissingObjective
            or Mutation.SwappedRequiredSections
            or Mutation.DuplicateObjective
            or Mutation.EmptyObjective
            or Mutation.UnchangedRevision;

    private static bool IsPostRunMutation(Mutation mutation) =>
        mutation != Mutation.UnmutatedControl && !IsRevisionMutation(mutation);

    /// <summary>
    /// The runtime rejects empty model content before evaluation, so a blank
    /// authoritative output can only reach the evaluator through its context.
    /// Every view of the revision result is blanked consistently so the plan
    /// check still sees the authoritative node match the evaluated content.
    /// </summary>
    private static EvalContext WithBlankRevision(EvalContext context)
    {
        static PhaseResult Blank(PhaseResult result) =>
            result.PhaseKind == PhaseKind.Revision
                ? result with { Content = string.Empty }
                : result;

        Assert.Single(context.PhaseResults, result => result.PhaseKind == PhaseKind.Revision);
        return context with
        {
            PhaseResults = context.PhaseResults.Select(Blank).ToArray(),
            NodeResultsById = context.NodeResultsById?.ToDictionary(
                pair => pair.Key,
                pair => Blank(pair.Value),
                StringComparer.Ordinal),
            ExecutedPhases = context.ExecutedPhases?
                .Select(input => input with { Result = Blank(input.Result) })
                .ToArray(),
            AuthoritativeContent = string.Empty
        };
    }

    /// <summary>
    /// Re-runs the real <see cref="EvalRunner"/> over the orchestrator's own
    /// context, with the trace re-read from disk. The in-flight artifact
    /// ledger is dropped so every required file is checked on disk.
    /// </summary>
    private static async Task<EvalReport> ReevaluateAsync(EvalContext context)
    {
        var trace = await new JsonTraceReader().ReadAsync(context.Artifacts.TracePath);
        var report = await new EvalRunner(new OutputContractValidator()).EvaluateAsync(
            context with { TraceEvents = trace.Events, ArtifactLedger = null });

        return WithTerminalIntegrity(report, trace.Events);
    }

    /// <summary>
    /// Terminal integrity is a post-run check: <see cref="EvalRunner"/> runs
    /// before <c>run.finalized</c> is emitted, so the fixture composes the
    /// existing evaluator's result with the runner's checks.
    /// </summary>
    private static EvalReport WithTerminalIntegrity(
        EvalReport report,
        IReadOnlyList<TraceEvent> events) =>
        new([.. report.Checks, new TerminalTraceIntegrityEvaluator().Evaluate(events)]);

    private static async Task ApplyPostRunMutationAsync(
        Mutation mutation,
        RunArtifactPaths artifacts)
    {
        switch (mutation)
        {
            case Mutation.UnmutatedControl:
            case Mutation.BlankAuthoritativeOutput:
                break;
            case Mutation.MissingTerminalEvent:
                await RewriteTraceAsync(artifacts.TracePath, events => events[..^1]);
                break;
            case Mutation.MissingRunStarted:
                await RemoveTraceEventAsync(artifacts.TracePath, TraceEventNames.RunStarted);
                break;
            case Mutation.MissingRunCompleted:
                await RemoveTraceEventAsync(artifacts.TracePath, TraceEventNames.RunCompleted);
                break;
            case Mutation.MissingMainCompletion:
                await RemoveTraceEventAsync(
                    artifacts.TracePath,
                    TraceEventNames.ModelCompleted,
                    TracePayloadKeys.Phase,
                    "main");
                break;
            case Mutation.MissingCriticCompletion:
                await RemoveTraceEventAsync(artifacts.TracePath, TraceEventNames.CriticCompleted);
                break;
            case Mutation.MissingRevisionCompletion:
                await RemoveTraceEventAsync(artifacts.TracePath, TraceEventNames.RevisionCompleted);
                break;
            case Mutation.UnpairedModelCall:
                await RemoveTraceEventAsync(
                    artifacts.TracePath,
                    TraceEventNames.ModelCompleted,
                    TracePayloadKeys.Phase,
                    "critic");
                break;
            case Mutation.MissingNodeTerminal:
                await RemoveTraceEventAsync(
                    artifacts.TracePath,
                    TraceEventNames.NodeCompleted,
                    TracePayloadKeys.NodeId,
                    "critic");
                break;
            case Mutation.EmptyResultFile:
                Assert.NotEqual(
                    string.Empty,
                    (await File.ReadAllTextAsync(artifacts.ResultPath)).Trim());
                await File.WriteAllTextAsync(artifacts.ResultPath, string.Empty);
                break;
            case Mutation.EventAfterTerminal:
                await RewriteTraceAsync(
                    artifacts.TracePath,
                    events =>
                    [
                        .. events,
                        events[^1] with
                        {
                            Sequence = events[^1].Sequence + 1,
                            Type = TraceEventNames.EvalCompleted,
                            Data = new Dictionary<string, object?>()
                        }
                    ]);
                break;
            case Mutation.MissingRequiredArtifact:
                Assert.True(File.Exists(artifacts.InputPath));
                File.Delete(artifacts.InputPath);
                Assert.False(File.Exists(artifacts.InputPath));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }
    }

    /// <summary>
    /// Removes the single event of <paramref name="eventType"/> (optionally
    /// narrowed to one payload value) and renumbers the rest, because the
    /// reader rejects a trace whose sequence is not contiguous.
    /// </summary>
    private static Task RemoveTraceEventAsync(
        string tracePath,
        string eventType,
        string? dataKey = null,
        string? dataValue = null) =>
        RewriteTraceAsync(
            tracePath,
            events =>
            {
                var removed = Assert.Single(
                    events,
                    traceEvent => traceEvent.Type == eventType &&
                        (dataKey is null ||
                            (traceEvent.Data.TryGetValue(dataKey, out var value) &&
                                value?.ToString() == dataValue)));
                return events
                    .Where(traceEvent => !ReferenceEquals(traceEvent, removed))
                    .Select((traceEvent, index) => traceEvent with { Sequence = index + 1 })
                    .ToArray();
            });

    /// <summary>
    /// Rewrites trace.json as valid JSON with a changed event list. Both trace
    /// fixtures are lifecycle defects, not syntax errors, so the file must
    /// still load through <see cref="JsonTraceReader"/>.
    /// </summary>
    private static async Task RewriteTraceAsync(
        string tracePath,
        Func<TraceEvent[], TraceEvent[]> rewrite)
    {
        var document = await new JsonTraceReader().ReadAsync(tracePath);
        var events = document.Events.ToArray();
        Assert.Equal(TraceEventNames.RunFinalized, events[^1].Type);
        Assert.Single(events, traceEvent => TraceEventNames.IsTerminal(traceEvent.Type));
        Assert.Contains(events, traceEvent => traceEvent.Type == TraceEventNames.RunCompleted);

        await File.WriteAllTextAsync(
            tracePath,
            JsonSerializer.Serialize(
                document with { Events = rewrite(events) },
                new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void AssertExactFailures(
        Mutation mutation,
        EvalReport report,
        IReadOnlyDictionary<string, string> expected)
    {
        var diagnostics = $"{mutation}:{Environment.NewLine}{EvalRunner.RenderMarkdown(report)}";
        // The full, ordered check list: a check that silently stopped being
        // reported cannot masquerade as a pass.
        Assert.Equal(AllChecks, report.Checks.Select(check => check.Name));
        Assert.True(report.Passed == (expected.Count == 0), diagnostics);

        var failedNames = report.Checks
            .Where(check => !check.Passed)
            .Select(check => check.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(
            expected.Keys.Order(StringComparer.Ordinal).SequenceEqual(failedNames),
            diagnostics);

        foreach (var (checkName, fragment) in expected)
        {
            var details = report.Checks.Single(check => check.Name == checkName).Details;
            Assert.True(
                details.Contains(fragment, StringComparison.Ordinal),
                $"Expected '{checkName}' details to contain '{fragment}'. {diagnostics}");
        }
    }

    /// <summary>
    /// Applies a revision mutation: a heading defect in a Markdown document
    /// made of level-two sections, or the main draft returned verbatim. Other
    /// mutations return the content unchanged.
    /// </summary>
    private static string MutateRevision(Mutation mutation, string content, string mainDraft)
    {
        if (!IsRevisionMutation(mutation))
        {
            return content;
        }

        if (mutation == Mutation.UnchangedRevision)
        {
            // The draft already satisfies the contract, so the only defect is
            // a revision that ignored the critic.
            Assert.NotEqual(mainDraft, content);
            return mainDraft;
        }

        var sections = ParseSections(content);
        var target = IndexOfSection(sections, TargetHeading);

        switch (mutation)
        {
            case Mutation.MissingObjective:
                sections.RemoveAt(target);
                break;
            case Mutation.SwappedRequiredSections:
                var other = IndexOfSection(sections, SwapHeading);
                (sections[target], sections[other]) = (sections[other], sections[target]);
                break;
            case Mutation.DuplicateObjective:
                // A complete, nonempty second block at the end keeps the first
                // occurrence in place and non-empty: only the duplicate rule fires.
                sections.Add(sections[target]);
                break;
            case Mutation.EmptyObjective:
                sections[target] = sections[target] with { Body = string.Empty };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null);
        }

        return string.Join(
            "\n\n",
            sections.Select(section => section.Body.Length == 0
                ? section.Heading
                : $"{section.Heading}\n\n{section.Body}"));
    }

    private static List<Section> ParseSections(string content)
    {
        var sections = new List<Section>();
        foreach (var line in content.ReplaceLineEndings("\n").Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                sections.Add(new Section(line.Trim(), string.Empty));
            }
            else
            {
                Assert.True(
                    sections.Count > 0 || line.Trim().Length == 0,
                    "Fixture content must start with a level-two heading.");
                if (sections.Count > 0)
                {
                    sections[^1] = sections[^1] with { Body = sections[^1].Body + line + "\n" };
                }
            }
        }

        return sections
            .Select(section => section with { Body = section.Body.Trim() })
            .ToList();
    }

    private static int IndexOfSection(List<Section> sections, string heading)
    {
        var section = Assert.Single(sections, section => section.Heading == heading);
        Assert.NotEqual(string.Empty, section.Body);
        return sections.IndexOf(section);
    }

    private sealed record Section(string Heading, string Body);

    /// <summary>
    /// Returns the mock client's responses unchanged for main and critic, and
    /// the mutated mock revision for the revision phase.
    /// </summary>
    private sealed class RevisionMutatingModelClient(Mutation mutation) : IModelClient
    {
        private readonly MockModelClient _inner = new();
        private string _mainDraft = string.Empty;

        public string ProviderName => _inner.ProviderName;

        public async Task<ModelResponse> CompleteAsync(
            ModelRequest request,
            CancellationToken cancellationToken = default)
        {
            var response = await _inner.CompleteAsync(request, cancellationToken);
            if (request.PhaseKind == PhaseKind.Main)
            {
                _mainDraft = response.Content;
            }

            return request.PhaseKind == PhaseKind.Revision
                ? response with { Content = MutateRevision(mutation, response.Content, _mainDraft) }
                : response;
        }
    }

    /// <summary>
    /// Delegates unchanged to the real <see cref="EvalRunner"/> while keeping
    /// the context the orchestrator built, for post-run re-evaluation.
    /// </summary>
    private sealed class CapturingEvalRunner : IEvalRunner
    {
        private readonly EvalRunner _inner = new(new OutputContractValidator());

        public EvalContext? Context { get; private set; }

        public Task<EvalReport> EvaluateAsync(
            EvalContext context,
            CancellationToken cancellationToken = default)
        {
            Context = context;
            return _inner.EvaluateAsync(context, cancellationToken);
        }
    }
}
