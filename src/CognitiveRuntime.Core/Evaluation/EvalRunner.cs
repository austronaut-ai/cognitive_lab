using System.Text;
using CognitiveRuntime.Core.Abstractions;
using CognitiveRuntime.Core.Contracts;

namespace CognitiveRuntime.Core.Evaluation;

public sealed class EvalRunner : IEvalRunner
{
    private readonly OutputContractValidator _outputContractValidator;
    private readonly LoopEfficacyEvaluator _loopEfficacyEvaluator;
    private readonly DeclaredPlanExecutionEvaluator _planExecutionEvaluator;
    private readonly ModelCallPairingEvaluator _modelCallPairingEvaluator;

    public EvalRunner(
        OutputContractValidator outputContractValidator,
        LoopEfficacyEvaluator? loopEfficacyEvaluator = null,
        DeclaredPlanExecutionEvaluator? planExecutionEvaluator = null,
        ModelCallPairingEvaluator? modelCallPairingEvaluator = null)
    {
        _outputContractValidator = outputContractValidator;
        _loopEfficacyEvaluator = loopEfficacyEvaluator ?? new LoopEfficacyEvaluator();
        _planExecutionEvaluator =
            planExecutionEvaluator ?? new DeclaredPlanExecutionEvaluator();
        _modelCallPairingEvaluator =
            modelCallPairingEvaluator ?? new ModelCallPairingEvaluator();
    }

    public async Task<EvalReport> EvaluateAsync(
        EvalContext context,
        CancellationToken cancellationToken = default)
    {
        var result = File.Exists(context.Artifacts.ResultPath)
            ? await File.ReadAllTextAsync(
                context.Artifacts.ResultPath,
                cancellationToken)
            : string.Empty;
        var main = context.PhaseResults.FirstOrDefault(
            phaseResult => phaseResult.PhaseKind == PhaseKind.Main);
        var critic = context.PhaseResults.FirstOrDefault(
            phaseResult => phaseResult.PhaseKind == PhaseKind.Critic);
        var revision = context.PhaseResults.FirstOrDefault(
            phaseResult => phaseResult.PhaseKind == PhaseKind.Revision);
        var executedPhases = context.ExecutedPhases ?? MatchLoadedPhases(context);
        var (authoritativeResult, authoritativePhase) =
            ResolveAuthoritative(context, executedPhases);
        var mainContent = main?.Content ?? string.Empty;
        var criticContent = critic?.Content ?? string.Empty;
        var revisionContent = revision?.Content ?? string.Empty;
        var authoritativeContent = authoritativeResult?.Content ?? string.Empty;

        var events = context.TraceEvents;
        var checks = new List<EvalCheckResult>
        {
            CheckRequiredArtifacts(context),
            CheckTraceEvent(events, TraceEventNames.RunStarted),
            CheckTraceEvent(events, TraceEventNames.RunCompleted),
            new(
                "result is not empty",
                !string.IsNullOrWhiteSpace(result),
                string.IsNullOrWhiteSpace(result)
                    ? "result.md is empty."
                    : "result.md contains content.")
        };

        checks.AddRange(
            context.Plan.RequiredPhaseKinds.Select(
                phaseKind => CheckPhaseCompleted(
                    context.PhaseResults,
                    events,
                    phaseKind)));
        checks.Add(
            CheckAuthoritativeOutput(
                context.Plan.AuthoritativePhaseKind,
                authoritativeContent));
        checks.Add(
            CheckFinalContract(
                authoritativeContent,
                authoritativePhase,
                context.Plan.AuthoritativePhaseKind));

        if (context.Plan.Execution is not null)
        {
            checks.Add(
                _planExecutionEvaluator.Evaluate(
                    context.Plan.Execution,
                    events,
                    context.NodeResultsById,
                    context.AuthoritativeContent));
            checks.Add(_modelCallPairingEvaluator.Evaluate(events));
        }

        if (context.Plan.EvaluateLoopEfficacy)
        {
            checks.Add(
                CheckLoopEfficacy(
                    mainContent,
                    criticContent,
                    revisionContent,
                    FindPhase(context.Mode, PhaseKind.Revision)?.OutputContract));
        }

        checks.AddRange(CheckDeclaredPhaseContracts(executedPhases));

        return new EvalReport(checks);
    }

    private const string OutputContractCheckName = "output contract satisfied";

    /// <summary>
    /// Validates the raw authoritative output against the contract the loader
    /// resolved for the authoritative phase (the revision in a critic loop).
    /// </summary>
    private EvalCheckResult CheckFinalContract(
        string authoritativeContent,
        LoadedPhase? authoritativePhase,
        PhaseKind authoritativeKind)
    {
        if (authoritativePhase?.OutputContract is not { } contract)
        {
            return new EvalCheckResult(
                OutputContractCheckName,
                false,
                $"No output contract was resolved for the authoritative " +
                $"{FormatKind(authoritativeKind)} phase.");
        }

        return _outputContractValidator.Validate(authoritativeContent, contract);
    }

    private EvalCheckResult CheckLoopEfficacy(
        string draft,
        string criticContent,
        string revision,
        OutputContract? revisionContract) =>
        revisionContract is null
            ? new EvalCheckResult(
                "loop responded to critic findings",
                false,
                "No output contract was resolved for the revision phase.")
            : _loopEfficacyEvaluator.Evaluate(draft, criticContent, revision, revisionContract);

    /// <summary>
    /// Adds one check per executed phase that declared its own contract. Inherited
    /// contracts get no standalone check, so legacy modes report exactly as before.
    /// These checks add to, and never replace, the final-result check.
    /// </summary>
    private IEnumerable<EvalCheckResult> CheckDeclaredPhaseContracts(
        IReadOnlyList<EvalPhaseInput> executedPhases)
    {
        var declared = executedPhases
            .Where(input => input.Phase.DeclaresOutputContract && input.Phase.OutputContract is not null)
            .ToArray();
        var repeatedNames = declared
            .GroupBy(input => input.Phase.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var input in declared)
        {
            var name = $"{OutputContractCheckName} for phase {input.Phase.Name}";
            if (repeatedNames.Contains(input.Phase.Name))
            {
                name += $" (node {input.NodeId})";
            }

            yield return _outputContractValidator.Validate(
                input.Result.Content,
                input.Phase.OutputContract!) with { Name = name };
        }
    }

    /// <summary>
    /// Resolves the authoritative node once, so the final checks read its raw
    /// output and its resolved contract from the same node. When the plan names
    /// an authoritative node, that node wins: first from the executed phase
    /// inputs, then from the node-result map paired with its loaded phase. Only
    /// callers without node data fall back to the first result and loaded phase
    /// of the authoritative kind.
    /// </summary>
    private static (PhaseResult? Result, LoadedPhase? Phase) ResolveAuthoritative(
        EvalContext context,
        IReadOnlyList<EvalPhaseInput> executedPhases)
    {
        var kind = context.Plan.AuthoritativePhaseKind;
        var nodeId = context.Plan.Execution?.AuthoritativeNodeId;
        if (nodeId is not null)
        {
            var authoritativeNode = executedPhases.FirstOrDefault(
                input => string.Equals(input.NodeId, nodeId, StringComparison.Ordinal));
            if (authoritativeNode is not null)
            {
                return (authoritativeNode.Result, authoritativeNode.Phase);
            }

            if (context.NodeResultsById?.TryGetValue(nodeId, out var mapped) == true)
            {
                return (mapped, MatchLoadedPhase(context.Mode, mapped) ?? FindPhase(context.Mode, kind));
            }
        }

        return (
            context.PhaseResults.FirstOrDefault(result => result.PhaseKind == kind),
            FindPhase(context.Mode, kind));
    }

    private static LoadedPhase? FindPhase(LoadedMode mode, PhaseKind kind) =>
        mode.Phases.FirstOrDefault(phase => phase.Kind == kind);

    private static LoadedPhase? MatchLoadedPhase(LoadedMode mode, PhaseResult result) =>
        mode.Phases.FirstOrDefault(
            phase => phase.Kind == result.PhaseKind &&
                string.Equals(phase.Name, result.PhaseName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// For direct callers that supply no execution inputs, pairs each result
    /// with the loaded phase of the same kind and name. Node IDs come from the
    /// node-result map when one is supplied, and from phase names otherwise.
    /// </summary>
    private static IReadOnlyList<EvalPhaseInput> MatchLoadedPhases(EvalContext context)
    {
        var results = context.NodeResultsById is { Count: > 0 } nodeResults
            ? nodeResults.Select(pair => (NodeId: pair.Key, Result: pair.Value))
            : context.PhaseResults.Select(result => (NodeId: result.PhaseName, Result: result));

        return results
            .Select(match => (match.NodeId, match.Result, Phase: MatchLoadedPhase(context.Mode, match.Result)))
            .Where(match => match.Phase is not null)
            .Select(match => new EvalPhaseInput(match.NodeId, match.Phase!, match.Result))
            .ToArray();
    }

    private static string FormatKind(PhaseKind kind) => kind.ToString().ToLowerInvariant();

    public static string RenderMarkdown(EvalReport report)
    {
        var builder = new StringBuilder()
            .AppendLine("# Evaluation Report")
            .AppendLine()
            .AppendLine($"Overall: **{(report.Passed ? "PASS" : "FAIL")}**")
            .AppendLine();

        foreach (var check in report.Checks)
        {
            builder
                .Append("- [")
                .Append(check.Passed ? 'x' : ' ')
                .Append("] ")
                .Append(check.Name)
                .Append(": ")
                .AppendLine(check.Details);
        }

        return builder.ToString();
    }

    private static EvalCheckResult CheckRequiredArtifacts(EvalContext context)
    {
        var ledger = context.ArtifactLedger;
        if (ledger is null || ledger.Count == 0)
        {
            // No artifact ledger supplied (for example a direct eval): fall back
            // to verifying the required artifact files exist on disk.
            var missingFiles = context.Artifacts.RequiredPaths
                .Where(path => !File.Exists(path))
                .Select(Path.GetFileName)
                .ToArray();

            return new EvalCheckResult(
                "required artifacts exist",
                missingFiles.Length == 0,
                missingFiles.Length == 0
                    ? "All required artifact paths exist."
                    : $"Missing artifacts: {string.Join(", ", missingFiles)}.");
        }

        // The runtime tracks artifact planning and completion explicitly, so the
        // check no longer depends on a placeholder eval report existing on disk.
        // It fails when an artifact's write was attempted and failed, or when an
        // artifact reported as written is missing on disk. Planned-but-pending
        // artifacts (the eval report and run manifest, produced after this check)
        // are expected and do not fail it.
        var failed = ledger
            .Where(artifact => artifact.Status == RunArtifactStatus.Failed)
            .Select(artifact => artifact.Name)
            .ToArray();
        var writtenButMissing = ledger
            .Where(artifact =>
                artifact.Status == RunArtifactStatus.Written &&
                !File.Exists(
                    Path.Combine(context.Artifacts.RunDirectory, artifact.Name)))
            .Select(artifact => artifact.Name)
            .ToArray();
        var problems = failed.Concat(writtenButMissing).ToArray();

        return new EvalCheckResult(
            "required artifacts exist",
            problems.Length == 0,
            problems.Length == 0
                ? "All written required artifacts exist; none failed."
                : $"Failed or missing artifacts: {string.Join(", ", problems)}.");
    }

    private static EvalCheckResult CheckTraceEvent(
        IReadOnlyList<TraceEvent> events,
        string eventType,
        string? checkName = null)
    {
        var found = events.Any(
            traceEvent => string.Equals(
                traceEvent.Type,
                eventType,
                StringComparison.Ordinal));

        return new EvalCheckResult(
            checkName ?? $"trace contains {eventType}",
            found,
            found
                ? $"Trace contains '{eventType}'."
                : $"Trace does not contain '{eventType}'.");
    }

    private static EvalCheckResult CheckPhaseCompleted(
        IReadOnlyList<PhaseResult> phaseResults,
        IReadOnlyList<TraceEvent> events,
        PhaseKind phaseKind)
    {
        var matches = phaseResults
            .Where(result => result.PhaseKind == phaseKind)
            .ToArray();
        var phaseName = matches.FirstOrDefault()?.PhaseName;
        var completionEventType = phaseKind switch
        {
            PhaseKind.Main => TraceEventNames.ModelCompleted,
            PhaseKind.Critic => TraceEventNames.CriticCompleted,
            PhaseKind.Revision => TraceEventNames.RevisionCompleted,
            _ => throw new ArgumentOutOfRangeException(
                nameof(phaseKind),
                phaseKind,
                "Unsupported phase kind.")
        };
        var completionEventFound = phaseName is not null &&
            events.Any(traceEvent =>
                string.Equals(
                    traceEvent.Type,
                    completionEventType,
                    StringComparison.Ordinal) &&
                string.Equals(
                    GetDataString(traceEvent, TracePayloadKeys.Phase),
                    phaseName,
                    StringComparison.OrdinalIgnoreCase));
        var passed = matches.Length == 1 && completionEventFound;
        var kindName = phaseKind.ToString().ToLowerInvariant();
        var details = matches.Length switch
        {
            0 => $"No {kindName} phase result was recorded.",
            > 1 => $"Expected one {kindName} phase result, but found {matches.Length}.",
            _ when !completionEventFound =>
                $"Trace does not contain '{completionEventType}' for phase '{phaseName}'.",
            _ => $"The {kindName} phase completed exactly once."
        };

        return new EvalCheckResult(
            $"{kindName} phase ran",
            passed,
            details);
    }

    private static EvalCheckResult CheckAuthoritativeOutput(
        PhaseKind authoritativePhaseKind,
        string content)
    {
        var kindName = authoritativePhaseKind.ToString().ToLowerInvariant();
        var passed = !string.IsNullOrWhiteSpace(content);

        return new EvalCheckResult(
            $"{kindName} is not empty",
            passed,
            passed
                ? $"The authoritative {kindName} contains content."
                : $"The authoritative {kindName} is empty.");
    }

    private static string? GetDataString(TraceEvent traceEvent, string key) =>
        traceEvent.Data.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
}
