using System.Collections.Immutable;
using CognitiveRuntime.Core.Contracts;

namespace CognitiveRuntime.Core.Runtime.Orchestration;

/// <summary>
/// The values disclosed to one execution node, paired with the typed
/// <see cref="ContextProjection"/> record of that disclosure. The phase results
/// are the channel handed to the model; the projection is the auditable record
/// the runtime traces.
/// </summary>
public sealed record ProjectedContext(
    ContextProjection Projection,
    IReadOnlyList<PhaseResult> PhaseResults);

/// <summary>
/// Builds the runtime-owned <see cref="ContextProjection"/> for one execution
/// node: the exact, default-deny set of values disclosed to that node's model
/// call. A node receives only the run input and the prior-result context the
/// plan declared for it (<see cref="PatternExecutionNode.ContextNodeIds"/>).
/// Nothing undeclared can enter the projection, so nothing undeclared can reach
/// the provider — the barrier is enforced by construction, not by trust.
///
/// This is the RB-009 v1: the identity transform over the <c>input</c> and
/// <c>prior-result</c> sources only. Redaction and truncation transforms, and
/// the evidence and blackboard sources, are deliberately out of scope here and
/// deferred to <c>TP-009</c> and <c>RB-007</c>. With the identity transform the
/// projected phase results are byte-identical to the pre-projection context, so
/// the step is non-breaking for every existing pattern.
/// </summary>
public sealed class ContextProjector
{
    /// <summary>
    /// The source id used for the run input when a node consumes the original
    /// run input rather than a prior node's output.
    /// </summary>
    public const string RunInputSourceId = "run-input";

    private const string TextMediaType = "text/markdown";

    private readonly TimeProvider _timeProvider;

    public ContextProjector(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ProjectedContext Project(
        string runId,
        PatternExecutionNode node,
        string input,
        IReadOnlyDictionary<string, PatternNodeExecutionResult> completedResults)
    {
        var items = ImmutableArray.CreateBuilder<ContextProjectionItem>(
            node.ContextNodeIds.Count + 1);
        var inputs = ImmutableArray.CreateBuilder<AgentInformationReference>(
            node.ContextNodeIds.Count + 1);

        // The run input is a user observation disclosed to the node. Its source
        // is the declared input node when one is set, otherwise the run input.
        var inputSourceId = node.InputNodeId ?? RunInputSourceId;
        var inputReference = new AgentInformationReference(
            AgentInformationKind.Observation,
            inputSourceId);
        items.Add(new ContextProjectionItem(
            inputSourceId,
            inputReference,
            TextMediaType,
            input));
        inputs.Add(inputReference);

        // Default-deny: only the declared prior-result context is disclosed, in
        // declared order. Each prior result is a claim produced by its node.
        var phaseResults = new List<PhaseResult>(node.ContextNodeIds.Count);
        foreach (var contextNodeId in node.ContextNodeIds)
        {
            if (!completedResults.TryGetValue(contextNodeId, out var source))
            {
                throw new InvalidOperationException(
                    $"Context source '{contextNodeId}' for node '{node.Id}' is " +
                    "failed or incomplete.");
            }

            var phaseResult = source.PhaseResult;
            phaseResults.Add(phaseResult);
            var reference = new AgentInformationReference(
                AgentInformationKind.Claim,
                contextNodeId);
            items.Add(new ContextProjectionItem(
                contextNodeId,
                reference,
                TextMediaType,
                phaseResult.Content));
            inputs.Add(reference);
        }

        var now = _timeProvider.GetUtcNow();
        var projection = new ContextProjection(
            new ContextProjectionId($"{runId}:{node.Id}:projection"),
            runId,
            node.Id,
            items.ToImmutable(),
            new InformationProvenance(
                runId,
                InformationSourceKind.Runtime,
                node.Id,
                node.Id,
                inputs.ToImmutable(),
                now),
            new InformationLifecycle(
                InformationLifecycleState.Active,
                now));

        return new ProjectedContext(projection, phaseResults);
    }
}
