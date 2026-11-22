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
    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

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

public sealed record LoadedPhase(string Name, PhaseKind Kind, string Prompt);

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
