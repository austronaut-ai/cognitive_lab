using System.Text.Json;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Runtime.Orchestration;
using CognitiveRuntime.Core.Tracing;
using static CognitiveRuntime.Tests.RuntimeTestHarness;

namespace CognitiveRuntime.Tests;

public sealed class ContextProjectionTests
{
    private static FixedTimeProvider FixedTime() =>
        new(new DateTimeOffset(2026, 6, 25, 12, 0, 0, TimeSpan.Zero));

    private static PatternExecutionNode Node(
        string id,
        PhaseKind kind,
        params string[] context) =>
        new(
            id,
            PatternNodeKind.Phase,
            "primary",
            kind,
            DependencyNodeIds: context,
            ContextNodeIds: context);

    // Mode/Phase are not read by the projector, so a minimal result carrying the
    // phase output is sufficient for these unit tests.
    private static PatternNodeExecutionResult Completed(
        PatternExecutionNode node,
        string content) =>
        new(
            node,
            Mode: null!,
            Phase: null!,
            Input: "ignored",
            ContextNodeIds: node.ContextNodeIds,
            PhaseResult: new PhaseResult(
                node.Id,
                node.PhaseKind,
                content,
                "mock",
                "deterministic-template-v2"));

    [Fact]
    public void Project_DefaultDeny_NodeWithNoContextSeesOnlyTheInput()
    {
        var projector = new ContextProjector(FixedTime());

        var projected = projector.Project(
            "run-1",
            Node("main", PhaseKind.Main),
            "the run input",
            new Dictionary<string, PatternNodeExecutionResult>());

        // No prior-result context is disclosed: the model receives nothing but
        // the input it was given separately.
        Assert.Empty(projected.PhaseResults);

        // The projection still records the one disclosed value: the input,
        // sourced as a user observation.
        var item = Assert.Single(projected.Projection.Items);
        Assert.Equal(AgentInformationKind.Observation, item.Source.Kind);
        Assert.Equal(ContextProjector.RunInputSourceId, item.Source.Id);
        Assert.Equal("the run input", item.Content);
        Assert.Equal("run-1", projected.Projection.RunId);
        Assert.Equal("main", projected.Projection.NodeId);
    }

    [Fact]
    public void Project_Identity_DisclosesDeclaredPriorResultsUnchanged()
    {
        var projector = new ContextProjector(FixedTime());
        var main = Node("main", PhaseKind.Main);
        var completed = new Dictionary<string, PatternNodeExecutionResult>
        {
            ["main"] = Completed(main, "MAIN OUTPUT")
        };

        var projected = projector.Project(
            "run-1",
            Node("critic", PhaseKind.Critic, "main"),
            "the run input",
            completed);

        // The identity transform hands the declared prior result through
        // byte-for-byte: the channel the provider sees is unchanged.
        var phaseResult = Assert.Single(projected.PhaseResults);
        Assert.Equal("MAIN OUTPUT", phaseResult.Content);
        Assert.Equal(PhaseKind.Main, phaseResult.PhaseKind);

        // The projection records input first, then the declared claim context.
        Assert.Collection(
            projected.Projection.Items,
            input => Assert.Equal(
                AgentInformationKind.Observation, input.Source.Kind),
            claim =>
            {
                Assert.Equal(AgentInformationKind.Claim, claim.Source.Kind);
                Assert.Equal("main", claim.Source.Id);
                Assert.Equal("MAIN OUTPUT", claim.Content);
            });
    }

    [Fact]
    public void Project_RejectsAnUndeclaredOrIncompleteContextSource()
    {
        var projector = new ContextProjector(FixedTime());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            projector.Project(
                "run-1",
                Node("critic", PhaseKind.Critic, "main"),
                "the run input",
                new Dictionary<string, PatternNodeExecutionResult>()));

        Assert.Contains("main", exception.Message);
    }

    [Fact]
    public async Task Run_TracesEachNodesDisclosureAndLeaksNoRawContent()
    {
        using var workspace = new TestWorkspace();
        workspace.CreateMode();
        var orchestrator = CreateOrchestrator(workspace, FixedTime());

        var result = await orchestrator.RunAsync(
            new RunRequest(
                "frame",
                "Exercise context projection over the bounded loop.",
                "mock",
                workspace.OutputRoot));

        Assert.Equal(RunOutcome.Success, result.Outcome);

        var trace = await new JsonTraceReader().ReadAsync(
            Path.Combine(result.OutputDirectory, "trace.json"));
        var projections = trace.Events
            .Where(traceEvent => traceEvent.Type == TraceEventNames.ContextProjected)
            .ToArray();

        // One projection per declared node, and each discloses exactly the
        // prior-result context the critic-revision plan declared for it.
        var sourcesByNode = projections.ToDictionary(
            traceEvent => GetString(traceEvent, TracePayloadKeys.NodeId),
            traceEvent => GetStrings(traceEvent, TracePayloadKeys.SourceIds));
        Assert.Equal(
            new Dictionary<string, string[]>
            {
                ["main"] = [],
                ["critic"] = ["main"],
                ["revision"] = ["main", "critic"]
            },
            sourcesByNode);

        // The trace records the disclosure, never the disclosed content. The
        // mock main output contains "## Problem"; no projection event may.
        foreach (var projection in projections)
        {
            var serialized = JsonSerializer.Serialize(
                projection.Data.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value));
            Assert.DoesNotContain("## Problem", serialized, StringComparison.Ordinal);
        }
    }

    private static string GetString(TraceEvent traceEvent, string key) =>
        traceEvent.Data[key] is JsonElement element
            ? element.GetString() ?? string.Empty
            : traceEvent.Data[key]?.ToString() ?? string.Empty;

    private static string[] GetStrings(TraceEvent traceEvent, string key) =>
        traceEvent.Data[key] is JsonElement { ValueKind: JsonValueKind.Array } element
            ? element.EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToArray()
            : [];
}
