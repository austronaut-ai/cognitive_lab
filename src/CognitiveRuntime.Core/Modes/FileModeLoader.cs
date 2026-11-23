using System.Text.Json;
using System.Text.Json.Serialization;
using CognitiveRuntime.Core.Abstractions;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Exceptions;

namespace CognitiveRuntime.Core.Modes;

public sealed class FileModeLoader : IModeLoader
{
    /// <summary>
    /// The one <c>mode.json</c> schema version this runtime reads. A manifest must declare exactly this value
    /// in its top-level <c>schemaVersion</c>; the mode's own <c>version</c> is unrelated.
    /// </summary>
    public const int SupportedSchemaVersion = 1;

    private const string SchemaVersionField = "schemaVersion";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    // Same comment and trailing-comma tolerance as JsonOptions, for the raw schema-version check.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private static readonly string[] RequiredPromptReferences =
    [
        "prompts/main.md",
        "prompts/critic.md",
        "prompts/revision.md"
    ];

    private readonly string _modesRoot;

    public FileModeLoader(string modesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modesRoot);
        _modesRoot = Path.GetFullPath(modesRoot);
    }

    public async Task<LoadedMode> LoadAsync(
        string modeName,
        string? lens = null,
        CancellationToken cancellationToken = default)
    {
        ValidateModeName(modeName);
        if (lens is not null)
        {
            ValidateLensName(lens);
        }

        var modeDirectory = ResolveChildPath(_modesRoot, modeName);
        if (!Directory.Exists(modeDirectory))
        {
            throw new ModeLoadException(
                $"Mode '{modeName}' was not found under '{_modesRoot}'.");
        }

        if (lens is not null && !Directory.Exists(Path.Combine(modeDirectory, "prompts", lens)))
        {
            throw new ModeLoadException(
                $"Lens '{lens}' was not found for mode '{modeName}'.");
        }

        var documentationPath = RequireFile(modeDirectory, "MODE.md");
        var manifestPath = RequireFile(modeDirectory, "mode.json");

        ModeManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
            RequireSupportedSchemaVersion(manifestPath, json);
            manifest = JsonSerializer.Deserialize<ModeManifest>(json, JsonOptions)
                ?? throw new ModeLoadException(
                    $"Mode manifest '{manifestPath}' was empty.");
        }
        catch (JsonException exception)
        {
            throw new ModeLoadException(
                $"Mode manifest '{manifestPath}' could not be read: {DescribeJsonError(exception)}",
                exception);
        }

        ValidateManifest(modeName, manifest);

        var promptPaths = ResolvePhasePrompts(modeDirectory, manifest, lens);
        ValidateRequiredPromptCoverage(modeDirectory, promptPaths, lens);
        RequireLensWhenOnlyVariantsExist(modeName, modeDirectory, lens);

        var phases = new List<LoadedPhase>(manifest.Phases.Count);
        for (var index = 0; index < manifest.Phases.Count; index++)
        {
            var phase = manifest.Phases[index];
            var promptPath = RequirePromptFile(modeDirectory, promptPaths[index]);
            var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ModeLoadException(
                    $"Prompt '{phase.Prompt}' for phase '{phase.Name}' is empty.");
            }

            phases.Add(new LoadedPhase(phase.Name, phase.Kind, prompt)
            {
                OutputContract = PhaseContractDefaults.Resolve(phase, manifest.OutputContract),
                DeclaresOutputContract = phase.OutputContractSpecified
            });
        }

        var documentation = await File.ReadAllTextAsync(
            documentationPath,
            cancellationToken);
        ValidateDocumentation(modeName, documentation, phases);

        return new LoadedMode(modeDirectory, documentation, manifest, phases);
    }

    /// <summary>
    /// Checks that <c>MODE.md</c> is not blank, names every configured phase as a whole word
    /// (ordinal, case-insensitive), and contains each required heading of the authoritative
    /// revision contract verbatim (ordinal, case-sensitive, hashes included). Prose quality,
    /// layout, and ordering are not checked. Every missing item is reported in one diagnostic.
    /// </summary>
    private static void ValidateDocumentation(
        string modeName,
        string documentation,
        IReadOnlyList<LoadedPhase> phases)
    {
        var missingPhases = phases
            .Select(phase => phase.Name)
            .Where(name => !ContainsWholeWord(documentation, name))
            .ToList();

        // Manifest validation guarantees one revision phase; PhaseContractDefaults guarantees its contract.
        var revisionContract = phases.Single(phase => phase.Kind == PhaseKind.Revision).OutputContract!;
        var missingHeadings = revisionContract.RequiredHeadings
            .Where(heading => !documentation.Contains(heading, StringComparison.Ordinal))
            .ToList();

        var blank = string.IsNullOrWhiteSpace(documentation);
        if (!blank && missingPhases.Count == 0 && missingHeadings.Count == 0)
        {
            return;
        }

        var problems = new List<string>();
        if (blank)
        {
            problems.Add("the file is empty or contains only whitespace");
        }

        if (missingPhases.Count > 0)
        {
            problems.Add($"missing phase names {Quote(missingPhases)}");
        }

        if (missingHeadings.Count > 0)
        {
            problems.Add($"missing output headings {Quote(missingHeadings)}");
        }

        throw new ModeLoadException(
            $"Mode '{modeName}' documentation 'MODE.md' does not describe the configured mode: " +
            string.Join("; ", problems) + ". MODE.md must name each phase and contain each " +
            "required heading of the revision output contract exactly as written.");
    }

    private static string Quote(IEnumerable<string> values) =>
        string.Join(", ", values.Select(value => $"'{value}'"));

    /// <summary>
    /// True when <paramref name="word"/> occurs in <paramref name="text"/> (ordinal, ignoring case) with no
    /// letter, digit, underscore, or hyphen directly on either side, so <c>draft</c> does not match inside
    /// <c>pre-draft</c> or <c>draft_extra</c>. A partial occurrence does not stop the search.
    /// </summary>
    private static bool ContainsWholeWord(string text, string word)
    {
        for (var start = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
             start >= 0;
             start = text.IndexOf(word, start + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = start + word.Length;
            if ((start == 0 || !IsWordContinuation(text[start - 1])) &&
                (end == text.Length || !IsWordContinuation(text[end])))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWordContinuation(char character) =>
        char.IsLetterOrDigit(character) || character is '_' or '-';

    /// <summary>
    /// Loads every immediate child directory of the modes root through
    /// <see cref="LoadAsync"/> and reports what loaded and what did not. A mode that needs a
    /// lens is listed with each lens that loads; lenses that fail are reported separately.
    /// Per-directory failures never stop the listing; an unreadable root and
    /// cancellation propagate.
    /// </summary>
    public async Task<ModeCatalog> ListAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var names = Directory.EnumerateDirectories(_modesRoot)
            .Select(directory => Path.GetFileName(directory))
            .Order(StringComparer.Ordinal)
            .ToList();

        var entries = new List<ModeCatalogEntry>();
        var diagnostics = new List<ModeCatalogDiagnostic>();
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await CatalogDirectoryAsync(name, entries, diagnostics, cancellationToken);
        }

        return new ModeCatalog(entries, diagnostics);
    }

    private async Task CatalogDirectoryAsync(
        string name,
        List<ModeCatalogEntry> entries,
        List<ModeCatalogDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        // Directory names come from the filesystem, so apply the loader's name rule here rather than letting
        // LoadAsync's argument guard (for example, a whitespace-only name) abort the whole listing.
        if (!IsSafeName(name))
        {
            diagnostics.Add(new ModeCatalogDiagnostic(name, null, UnsafeModeNameMessage(name)));
            return;
        }

        try
        {
            var mode = await LoadAsync(name, null, cancellationToken);
            entries.Add(ToEntry(mode, []));
        }
        catch (ModeLoadException exception) when (exception.RequiredLenses is { } lenses)
        {
            await CatalogLensesAsync(name, lenses, entries, diagnostics, cancellationToken);
        }
        catch (Exception exception) when (IsLoadFailure(exception))
        {
            diagnostics.Add(new ModeCatalogDiagnostic(name, null, exception.Message));
        }
    }

    private async Task CatalogLensesAsync(
        string name,
        IReadOnlyList<string> lenses,
        List<ModeCatalogEntry> entries,
        List<ModeCatalogDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        LoadedMode? first = null;
        var usable = new List<string>();
        foreach (var lens in lenses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var mode = await LoadAsync(name, lens, cancellationToken);
                first ??= mode;
                usable.Add(lens);
            }
            catch (Exception exception) when (IsLoadFailure(exception))
            {
                diagnostics.Add(new ModeCatalogDiagnostic(name, lens, exception.Message));
            }
        }

        if (first is not null)
        {
            entries.Add(ToEntry(first, usable));
        }
    }

    private static bool IsLoadFailure(Exception exception) =>
        exception is ModeLoadException or IOException or UnauthorizedAccessException;

    private static ModeCatalogEntry ToEntry(LoadedMode mode, IReadOnlyList<string> lenses) =>
        new(
            mode.Manifest.Name,
            mode.Manifest.Version,
            mode.Manifest.Description,
            mode.Phases.Select(phase => new ModeCatalogPhase(phase.Name, phase.Kind)).ToList(),
            lenses);

    /// <summary>
    /// A mode whose prompts live only under lens variants cannot load without a
    /// lens. Say so and list the choices instead of reporting a bare missing file.
    /// A partial default prompt set keeps the ordinary missing-file diagnostic.
    /// </summary>
    private static void RequireLensWhenOnlyVariantsExist(
        string modeName,
        string modeDirectory,
        string? lens)
    {
        if (lens is not null)
        {
            return;
        }

        var defaults = RequiredPromptReferences
            .Select(reference => ResolveChildPath(modeDirectory, reference))
            .ToArray();
        if (defaults.Any(File.Exists))
        {
            return;
        }

        var lenses = ListAvailableLenses(modeDirectory);
        if (lenses.Count == 0)
        {
            return;
        }

        throw new ModeLoadException(
            $"Mode '{modeName}' requires a lens; its prompts are only available under " +
            $"lens variants. Pass --lens <name>. Available lenses: {string.Join(", ", lenses)}.",
            lenses);
    }

    private static List<string> ListAvailableLenses(string modeDirectory)
    {
        var promptsDirectory = Path.Combine(modeDirectory, "prompts");
        if (!Directory.Exists(promptsDirectory))
        {
            return [];
        }

        RejectLinks(modeDirectory, promptsDirectory);
        return Directory.EnumerateDirectories(promptsDirectory)
            .Where(directory => !new DirectoryInfo(directory).Attributes
                .HasFlag(FileAttributes.ReparsePoint))
            .Select(directory => Path.GetFileName(directory))
            .Where(IsSafeName)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Checks the raw top-level <c>schemaVersion</c> token before the manifest is interpreted, so an
    /// unsupported manifest is reported as such rather than by whichever field changed, and so the serializer
    /// cannot coerce a quoted number. The field is located the way the serializer binds it (ignoring case).
    /// Declaring it more than once is an error, so deserialization only ever sees the one declaration this
    /// check accepted. Malformed JSON throws
    /// <see cref="JsonException"/>; a root that is not an object is left for deserialization to report.
    /// </summary>
    private static void RequireSupportedSchemaVersion(string manifestPath, string json)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var declarations = document.RootElement.EnumerateObject()
            .Where(property => string.Equals(property.Name, SchemaVersionField, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value)
            .ToList();
        if (declarations.Count > 1)
        {
            throw new ModeLoadException(
                $"Mode manifest '{manifestPath}' field '{SchemaVersionField}' is declared {declarations.Count} times " +
                $"(names compare ignoring case); declare it once, as {SchemaVersionField} {SupportedSchemaVersion}.");
        }

        JsonElement? declared = declarations.Count == 1 ? declarations[0] : null;

        if (declared is { ValueKind: JsonValueKind.Number } number &&
            number.TryGetInt32(out var version) &&
            version == SupportedSchemaVersion)
        {
            return;
        }

        var found = declared is { } value ? $"is {value.GetRawText()}" : "is missing";
        throw new ModeLoadException(
            $"Mode manifest '{manifestPath}' field '{SchemaVersionField}' {found}; this runtime supports " +
            $"only {SchemaVersionField} {SupportedSchemaVersion} (an integer, separate from the mode's " +
            "'version').");
    }

    private static string DescribeJsonError(JsonException exception)
    {
        var location = new List<string>();
        if (!string.IsNullOrEmpty(exception.Path))
        {
            location.Add($"field '{exception.Path}'");
        }

        if (exception.LineNumber is { } line)
        {
            var position = exception.BytePositionInLine is { } column
                ? $"line {line + 1}, byte {column + 1}"
                : $"line {line + 1}";
            location.Add(position);
        }

        var where = location.Count == 0 ? string.Empty : $" ({string.Join(", ", location)})";
        // The first sentence only (the rest repeats the path and position); split on ". " so ".NET" stays whole.
        return exception.Message.Split(". ")[0].TrimEnd('.') + "." + where;
    }

    private static void ValidateModeName(string modeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modeName);

        if (!IsSafeName(modeName))
        {
            throw new ModeLoadException(UnsafeModeNameMessage(modeName));
        }
    }

    private static string UnsafeModeNameMessage(string modeName) =>
        $"Mode name '{modeName}' contains unsupported characters: {NameRule}";

    private static void ValidateLensName(string lens)
    {
        if (!IsSafeName(lens))
        {
            throw new ModeLoadException(
                $"Lens name '{lens}' contains unsupported characters: {NameRule}");
        }
    }

    /// <summary>
    /// Rewrites a mode-relative prompt path to load from a lens-specific
    /// subdirectory, e.g. <c>prompts/main.md</c> becomes
    /// <c>prompts/warcraft/main.md</c> when <paramref name="lens"/> is
    /// <c>warcraft</c>.
    /// </summary>
    private static string ApplyLens(string relativePath, string? lens)
    {
        if (string.IsNullOrEmpty(lens))
        {
            return relativePath;
        }

        const string promptsPrefix = "prompts/";
        if (!relativePath.StartsWith(promptsPrefix, StringComparison.Ordinal))
        {
            throw new ModeLoadException(
                $"Prompt path '{relativePath}' does not support a lens; " +
                $"expected it to start with '{promptsPrefix}'.");
        }

        return promptsPrefix + lens + "/" + relativePath[promptsPrefix.Length..];
    }

    private static void RequireManifestFields(ModeManifest manifest)
    {
        if (manifest.Phases is null)
        {
            throw new ModeLoadException(
                "Mode manifest field 'phases' must be an array of three phases, not null.");
        }

        for (var index = 0; index < manifest.Phases.Count; index++)
        {
            if (manifest.Phases[index] is null)
            {
                throw new ModeLoadException(
                    $"Mode manifest phase {index + 1} in 'phases' must be an object, not null.");
            }
        }

        if (manifest.OutputContract is null)
        {
            throw new ModeLoadException(
                "Mode manifest field 'outputContract' must be an object, not null.");
        }

        if (manifest.OutputContract.RequiredHeadings is null)
        {
            throw new ModeLoadException(
                "Mode manifest field 'outputContract.requiredHeadings' must be " +
                "an array of headings, not null.");
        }
    }

    private static void ValidateManifest(string requestedName, ModeManifest manifest)
    {
        RequireManifestFields(manifest);

        if (!string.Equals(requestedName, manifest.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModeLoadException(
                $"Mode manifest name '{manifest.Name}' does not match requested mode '{requestedName}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.Description))
        {
            throw new ModeLoadException("Mode description is required.");
        }

        var requiredPhaseOrder = new[]
        {
            PhaseKind.Main,
            PhaseKind.Critic,
            PhaseKind.Revision
        };

        if (manifest.Phases.Count != requiredPhaseOrder.Length)
        {
            throw new ModeLoadException(
                "A mode must define exactly three phases in this order: " +
                "main, critic, revision.");
        }

        for (var index = 0; index < manifest.Phases.Count; index++)
        {
            if (!IsSafeName(manifest.Phases[index].Name))
            {
                throw new ModeLoadException(
                    $"Phase {index + 1} name '{manifest.Phases[index].Name}' is invalid: {NameRule}");
            }
        }

        var duplicatePhase = manifest.Phases
            .GroupBy(phase => phase.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicatePhase is not null)
        {
            throw new ModeLoadException(
                $"Phase name '{duplicatePhase.Key}' is duplicated (names are compared " +
                "case-insensitively).");
        }

        for (var index = 0; index < manifest.Phases.Count; index++)
        {
            if (string.IsNullOrWhiteSpace(manifest.Phases[index].Prompt))
            {
                throw new ModeLoadException(
                    $"Phase {index + 1} ('{manifest.Phases[index].Name}') requires a prompt path.");
            }
        }

        for (var index = 0; index < requiredPhaseOrder.Length; index++)
        {
            var expectedKind = requiredPhaseOrder[index];
            var actualKind = manifest.Phases[index].Kind;
            if (actualKind != expectedKind)
            {
                throw new ModeLoadException(
                    $"Phase {index + 1} must be '{FormatKind(expectedKind)}' " +
                    $"but was '{FormatKind(actualKind)}'. Required order: " +
                    "main, critic, revision.");
            }
        }

        ValidateContractRules(manifest.OutputContract, "The output contract");

        for (var index = 0; index < manifest.Phases.Count; index++)
        {
            ValidatePhaseContract(index, manifest.Phases[index]);
        }
    }

    /// <summary>
    /// Applies the mode-level contract rules to a phase's own contract when the
    /// manifest supplies one. Omission is allowed and takes the runtime default.
    /// </summary>
    private static void ValidatePhaseContract(int index, ModePhaseManifest phase)
    {
        if (!phase.OutputContractSpecified)
        {
            return;
        }

        var field = $"Phase {index + 1} ('{phase.Name}') field 'outputContract'";
        if (phase.OutputContract is null)
        {
            throw new ModeLoadException(
                $"{field} must be an object, not null; omit it to use the runtime default.");
        }

        if (phase.OutputContract.RequiredHeadings is null)
        {
            throw new ModeLoadException(
                $"{field}.requiredHeadings must be an array of headings, not null.");
        }

        ValidateContractRules(phase.OutputContract, $"{field} (output contract)");
    }

    private static void ValidateContractRules(OutputContract contract, string subject)
    {
        if (contract.MinimumLength < 1 ||
            contract.RequiredHeadings.Count == 0 ||
            contract.RequiredHeadings.Any(string.IsNullOrWhiteSpace))
        {
            throw new ModeLoadException(
                $"{subject} requires a positive minimum length and at least one heading.");
        }
    }

    private static string FormatKind(PhaseKind kind) =>
        kind.ToString().ToLowerInvariant();

    internal const string NameRule =
        "use only ASCII letters, digits, or hyphens, starting with a letter or digit.";

    /// <summary>
    /// The one rule for mode, lens, and phase names. A leading letter or digit keeps a name from looking like a
    /// command-line option, so every listed lens can be passed as <c>--lens &lt;name&gt;</c> and none reads as a
    /// flag such as <c>-h</c>.
    /// </summary>
    internal static bool IsSafeName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static string RequireFile(string root, string relativePath)
    {
        var path = ResolveChildPath(root, relativePath);
        if (!File.Exists(path))
        {
            throw new ModeLoadException(
                $"Required mode file '{relativePath}' was not found.");
        }

        return path;
    }

    private static string RequirePromptFile(string modeDirectory, string path)
    {
        RejectLinks(modeDirectory, path);
        if (!File.Exists(path))
        {
            throw new ModeLoadException(
                $"Required mode file '{DescribeRelative(modeDirectory, path)}' was not found.");
        }

        return path;
    }

    private static string DescribeRelative(string modeDirectory, string path) =>
        Path.GetRelativePath(modeDirectory, path).Replace('\\', '/');

    private static string[] ResolvePhasePrompts(
        string modeDirectory,
        ModeManifest manifest,
        string? lens)
    {
        var paths = new string[manifest.Phases.Count];
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < paths.Length; index++)
        {
            var phase = manifest.Phases[index];
            paths[index] = ResolvePromptReference(modeDirectory, phase.Prompt, lens);
            if (seen.TryGetValue(paths[index], out var first))
            {
                var other = manifest.Phases[first];
                throw new ModeLoadException(
                    $"Phases '{other.Name}' and '{phase.Name}' reference the same prompt file " +
                    $"('{other.Prompt}' and '{phase.Prompt}'); prompt paths must be unique " +
                    "(compared case-insensitively after normalization).");
            }

            seen[paths[index]] = index;
        }

        return paths;
    }

    private static void ValidateRequiredPromptCoverage(
        string modeDirectory,
        string[] promptPaths,
        string? lens)
    {
        var referenced = new HashSet<string>(promptPaths, StringComparer.Ordinal);
        foreach (var required in RequiredPromptReferences)
        {
            var effective = ResolvePromptReference(modeDirectory, required, lens);
            if (!referenced.Contains(effective))
            {
                throw new ModeLoadException(
                    $"Required prompt '{DescribeRelative(modeDirectory, effective)}' is not " +
                    "referenced by any phase.");
            }
        }
    }

    /// <summary>
    /// Resolves a manifest prompt reference to its effective absolute path.
    /// The reference is checked as written and again after the lens rewrite.
    /// </summary>
    private static string ResolvePromptReference(
        string modeDirectory,
        string reference,
        string? lens)
    {
        RejectRooted(reference);
        var normalized = reference.Replace('\\', '/');
        var contained = ResolveChildPath(modeDirectory, normalized);

        // Apply the lens to the canonical mode-relative path so equivalent
        // spellings (./prompts/x.md, prompts/../prompts/x.md) behave identically.
        var canonical = Path.GetRelativePath(Path.GetFullPath(modeDirectory), contained)
            .Replace('\\', '/');
        return ResolveChildPath(modeDirectory, ApplyLens(canonical, lens));
    }

    private static void RejectRooted(string reference)
    {
        // Rejected on every platform so a manifest means the same thing everywhere.
        var rooted = reference.StartsWith('/') ||
            reference.StartsWith('\\') ||
            (reference.Length >= 2 && char.IsAsciiLetter(reference[0]) && reference[1] == ':') ||
            Path.IsPathRooted(reference);
        if (rooted)
        {
            throw new ModeLoadException(
                $"Prompt path '{reference}' must be relative to the mode directory.");
        }
    }

    /// <summary>
    /// Rejects symbolic links, junctions, and other reparse points on the mode
    /// directory and every component below it down to <paramref name="path"/>.
    /// The configured modes root is the trusted base and is not checked.
    /// </summary>
    private static void RejectLinks(string modeDirectory, string path)
    {
        var current = modeDirectory;
        ThrowIfLink(modeDirectory, modeDirectory, path);
        foreach (var segment in Path.GetRelativePath(modeDirectory, path)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            ThrowIfLink(modeDirectory, current, path);
        }
    }

    private static void ThrowIfLink(string modeDirectory, string component, string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(component);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return; // Missing or unreadable; the existence check reports it.
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ModeLoadException(
                $"Prompt path '{DescribeRelative(modeDirectory, path)}' passes through a " +
                $"symbolic link or junction at '{DescribeRelative(modeDirectory, component)}'; " +
                "links are not allowed inside a mode directory.");
        }
    }

    private static string ResolveChildPath(string root, string relativePath)
    {
        string fullRoot;
        string candidate;
        try
        {
            fullRoot = Path.GetFullPath(root);
            candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ModeLoadException(
                $"Path '{relativePath}' is not a valid file path: {exception.Message}",
                exception);
        }

        // Exactly one trailing separator; a filesystem root already has one.
        var trimmed = Path.TrimEndingDirectorySeparator(fullRoot);
        var rootPrefix = Path.EndsInDirectorySeparator(trimmed)
            ? trimmed
            : trimmed + Path.DirectorySeparatorChar;

        // Ordinal on every OS: a case-only sibling is a different directory on
        // case-sensitive volumes, and rejecting it elsewhere keeps modes portable.
        if (!candidate.StartsWith(rootPrefix, StringComparison.Ordinal))
        {
            throw new ModeLoadException(
                $"Path '{relativePath}' escapes the mode directory.");
        }

        return candidate;
    }
}
