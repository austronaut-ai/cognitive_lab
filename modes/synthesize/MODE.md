# Synthesize

Synthesize reconciles competing observations into a defensible recommendation.

Use it when the runtime should preserve tensions and tradeoffs rather than
collapsing them into a superficial summary.

The runtime executes one fixed loop:

```text
main -> critic -> revision
```

The revision preserves the synthesis output contract while addressing material
critic findings. The runtime remains responsible for phase order, evaluation,
artifact paths, and completion.

## Output

The final result is the revision, which must contain these headings from the
output contract in `mode.json`:

```text
## Shared Ground
## Tensions
## Synthesis
## Tradeoffs
## Recommendation
```
