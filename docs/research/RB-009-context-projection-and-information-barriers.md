# RB-009: Context Projection And Information Barriers

Status: Research complete; v1 implemented on 2026-06-25 (remainder deferred)

Date: 2026-06-25

## Implementation Note

v1 is implemented. A runtime-owned `ContextProjector`
(`src/CognitiveRuntime.Core/Runtime/Orchestration/ContextProjector.cs`) computes
each node's default-deny `ContextProjection` at the one executor seam
(`PatternExecutor.ExecuteNodeAsync`) before the provider sees anything. The
projected phase results are the only prior-result context handed to the model;
the projection is the identity transform over the `input` and `prior-result`
(claim) sources, so every existing pattern produces byte-identical artifacts
(204 tests, including the artifact-content integration tests, pass unchanged). A
bounded `context.projected` trace event records the disclosure — projection ID,
node ID, input source, prior-result source IDs, item count — and never the raw
content. The projector and trace step live in the shared executor, so no
pattern-name branch is introduced (`OP-012`/`RT-007` preserved).

Deferred (not in v1, behind the named prerequisites): redaction and truncation
transforms; the evidence source (`TP-009`); the blackboard source (`RB-007`);
storing the projection in run state and writing a projection manifest artifact;
extending `EV-003` with a projection-source subset check; and projecting tool
requests. Folding the input into the same channel as prior results is also
deferred — v1 records the input as a projection item but still routes it through
the existing `input` argument.

## Decision Question

Falsifiable hypothesis: every execution node's input context can be constructed
by a runtime-owned **projection** step — built from a declared allowlist of
information references, default-deny — inserted between context resolution and
provider serialization, such that

1. a node receives only the values the plan declared for it, and the runtime can
   prove from the trace that no undeclared value reached the provider,
2. each projected value carries provenance (its source reference and any
   redaction or truncation transformation) that survives into traces and the
   optional projection manifest, and
3. the existing `single-pass`, `critic-revision`, `linear-pipeline`, and
   `scatter-gather` patterns produce byte-identical authoritative results to
   today when their projection is the identity projection (full prior results),
   i.e. the barrier is opt-in and non-breaking.

The hypothesis is refuted if projection cannot be enforced before provider
serialization without a pattern-name branch in the executor (violating
`OP-012`/`RT-007`), if default-deny cannot be expressed without embedding prompt
prose in runtime code, or if provenance cannot be carried without duplicating
raw projected content into the trace.

## Scope Boundary

This item owns **node-specific disclosure and information-flow policy**: which
declared values a single node may receive, and how absent/redacted/oversized
values are represented. It does **not** own retention, compaction, or eviction
across the run — that is `AR-004`. It does not own the blackboard itself — that
is `RB-007`. It does not own untrusted-content trust labels — that is `AR-001`,
which this item should compose with later but not absorb.

## What Already Exists

The vocabulary is already built; only the enforcement seam is missing.

- `ContextProjection` and `ContextProjectionItem` are typed contracts today
  (`src/CognitiveRuntime.Core/Contracts/AgentInformationContracts.cs`), with a
  `ContextProjectionId`, a `NodeId`, an array of items (each `Name`, `Source`
  `AgentInformationReference`, `MediaType`, `Content`), `InformationProvenance`,
  and `InformationLifecycle`. Commit authority is `Runtime`. Round-trip
  serialization is covered by `AgentInformationContractsTests`.
- The plan already declares per-node context as `ContextNodeIds`
  (`PatternExecutionNode`), validated by `OP-013` so a node cannot reference a
  future or undeclared node, and `EV-003` already asserts each node's recorded
  context source IDs equal its declared inputs.
- The executor assembles context at exactly one place
  (`PatternExecutor.ExecuteNodeAsync`): it maps `node.ContextNodeIds` to the
  full `PhaseResult` of each completed source and passes them to
  `PhaseRunner.RunAsync`. This is the single seam a projection step slots into.

So the gap is not new types or new plan structure; it is a runtime step that
turns "the full prior `PhaseResult`s for the declared node IDs" into a
`ContextProjection` the provider receives, and the provenance/trace plumbing
around it.

## Prerequisites And Assumptions

Already satisfied:

- `CF-001` / ADR-001 canonical information model (defines `ContextProjection`).
- `OP-011` typed pattern execution plan (declares per-node context edges).
- `OP-013` plan validation (context edges cannot reference future/undeclared
  nodes; assembly already rejects failed/incomplete sources).
- `RS-003` stable execution-node state.
- `TA-012` typed trace payload contracts (projection trace payload extends
  these, not raw strings).

Deferred, not blocking v1:

- `RB-007` runtime-owned blackboard. RB-009's prerequisite list names it, but it
  only gates the **blackboard-entry projection source**. The executor today
  projects *only prior phase results*, so v1 covers the `input` and
  `prior-result` (claim) sources and explicitly defers blackboard and evidence
  sources to when `RB-007` and the evidence phase (`TP-009`) land. This keeps
  RB-009 unblocked without pretending the blackboard exists.

Assumptions:

- Default visibility is deny: a node receives nothing it did not declare. The
  current behavior (full prior results for declared IDs) is re-expressed as an
  explicit identity projection, so it is opt-in to *narrow*, never opt-in to
  *widen*.
- Projection is computed by runtime code before any provider serialization; the
  provider client receives only the completed projection and cannot widen it
  (ADR-001 ownership boundary).

## Timebox

One spike of roughly two to three days:

1. Introduce a runtime `ContextProjector` that converts a node's declared
   context sources into a `ContextProjection` (identity transform first).
2. Route `PhaseRunner` input through the projection instead of raw
   `PhaseResult[]`, so the provider sees only projected items.
3. Emit a `context.projected` trace event (bounded: projection ID, node ID,
   source IDs, item names, content hashes/lengths — never raw content) and
   optionally write a projection manifest artifact.
4. Prove default-deny and non-undeclared-disclosure with the experiment below.

If carrying provenance through `PhaseRunner` without disturbing the existing
`PhaseResult` context contract proves larger than the spike, stop at step 2
(projection computed and enforced, identity transform only, provenance recorded
in run state but not yet in a manifest) and re-timebox redaction/truncation
transforms separately.

## Proposed Mechanism

Insert one runtime-owned step at the existing seam. Pseudocode against the real
code path (`PatternExecutor.ExecuteNodeAsync`, ~line 327):

```text
// today:
context = node.ContextNodeIds
    .Select(id => completedResults[id].PhaseResult)   // full content, no policy
    .ToArray();
phaseResult = await _phaseRunner.RunAsync(..., context, ...);

// proposed:
projection = _contextProjector.Project(
    runId, node,                       // declared sources = node.ContextNodeIds
    completedResults, initialInput);   // runtime resolves each source value
// projection.Items are the ONLY values the provider will see.
phaseResult = await _phaseRunner.RunAsync(..., projection, ...);
await trace.EmitAsync(TraceEventNames.ContextProjected, ProjectionTraceData(projection));
```

The projector is pattern-agnostic and lives in the shared executor, so no
pattern-name branch is introduced (`OP-012`/`RT-007` preserved). A pattern can
later attach a per-edge **projection policy** (identity, redact-by-name,
truncate-to-N) as *data* on the plan node, exactly as `scatter-gather` attached
`ArtifactGroup` as data — the executor honors it without knowing the pattern.

## Answers To The Open Questions

**How are projections declared without embedding prompt prose in runtime code?**
The plan declares *which* sources a node receives (already: `ContextNodeIds`)
plus an optional typed per-edge transform (identity / redact named fields /
truncate to a declared length). Prose stays in mode prompt files; the projection
declares structure and disclosure, never wording.

**Which metadata accompanies projected values so provenance survives
summarization or transformation?** Each `ContextProjectionItem` carries its
`Source` `AgentInformationReference` and `MediaType`; the enclosing
`ContextProjection` carries `InformationProvenance` (run ID, source kind, input
references, timestamp) and `InformationLifecycle`. A redaction or truncation is
recorded as an explicit transformation in provenance, so a reader can tell a
projected value was altered and from which source.

**How are absent, redacted, oversized, or unauthorized values represented?**
Explicitly, never silently:

- absent declared source → projection construction fails the node (reuses the
  existing "failed or incomplete dependency" rule), it does not pass an empty
  string,
- redacted → an item whose content is a typed placeholder with provenance noting
  redaction,
- oversized → truncated only when the edge declares a truncation transform, with
  original and projected lengths recorded; otherwise the run-output budget
  (`RS-006`) governs,
- unauthorized (a value not declared for this node) → never appears; default
  visibility is deny, so there is nothing to represent.

**Can the runtime prove that a node did not receive undeclared context?** Yes,
two-sided: (a) the projection is the *only* object handed to `PhaseRunner`, so by
construction the provider cannot see a non-projected value; (b) the
`context.projected` trace event records the projection's source IDs and item
names, and `EV-003` already asserts recorded context source IDs equal declared
context inputs — extend that eval to also assert the projection's item sources
are a subset of the declared edges. Proof is from the trace, not from trust.

**How should projections apply to model requests, tool requests, traces, and
artifacts?** v1: model requests only (the one seam that exists). Tool requests
inherit the same projector once the read-only evidence phase (`TP-009`) gives
tools a context, and are explicitly out of v1 scope. Traces get a bounded
`context.projected` event; artifacts get an optional `projection manifest`
(source IDs, item names, hashes, transforms) — never raw projected content
unless run policy permits, per ADR-001 persistence table.

## Smallest Useful Experiment

Add a `ContextProjector` and route one existing pattern's context through it,
then assert:

1. **Identity equivalence.** `critic-revision` and `scatter-gather` over the
   mock client, with identity projection, produce byte-identical `run.json` and
   `result.md` to the pre-change baseline. The barrier is non-breaking.
2. **Default-deny.** A node declaring context `[main]` receives a projection
   whose item sources are exactly `{main}`; an adversarial executor variant that
   tries to inject `critic` into the provider request without a declared edge is
   rejected (the projection has no such item, and the eval flags a mismatch).
3. **Provenance survives.** A redact-by-name transform on one edge yields a
   projected item whose provenance records the redaction and whose content is the
   placeholder, while the source `PhaseResult` in run state is unchanged.
4. **Trace proof.** The `context.projected` event for each node lists source IDs
   equal to (or a declared subset of) the node's `ContextNodeIds`, and never
   contains raw projected content (only hashes/lengths).
5. **Non-disclosure under concurrency.** Run the experiment under
   `scatter-gather` to confirm projection is per-node and concurrency does not
   leak one branch's content into another's projection.

## Baseline And Negative Control

Baseline: the current executor (full prior `PhaseResult`s, no projection). With
identity projection, every typed outcome, trace shape (modulo the additive
`context.projected` events), and artifact hash must match — proving the step is
transparent when not narrowing.

Negative controls:

- A plan that declares a context edge to an undeclared node must already be
  rejected by `OP-013`; confirm projection adds no path around that.
- A deliberately buggy projector that copies a non-declared source into the
  projection must be **caught** by the extended `EV-003` subset check — proving
  the trace-level proof is real and not decorative.
- A projector that omits a declared source must fail the node, not silently
  produce a thinner context — proving absence is loud.

## Required Fixtures, Traces, And Artifacts

- Mock outputs (existing, credential-free) for the patterns under test.
- An identity-equivalence fixture: pre/post `run.json` with fixed run ID and
  `TimeProvider`.
- A redaction fixture (one edge with a redact-by-name transform).
- An adversarial fixture (projector or executor attempting undeclared
  disclosure) proving the eval fails.
- Trace: a new `context.projected` event name added to `TraceEventNames`, with a
  typed payload (`TA-012`) of projection ID, node ID, source IDs, item names,
  content hashes/lengths. No raw content.
- Artifact: an optional `projections/NN-<node>.json` manifest mirroring the
  `phases/` convention; raw values only when policy permits.

## Reproduction Commands

```powershell
dotnet run --project src/CognitiveRuntime.Cli -- `
  --pattern critic-revision `
  --mode frame `
  --input examples/agent_runtime_goal.txt `
  --run-mode mock `
  --html

dotnet test CognitiveRuntime.slnx --no-restore
```

## Evidence And Limitations

- The dominant unknown is threading a `ContextProjection` (instead of
  `PhaseResult[]`) through `PhaseRunner` without disturbing the existing typed
  context contract every pattern depends on. If that proves invasive, ship the
  projection computed-and-enforced at the executor seam with provenance in run
  state, and defer the richer `PhaseRunner` signature change.
- v1 covers only `input` and `prior-result` projection sources. Evidence and
  blackboard sources are deferred to `TP-009` and `RB-007`; this note should not
  be promoted past v1 until those land.
- Projection bounds *disclosure*, not *trust*. Untrusted-content labeling
  (`AR-001`) is complementary and out of scope here; a projected item is still
  "data, not authority," but enforcing that is AR-001's job.
- This is run-scoped node disclosure, not retention or compaction (`AR-004`) and
  not long-term memory.

## Rejected Alternatives

- **Projection inside each pattern.** Would reintroduce per-pattern context
  logic and a pattern-name branch; rejected for `OP-012`/`RT-007`. Projection is
  one shared runtime step.
- **Model-requested context widening** ("the model asks for the critic's
  output"): violates default-deny and the runtime-owns-disclosure boundary;
  rejected outright.
- **Provider-side filtering** (let the provider client trim context): rejected;
  ADR-001 requires the runtime to compute the completed projection before
  serialization, and the provider receives only the result.
- **Raw projected content in traces** for "full inspectability": rejected;
  duplicates sensitive/large content into the trace against ADR-001. Hashes and
  lengths in the trace, raw values only in a policy-gated manifest.
- **Blocking on `RB-007` before any RB-009 work**: rejected as scoping; the
  blackboard gates only one projection source, and v1 has real value (default-
  deny over prior results) without it.

## Disposition

**Promoted; v1 implemented** on 2026-06-25. Today's full-context behavior is now
an explicit runtime-owned identity projection with default-deny, provenance, and
a bounded `context.projected` trace event, gated to the `input` and
`prior-result` sources. Identity-equivalence is evidenced by the unchanged
artifact-content integration tests; non-disclosure is evidenced by
`ContextProjectionTests` (per-node declared sources in the trace, and a proof the
event carries no raw content). Redaction/truncation transforms and the
evidence/blackboard sources remain deferred behind `TP-009` and `RB-007`.

## Research Sources

- [ADR-001 canonical agent information model](../architecture/ADR-001-canonical-agent-information-model.md)
- [RB-008 bounded scatter-gather orchestration](RB-008-bounded-scatter-gather.md)
  (names RB-009 as the intended mitigation for unbounded gather context)
- [TP-009 read-only evidence phase](TP-009-read-only-evidence-phase.md)
  (future source of evidence-kind projection items)
