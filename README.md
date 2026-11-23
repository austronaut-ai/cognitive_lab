# Cognitive Agent Runtime Lab

[![CI](https://github.com/austronaut-ai/cognitive_lab/actions/workflows/ci.yml/badge.svg)](https://github.com/austronaut-ai/cognitive_lab/actions/workflows/ci.yml)

A local-first C#/.NET environment for running, comparing, and inspecting
bounded agent orchestration patterns. The runtime owns the loop; the model
only performs reasoning inside the steps it is assigned. It is intentionally
not a chatbot, web app, or autonomous agent framework.

> The LLM is not the product. The loop is the product.

## Requirements

- .NET 10 SDK
- No credentials for mock mode

## Build And Test

```powershell
dotnet restore --configfile NuGet.Config
dotnet test CognitiveRuntime.slnx --no-restore
```

## Continuous Integration

GitHub Actions (`.github/workflows/ci.yml`) runs the same two commands above on
every push to `main` and on every pull request. The build and tests are
credential-free: no secrets are configured or required, and the job fails if any
generated `outputs/` content is ever committed.

## Run Mock Mode

Command Prompt:

```bat
dotnet run --project src/CognitiveRuntime.Cli -- --mode frame --input examples/agent_runtime_goal.txt --run-mode mock --html
```

PowerShell:

```powershell
dotnet run --project src/CognitiveRuntime.Cli -- `
  --mode frame `
  --input examples/agent_runtime_goal.txt `
  --run-mode mock `
  --html
```

Each run creates a timestamped directory under `outputs/` containing:

- `input.md`
- `result.md`
- `trace.json`
- `run_summary.md`
- `eval_report.md`
- `pattern.md`
- `run.json`
- `phases/NN-<phase>.md` - each phase output persisted individually
- `index.html` when `--html` is present

`result.md` remains the authoritative human-facing composition; the `phases/`
files expose each phase's raw output for inspection and are listed in
`run.json` and traced as artifact writes. For `linear-pipeline`, each stage
writes its own `stages/NN-<mode>/phases/NN-<phase>.md`.

## Validate A Mode

Check a mode without a run:

```powershell
dotnet run --project src/CognitiveRuntime.Cli -- modes validate frame
dotnet run --project src/CognitiveRuntime.Cli -- modes validate frame --modes-root path/to/modes
dotnet run --project src/CognitiveRuntime.Cli -- modes validate lens --lens warcraft
```

Validation loads `mode.json`, `MODE.md`, and the prompts through the
same Core loader a run uses. `--lens` selects the same prompt variant
(`prompts/<lens>/`) a run does. A mode whose prompts exist only under lens
variants, such as `lens`, needs it: without `--lens` validation prints the
available lenses to stderr and exits 1. It makes no model call, needs no credentials or
input file, and writes no run output. `--modes-root` defaults to `./modes`.

Manifest rules enforced by validation (and by every run):

- The top-level `schemaVersion` is required and must be the integer `1`, the
  only `mode.json` format this runtime supports. It versions the manifest
  format; `version` is separate and tracks the mode's own content. A missing
  `schemaVersion` or any other value (`2`, `"1"`, `1.0`, `null`) fails loading
  with an error naming the field, the value found, and the supported version.
  Declare it once: a second `schemaVersion` (names compare ignoring case) is
  an error too.
- Unknown properties anywhere in `mode.json` are rejected, so a misspelled
  field fails loading instead of being ignored. Comments and trailing commas
  are accepted.
- A mode has exactly three phases, in this order: `main`, `critic`,
  `revision`. Each phase must declare its `kind` explicitly; omitting or
  repeating a kind is an error.
- Phase names start with an ASCII letter or digit and then use only
  letters, digits, and hyphens (`A-Z`, `a-z`, `0-9`, `-`), the same rule as
  mode and lens names (a leading letter or digit keeps a lens name from
  reading as a command-line option). Names must be unique ignoring case.
- Prompt paths are relative to the mode directory. `/` and `\` both work as
  separators and `.`/`..` segments are resolved, but rooted paths (leading
  slash or backslash, drive letters, UNC) are rejected on every platform. A
  path must stay inside the mode directory; the containment check is
  case-sensitive, so `../Frame/...` from mode `frame` is rejected even on a
  case-insensitive filesystem. Symbolic links and junctions inside the mode
  directory are rejected.
- Each phase must use a distinct prompt file; two references that resolve to
  the same file (ignoring case) are rejected.
- The three required prompts (`prompts/main.md`, `prompts/critic.md`,
  `prompts/revision.md`) must each be referenced by a phase, using that exact
  spelling. With `--lens <name>` the required paths are the lens equivalents
  (`prompts/<name>/main.md`, and so on).
- The mode-level `outputContract` is required: `requiredHeadings` must list at
  least one non-blank heading and `minimumLength` (default `1`) must be at
  least `1`.
- A phase may also carry its own optional `outputContract` with the same shape
  and rules. An explicit `null` is rejected; omit the field to take the runtime
  default. Unknown properties beside or inside it are rejected like any other.

Documentation rules enforced by the same loader (so `modes validate`,
`modes list`, and every run agree). They check only that `MODE.md` mentions
what `mode.json` configures; they do not judge prose, layout, or order:

- `MODE.md` must exist and must not be empty or whitespace-only.
- Every configured phase name must appear as a whole word, compared ordinally
  and ignoring case. Letters, digits, `_`, and `-` continue a word, so phase
  `draft` is not satisfied by `pre-draft` or `draft_extra`; punctuation, spaces,
  and the start or end of the file end one.
- Every required heading of the revision phase's resolved output contract (its
  own `outputContract`, or the mode-level one when it declares none) must
  appear verbatim, case-sensitive and including its leading `#` characters,
  for example `## Problem`. It may sit anywhere: ordinary text, inline code, or
  a fenced block. Main and critic contracts add no documentation requirements.
- When checks fail, one diagnostic names `MODE.md` and lists every missing
  phase name and heading.
- Lens variants share the mode's `MODE.md` and contract, so the same
  documentation is checked whichever `--lens` is selected.

The shipped modes satisfy these rules with a short `## Output` section that
lists the contract headings in a fenced `text` block.

Per-phase contract defaults are resolved once when the mode loads
(`PhaseContractDefaults` in Core), so every consumer sees the same contract
per phase:

| Phase | Declares `outputContract` | Does not declare it |
| --- | --- | --- |
| `main` | its own contract | the mode-level `outputContract` |
| `critic` | its own contract | no structural contract; not checked |
| `revision` | its own contract | the mode-level `outputContract` |

The `output contract satisfied` eval check validates the raw authoritative
output against that phase's resolved contract: the revision in a critic loop,
the main phase in a single pass. Loop efficacy uses the revision's resolved
contract. Each executed phase that declares its own contract also gets a
separate check, `output contract satisfied for phase <name>` (with
`(node <id>)` appended when several executed nodes share a phase name), against
its own output. These checks add to the final check and never replace it;
inherited contracts produce no extra check, so the shipped modes report exactly
as before.

| Exit code | Meaning |
| --- | --- |
| `0` | The mode is valid. |
| `1` | The mode is invalid or could not be read; stderr names the mode and the reason. |
| `2` | Usage error (bad arguments or an invalid `--modes-root` path). |
| `130` | Cancelled. |

## List Modes

See which modes exist and what they do, without a run:

```powershell
dotnet run --project src/CognitiveRuntime.Cli -- modes list
dotnet run --project src/CognitiveRuntime.Cli -- modes list --modes-root path/to/modes
```

`modes list` loads every directory directly under the modes root through
the same Core loader that `modes validate` uses (there is no second
validator). Each valid mode is printed to stdout with its name, version,
description, and phases (name and kind) in declared order. A mode whose
prompts live only under lens variants, such as `lens`, is valid: it is
listed with the lenses that load (`Lenses: warcraft`). A lens that fails to
load is reported as invalid for that lens and left out of the list; a mode
with no usable lens is not listed. Directories that are not valid modes are
named on stderr with the loader's reason, and never stop the valid modes from
being listed. Ordinary files in the root are ignored. It makes no model call,
needs no credentials or input file, and writes no run output. `--modes-root`
defaults to `./modes`.

| Exit code | Meaning |
| --- | --- |
| `0` | Every directory is a valid mode. An existing root with no mode directories also exits `0` and prints a no-modes message. |
| `1` | Some directories or lenses are invalid or unreadable. The valid modes are still printed to stdout and each problem goes to stderr. Also used when the modes root is missing or unreadable. |
| `2` | Usage error (bad arguments or an invalid `--modes-root` path). |
| `130` | Cancelled. |

## Static HTML Inspection

The runtime may emit static HTML inspection artifacts for completed runs. These
files are artifacts, not a web application. They make the loop inspectable
without moving orchestration into the browser.

`index.html` is opt-in with `--html`. It opens directly from the filesystem and
contains a runtime-derived pattern graph plus read-only summaries of the run,
mode, phases, tool policy decisions, evals, trace, and artifact links. Pattern
nodes show executed steps or stages; directed relationships show phase context
or authoritative-revision handoffs. It has no JavaScript, external assets,
server, editing controls, approval controls, or rerun controls.

## Runtime Shape

The CLI parses arguments, loads environment configuration, builds dependency
injection, calls the orchestrator, and maps the result to an exit code.

The core runtime owns mode loading, phase order, model calls, tool policy,
artifact paths, trace events, evaluation, and completion. Mode-specific prose
lives under `modes/`.

## Orchestration Patterns

Every run executes one `IOrchestrationPattern`, selected with `--pattern`
(default `critic-revision`). A pattern produces an immutable, data-only plan
containing stable node IDs, mode sources, dependencies, context inputs, stage
groups, an authoritative output, and an eval profile.

The runtime validates the complete bounded plan before the first model call.
One shared executor then loads modes, assembles node context, invokes models,
emits traces, writes stage artifacts, and returns the authoritative result for
all registered patterns. Pattern implementations do not receive runtime
services, and the orchestrator does not branch on a pattern name.

During execution, the orchestrator advances an immutable `RunState` through
small runtime-owned updates. It captures run identity, the resolved plan,
artifact paths, loaded modes, execution-node state, lifecycle status,
evaluation, and terminal outcome. Every planned node begins `pending`; the
shared executor alone advances it through `running` to `completed`, `failed`,
or `cancelled`. Node state records its stable node and stage IDs, phase, mode,
provider, model, start and end times, and output length. Pipeline stages group
these same node states by stage ID instead of introducing a separate execution
model. Provider credentials and provider-specific configuration are not stored
in core run state.

Run identity is created through `IRunIdGenerator`. Production uses
collision-resistant GUIDs; tests can inject fixed IDs. The full run ID is part
of the timestamped output-directory name, and an existing computed directory
is rejected rather than reused.

Run lifecycle transitions are validated by a runtime-owned state machine:

```text
created -> running -> execution-completed -> evaluating -> finalizing -> succeeded
```

Failure or cancellation may terminate any non-terminal state. Invalid edges
throw before state changes, and `succeeded`, `failed`, and `cancelled` cannot
transition again. A `succeeded` lifecycle means finalization completed; the
separate run outcome remains either `success` or `evalFailed`.

The runtime also enforces a fixed, typed `RunBudget` from outside the model
clients: maximum input characters, model calls, phase output characters, and
run duration. These are runtime-owned configuration the model cannot read or
change; defaults are generous enough to leave normal runs untouched. A breach
emits a `budget.exceeded` trace event carrying the limit and observed value and
ends the run as a terminal budget failure.

- `single-pass` - runs the mode's main phase only. Its output is the
  authoritative result.
- `critic-revision` (default) - runs the bounded sequence
  `main -> critic -> revision`. The main phase receives no prior results. The
  critic receives the typed main result. The revision receives typed main and
  critic results, preserves the mode's output contract, and becomes the
  authoritative answer. `result.md` places that revision first, with the
  initial draft and critic review retained as inspectable supporting context.
  Deterministic evals validate the revision itself, so headings in an appendix
  cannot make a malformed revision pass.
- `linear-pipeline` - runs a runtime-configured, ordered sequence of modes as
  stages, set with `--pipeline <mode,mode,...>`. Each stage is a complete
  `critic-revision` run of one mode, fed by the previous stage's authoritative
  revision (or the run's initial input for the first stage). The stage list is
  fixed at construction; the model cannot add, remove, reorder, or repeat
  stages. Each stage writes `input.md` and `result.md` under
  `stages/NN-<mode>/`.
- `scatter-gather` - runs a fixed set of independent branch modes, set with
  `--scatter <mode,mode,...>`, then gathers their typed outputs into one
  authoritative synthesis node (the `--mode` mode). Branch count and modes are
  fixed by the plan; the model cannot add branches or skip the gather. The
  gather reads every branch in declared order, so a missing branch fails
  context assembly rather than being silently dropped. Independent branches run
  concurrently (bounded by a runtime concurrency cap), while results are
  assembled in declared order, so artifacts and context stay deterministic
  regardless of completion order. Each branch's phase output is grouped under
  `scatter/NN-<mode>/`
  ([`RB-008`](docs/research/RB-008-bounded-scatter-gather.md)).

```powershell
dotnet run --project src/CognitiveRuntime.Cli -- `
  --pattern linear-pipeline `
  --pipeline frame,challenge `
  --input examples/agent_runtime_goal.txt `
  --run-mode mock `
  --html
```

The model produces reasoning content inside each phase. It does not choose
phase order, request another revision, skip evaluation, write artifacts, or
declare the run complete. Adversarial integration tests return valid model
content that explicitly asks to switch patterns, skip or repeat phases, add a
pipeline stage, and declare completion; the runtime still executes only the
configured pattern in its declared order.

The canonical run-scoped information vocabulary is documented in
[`ADR-001`](docs/architecture/ADR-001-canonical-agent-information-model.md).
Observations, evidence, claims, beliefs, blackboard entries, and context
projections have distinct typed identities and provenance. Model output may
form a `ClaimProposal`; only runtime code may commit shared information state.

## Portable Storage

Persistence is disabled by default and split into independent ports:

- `IRunStateStore` holds queryable run lifecycle and catalog state.
- `IArtifactStore` holds opaque Markdown, JSON, HTML, and binary artifacts.
- `ISemanticIndex` is an optional projection for retrieval, not runtime state.

The relational run-state adapter supports PostgreSQL and SQL Server. Deploy
the corresponding ADO.NET driver, then set:

```text
COGNITIVE_RUNTIME_RUN_STATE_PROVIDER=postgresql|sqlserver
COGNITIVE_RUNTIME_RUN_STATE_CONNECTION_STRING=<provider connection string>
COGNITIVE_RUNTIME_RUN_STATE_FACTORY_TYPE=<assembly-qualified DbProviderFactory type>
```

Typical factory types are `Npgsql.NpgsqlFactory, Npgsql` and
`Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient`.
The previous `COGNITIVE_RUNTIME_DATABASE_*` names remain accepted as
backward-compatible aliases.

Artifact mirroring is configured separately. The included credential-free
adapter writes a portable object layout to a directory:

```powershell
$env:COGNITIVE_RUNTIME_ARTIFACT_PROVIDER = "directory"
$env:COGNITIVE_RUNTIME_ARTIFACT_ROOT = "C:\runtime-artifacts"

dotnet run --project src/CognitiveRuntime.Cli -- `
  --mode frame `
  --input examples/agent_runtime_goal.txt `
  --run-mode mock `
  --html
```

If `COGNITIVE_RUNTIME_ARTIFACT_ROOT` is omitted, the directory adapter uses
`outputs/.artifact-store`. Each object keeps a normalized relative path,
media type, byte length, SHA-256 hash, timestamp, and opaque bytes. This
handles `trace.json`, `index.html`, Markdown, pipeline stage files, and future
binary artifacts without relying on database JSON or XML column types.

`PortableStorageTransfer.CopyAsync` copies run records and artifacts between
any implementations of the separate ports. A deployment can therefore move
PostgreSQL to Azure SQL independently from moving a directory or S3-compatible
object store to Azure Blob Storage. `ISemanticIndex` can be rebuilt from
artifacts and is deliberately excluded from authoritative run state.

The filesystem remains the canonical artifact location. Database records are
not recovery checkpoints and do not make interrupted runs resumable. See
[`ADR-002`](docs/architecture/ADR-002-portable-run-storage.md) for the storage
decision and
[`ADR-003`](docs/architecture/ADR-003-azure-foundry-deployment-profile.md) for
the Azure/Foundry deployment mapping.

Deterministic evaluation follows the selected pattern. `single-pass` validates
its main phase as the authoritative result. `critic-revision` and each
`linear-pipeline` stage validate the authoritative revision and require the
critic-response efficacy check. Mock mode supplies deterministic,
contract-satisfying output for all three patterns, so each can run and pass
without credentials.

The trace wraps each run's phase (or stage) events in `pattern.started` and
`pattern.completed` events recording the resolved pattern name and its
declared plan. For `linear-pipeline`, each stage additionally emits
`stage.started` and `stage.completed` so its phase events are bounded within
the overall pattern lifecycle. The `pattern.md` artifact renders the resolved
pattern's steps (with each step's selected context) or stages (with each
stage's input source) from this same typed data.

Core trace event names are defined once in `TraceEventNames`. Runtime
producers, deterministic evals, lifecycle terminal-event projection, and
static-view readers use that contract rather than repeating event-name
literals. Core event payload keys (node ID, stage, phase, call ID, outcome,
and the rest) are likewise defined once in `TracePayloadKeys`, so producers
and consumers cannot drift on the magic strings they share. Both contracts are
key/name definitions only: event payloads remain ordinary JSON-compatible
dictionaries and the serialized trace exposes no implementation type names.
(The resolved-plan projection inside `pattern.started`/`pattern.completed`
mirrors the `run.json` plan structure rather than these flat event keys.)

`trace.json` uses schema version `1`. Every event has a contiguous,
monotonically increasing `sequence` beginning at `1`. `JsonTraceReader` is the
typed file boundary: it accepts the current schema, validates event sequencing,
and rejects unsupported future schema versions with a clear error.

Each model call has a runtime-derived `callId`, stable execution-node ID, and
attempt number. A `model.called` event is paired with exactly one
`model.completed` or `model.failed` event using that ID rather than trace
position. `ModelCallTraceAnalyzer` detects missing, duplicate, or inconsistent
pairing, and the deterministic eval report includes this check for every
resolved execution plan.

Successful phase, model-call, execution-node, evaluation, and terminal run
events record runtime-measured `durationMs` values. Failed model calls, failed
or cancelled running nodes, and failed or cancelled runs retain their elapsed
duration as well. All duration measurement uses the injected `TimeProvider`,
so tests do not depend on wall-clock timing.

`model.failed`, `node.failed`, and `run.failed` use one sanitized failure
contract containing category, phase, provider, exception type, and a bounded
safe message. Authorization values, API keys, tokens, passwords, query-string
credentials, control characters, and excess response text are removed before
failure data reaches traces or failure summary artifacts.

Every terminal run writes `run.json` before its terminal trace event. The
schema-versioned manifest records run identity, requested mode, loaded mode
versions, provider and model identifiers, start and end times, lifecycle and
outcome, the resolved pattern plan, execution-node and stage outcomes,
relative artifact inventory, eval summary, and sanitized failure information
when applicable. Each artifact in the inventory records its SHA-256 hash and
byte length, computed from the final bytes on disk, so a verifier can detect a
modified or truncated file. `run.json` and `trace.json` are still being written
at manifest time (the manifest cannot hash itself, and the terminal trace event
is appended after `run.json`), so both record their own integrity as unknown
rather than a value that will never match the final bytes. The same integrity
inventory is recorded by the dungeon experiment's `run.json`. Serialization is
deterministic for a fixed run, and the manifest contains no credentials or
provider request bodies.

The runtime tracks each required artifact through an explicit ledger of
`planned`, `written`, and `failed` states rather than reserving a placeholder
`eval_report.md` for the eval to find. The planned set is announced with an
`artifact.reserved` event; each artifact becomes `written` on success or
`failed` when its write is attempted but does not complete. The
required-artifact eval reads this ledger (no placeholder text), and `run.json`
records it so a partial run distinguishes which artifacts were planned,
written, and failed.

The shared executor also emits stable `node.started` and terminal
`node.completed`, `node.failed`, or `node.cancelled` events. Deterministic
evaluation compares these events and their context-source IDs against the
resolved plan, verifies dependency and pipeline-stage ordering, rejects
missing, duplicate, or undeclared nodes, and confirms the authoritative node
matches the content evaluated against the output contract.

`run.completed` marks completion of the cognitive phase loop. Deterministic
post-run evaluation and configured optional artifacts follow.
`run.finalized` is the single terminal success event and means all configured
post-run work was persisted. `run.failed` is the single terminal failure event
and follows best-effort required failure artifacts. `run.cancelled` is the
terminal cancellation event and is written with best-effort cancellation
artifacts. Terminal event names and lifecycle metadata are derived from the
validated terminal transition. A trace does not contain more than one terminal
outcome. `TerminalTraceIntegrityEvaluator` verifies completed traces, and the
JSON trace session rejects any attempt to append an event after a terminal
outcome.

## Lenses

The `lens` mode explains a concept by mapping it onto a hobby the reader
already has intuition for. Unlike other modes, it has no default prompt set;
`--lens <name>` selects which `modes/lens/prompts/<name>/` directory supplies
the main, critic, and revision prompts. `mode.json`, `MODE.md`, and the output
contract are shared by every lens.

```text
dotnet run --project src/CognitiveRuntime.Cli -- --mode lens --lens warcraft --input examples/agent_runtime_goal.txt --run-mode mock --html
```

Adding a hobby lens means adding three prompt files under
`modes/lens/prompts/<name>/` — no runtime code changes are required. See
[`modes/lens/MODE.md`](modes/lens/MODE.md) for the contract each lens prompt
set must satisfy.

## Model Providers

The default provider is `mock`. It is deterministic and requires no credentials.

GitHub Models calls the versioned GitHub REST inference API. Create a
fine-grained personal access token with the `models:read` permission. Keep the
token out of files and set it only in the current shell:

Command Prompt:

```bat
set "GITHUB_TOKEN=YOUR_NEW_TOKEN"
dotnet run --project src/CognitiveRuntime.Cli -- --mode frame --input examples/agent_runtime_goal.txt --run-mode github-models
```

PowerShell:

```powershell
$secureToken = Read-Host "GitHub PAT (models:read)" -AsSecureString
$env:GITHUB_TOKEN = [System.Net.NetworkCredential]::new("", $secureToken).Password

dotnet run --project src/CognitiveRuntime.Cli -- `
  --mode frame `
  --input examples/agent_runtime_goal.txt `
  --run-mode github-models
```

The default model is `openai/gpt-4.1`. These optional settings override the
GitHub Models defaults:

```text
GITHUB_MODELS_TOKEN=
GITHUB_MODELS_MODEL=openai/gpt-4.1
GITHUB_MODELS_ENDPOINT=https://models.github.ai/inference
GITHUB_MODELS_API_VERSION=2026-03-10
```

`GITHUB_MODELS_TOKEN` takes precedence over `GITHUB_TOKEN` when both are set.
GitHub's free API usage is rate limited. GPT-5-family models require a paid
Copilot plan; Copilot Free accounts are limited to lower-tier models such as
`openai/gpt-4.1`.

OpenRouter calls its OpenAI-compatible Responses API
(`POST /responses`) and provides access to models from multiple vendors
(including GPT-6.x and Claude Opus 5.x) through a single API key:

```powershell
$secureKey = Read-Host "OpenRouter API key" -AsSecureString
$env:OPENROUTER_API_KEY = [System.Net.NetworkCredential]::new("", $secureKey).Password
$env:OPENROUTER_MODEL = "openai/gpt-6.2-sol"

dotnet run --project src/CognitiveRuntime.Cli -- `
  --mode frame `
  --input examples/agent_runtime_goal.txt `
  --run-mode openrouter
```

```text
OPENROUTER_API_KEY=
OPENROUTER_MODEL=
OPENROUTER_ENDPOINT=https://openrouter.ai/api/v1
OPENROUTER_ENABLE_CODE_EXECUTION=false
```

`OPENROUTER_MODEL` must be a valid OpenRouter model ID, for example
`openai/gpt-6.2-sol` or `anthropic/claude-opus-5.5`.

When `OPENROUTER_ENABLE_CODE_EXECUTION=true`, the openrouter provider attaches
OpenAI's hosted `code_interpreter` tool (`{ type: "code_interpreter",
container: { type: "auto" } }`) to each Responses API request. The provider
runs the sandboxed code on OpenRouter's infrastructure and returns the result
inline in the model's message; this is separate from the runtime's own
`IToolProvider`/`ToolPolicy`/`ToolExecutor` boundary described under
"Tool Safety". It was confirmed with `openai/gpt-5`, which OpenRouter retires on
2026-12-11; it has not been re-confirmed with `openai/gpt-6.2-sol` yet.

Azure Foundry calls the same Responses API shape as OpenRouter, against an
Azure OpenAI / Microsoft Foundry resource
(`POST {endpoint}/openai/v1/responses`):

```powershell
$secureKey = Read-Host "Azure Foundry API key" -AsSecureString
$env:AZURE_FOUNDRY_API_KEY = [System.Net.NetworkCredential]::new("", $secureKey).Password
$env:AZURE_FOUNDRY_ENDPOINT = "https://YOUR-RESOURCE-NAME.openai.azure.com"
$env:AZURE_FOUNDRY_DEPLOYMENT = "gpt-5"

dotnet run --project src/CognitiveRuntime.Cli -- `
  --mode frame `
  --input examples/agent_runtime_goal.txt `
  --run-mode azure-foundry
```

```text
AZURE_FOUNDRY_ENDPOINT=
AZURE_FOUNDRY_API_KEY=
AZURE_FOUNDRY_DEPLOYMENT=
AZURE_FOUNDRY_API_VERSION=
```

`AZURE_FOUNDRY_ENDPOINT` is the resource base URL
(`https://<resource>.openai.azure.com`); `/openai/v1/responses` is appended
automatically. `AZURE_FOUNDRY_DEPLOYMENT` is sent as the `model` field and
must name a deployment that supports the Responses API. `AZURE_FOUNDRY_API_VERSION`
is optional; if set, it is appended as an `api-version` query parameter for
resources still on a dated preview API version. Authentication uses the
`api-key` header (API key auth), not a bearer token.

## Tool Safety

Modes do not call tools in the MVP. The `IToolProvider`, `ToolPolicy`, and
`ToolExecutor` boundaries are present for later wiring. Tools are blocked unless
allowlisted, writes require runtime approval and an in-run target path, execute
tools stay blocked, and external tools require detailed tracing.

## Related Projects

`projects/document-distiller` is a separate portfolio application that consumes
this runtime. It has its own solution, tests, providers, and `AGENTS.md`; see
its README for setup and usage.
