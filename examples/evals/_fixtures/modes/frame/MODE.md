# Frame (eval fixture)

This is not a shipped mode. It is a copy of `modes/frame` used only by the
intentionally failing eval cases under `examples/evals/`.

It differs from the shipped frame mode in one way: its output contract also
requires `## Risks`. The mock provider answers the `frame` mode with fixed text
that never contains that heading, so a mock run against this fixture fails the
`output contract satisfied` check for a known reason.

The runtime executes the same fixed loop:

```text
main -> critic -> revision
```

## Output

The final result must contain these headings from the output contract in
`mode.json`:

```text
## Problem
## Objective
## Constraints
## Unknowns
## Next Actions
## Risks
```
