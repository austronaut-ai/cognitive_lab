using CognitiveRuntime.Core.Contracts;

namespace CognitiveRuntime.Core.Modes;

/// <summary>
/// The one place that names the runtime defaults for per-phase output contracts.
/// A phase that declares its own <c>outputContract</c> always uses it. Otherwise:
/// <list type="bullet">
///   <item><description>main and revision use the mode-level <c>outputContract</c>;</description></item>
///   <item><description>critic has no structural contract and is not checked.</description></item>
/// </list>
/// The loader resolves these once, so every consumer sees the same contract per phase.
/// </summary>
public static class PhaseContractDefaults
{
    public static OutputContract? Resolve(ModePhaseManifest phase, OutputContract modeContract) =>
        phase.OutputContract ?? Default(phase.Kind, modeContract);

    public static OutputContract? Default(PhaseKind kind, OutputContract modeContract) =>
        kind switch
        {
            PhaseKind.Main => modeContract,
            PhaseKind.Revision => modeContract,
            PhaseKind.Critic => null,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported phase kind.")
        };
}
