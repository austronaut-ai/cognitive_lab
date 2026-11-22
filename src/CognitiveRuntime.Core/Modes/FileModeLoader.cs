using System.Text.Json;
using System.Text.Json.Serialization;
using CognitiveRuntime.Core.Abstractions;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Exceptions;

namespace CognitiveRuntime.Core.Modes;

public sealed class FileModeLoader : IModeLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

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
        RequireFile(modeDirectory, ApplyLens("prompts/main.md", lens));
        RequireFile(modeDirectory, ApplyLens("prompts/critic.md", lens));
        RequireFile(modeDirectory, ApplyLens("prompts/revision.md", lens));

        ModeManifest manifest;
        try
        {
            var json = await File.ReadAllTextAsync(manifestPath, cancellationToken);
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

        var phases = new List<LoadedPhase>(manifest.Phases.Count);
        foreach (var phase in manifest.Phases)
        {
            var promptPath = RequireFile(modeDirectory, ApplyLens(phase.Prompt, lens));
            var prompt = await File.ReadAllTextAsync(promptPath, cancellationToken);
            if (string.IsNullOrWhiteSpace(prompt))
            {
                throw new ModeLoadException(
                    $"Prompt '{phase.Prompt}' for phase '{phase.Name}' is empty.");
            }

            phases.Add(new LoadedPhase(phase.Name, phase.Kind, prompt));
        }

        var documentation = await File.ReadAllTextAsync(
            documentationPath,
            cancellationToken);

        return new LoadedMode(modeDirectory, documentation, manifest, phases);
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

        if (modeName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ModeLoadException(
                $"Mode name '{modeName}' contains unsupported characters.");
        }
    }

    private static void ValidateLensName(string lens)
    {
        if (string.IsNullOrWhiteSpace(lens) ||
            lens.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character == '-')))
        {
            throw new ModeLoadException(
                $"Lens name '{lens}' contains unsupported characters.");
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

        var duplicatePhase = manifest.Phases
            .GroupBy(phase => phase.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicatePhase is not null)
        {
            throw new ModeLoadException(
                $"Phase name '{duplicatePhase.Key}' is duplicated.");
        }

        foreach (var phase in manifest.Phases)
        {
            if (string.IsNullOrWhiteSpace(phase.Name) ||
                string.IsNullOrWhiteSpace(phase.Prompt))
            {
                throw new ModeLoadException(
                    "Every phase requires a name and prompt path.");
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

        if (manifest.OutputContract.MinimumLength < 1 ||
            manifest.OutputContract.RequiredHeadings.Count == 0 ||
            manifest.OutputContract.RequiredHeadings.Any(string.IsNullOrWhiteSpace))
        {
            throw new ModeLoadException(
                "The output contract requires a positive minimum length and at least one heading.");
        }
    }

    private static string FormatKind(PhaseKind kind) =>
        kind.ToString().ToLowerInvariant();

    private static string RequireFile(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new ModeLoadException(
                $"Mode file path '{relativePath}' must be relative.");
        }

        var path = ResolveChildPath(root, relativePath);
        if (!File.Exists(path))
        {
            throw new ModeLoadException(
                $"Required mode file '{relativePath}' was not found.");
        }

        return path;
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

        // A filesystem root (e.g. "C:\") already ends with a separator.
        var rootPrefix = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.StartsWith(rootPrefix, comparison))
        {
            throw new ModeLoadException(
                $"Path '{relativePath}' escapes the mode directory.");
        }

        return candidate;
    }
}
