using System.Text.Json.Serialization;

namespace CognitiveRuntime.Core.Contracts;

public enum PhaseKind
{
    Main,
    Critic,
    Revision
}

public sealed record ModeManifest
{
    /// <summary>
    /// The version of the <c>mode.json</c> format itself, not of the mode's content. The loader accepts only
    /// <see cref="CognitiveRuntime.Core.Modes.FileModeLoader.SupportedSchemaVersion"/>; there is no default.
    /// </summary>
    public int SchemaVersion { get; init; }

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>The mode's content version, chosen by the mode author.</summary>
    public int Version { get; init; } = 1;

    public List<ModePhaseManifest> Phases { get; init; } = [];

    public OutputContract OutputContract { get; init; } = new();
}

public sealed record ModePhaseManifest
{
    public string Name { get; init; } = string.Empty;

    [JsonRequired]
    public PhaseKind Kind { get; init; }

    public string Prompt { get; init; } = string.Empty;

    private readonly OutputContract? _outputContract;

    /// <summary>
    /// The phase's own structural contract, or null when the manifest omits it.
    /// An omitted contract takes the runtime default (see
    /// <see cref="CognitiveRuntime.Core.Modes.PhaseContractDefaults"/>); an explicit JSON null is rejected.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OutputContract? OutputContract
    {
        get => _outputContract;
        init
        {
            _outputContract = value;
            OutputContractSpecified = true;
        }
    }

    /// <summary>
    /// True when the manifest supplied <c>outputContract</c>, even as null.
    /// Internal so the serializer never maps it: a manifest that spells it out is
    /// rejected as an unknown property like any other.
    /// </summary>
    internal bool OutputContractSpecified { get; private init; }
}

public sealed record OutputContract
{
    public List<string> RequiredHeadings { get; init; } = [];

    public int MinimumLength { get; init; } = 1;
}

public sealed record LoadedMode(
    string DirectoryPath,
    string Documentation,
    ModeManifest Manifest,
    IReadOnlyList<LoadedPhase> Phases);

public sealed record LoadedPhase(string Name, PhaseKind Kind, string Prompt)
{
    /// <summary>
    /// The structural contract that applies to this phase's output after the
    /// runtime defaults are resolved at load time. Null means the phase has no
    /// structural contract and is not checked.
    /// </summary>
    public OutputContract? OutputContract { get; init; }

    /// <summary>True when the manifest declared this phase's contract itself.</summary>
    public bool DeclaresOutputContract { get; init; }
}

public sealed record ModeCatalogPhase(string Name, PhaseKind Kind);

/// <summary>A mode that loaded. <c>Lenses</c> is empty unless the mode needs a lens.</summary>
public sealed record ModeCatalogEntry(
    string Name,
    int Version,
    string Description,
    IReadOnlyList<ModeCatalogPhase> Phases,
    IReadOnlyList<string> Lenses);

/// <summary>A mode directory, or one lens of it, that did not load, and why.</summary>
public sealed record ModeCatalogDiagnostic(string Directory, string? Lens, string Reason);

public sealed record ModeCatalog(
    IReadOnlyList<ModeCatalogEntry> Entries,
    IReadOnlyList<ModeCatalogDiagnostic> Diagnostics);
