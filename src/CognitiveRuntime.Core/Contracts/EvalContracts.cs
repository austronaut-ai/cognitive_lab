namespace CognitiveRuntime.Core.Contracts;

public sealed record EvalCheckResult(string Name, bool Passed, string Details);

public sealed record EvalReport(IReadOnlyList<EvalCheckResult> Checks)
{
    public bool Passed => Checks.All(check => check.Passed);
}

public sealed record EvalPlan(
    IReadOnlyList<PhaseKind> RequiredPhaseKinds,
    PhaseKind AuthoritativePhaseKind,
    bool EvaluateLoopEfficacy,
    EvalExecutionPlan? Execution = null);

public sealed record EvalExecutionNode(
    string NodeId,
    IReadOnlyList<string> DependencyNodeIds,
    IReadOnlyList<string> ContextNodeIds,
    string? StageId);

public sealed record EvalExecutionPlan(
    IReadOnlyList<EvalExecutionNode> Nodes,
    string AuthoritativeNodeId);

/// <summary>
/// One executed node paired with the loaded phase that produced it and its raw
/// output. Carries each node's own resolved contract into evaluation, so an
/// earlier pipeline stage or scatter branch is checked against its own mode.
/// </summary>
public sealed record EvalPhaseInput(string NodeId, LoadedPhase Phase, PhaseResult Result);

public sealed record EvalContext(
    RunArtifactPaths Artifacts,
    LoadedMode Mode,
    IReadOnlyList<TraceEvent> TraceEvents,
    IReadOnlyList<PhaseResult> PhaseResults,
    EvalPlan Plan,
    IReadOnlyDictionary<string, PhaseResult>? NodeResultsById = null,
    string? AuthoritativeContent = null,
    IReadOnlyList<RunArtifactState>? ArtifactLedger = null,
    IReadOnlyList<EvalPhaseInput>? ExecutedPhases = null);
