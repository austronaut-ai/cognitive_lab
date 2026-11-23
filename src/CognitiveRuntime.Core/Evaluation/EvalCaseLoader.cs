using System.Text.Json;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Exceptions;

namespace CognitiveRuntime.Core.Evaluation;

/// <summary>
/// Loads offline eval cases: one JSON file per case under the eval case root (<c>examples/evals</c> in this
/// repository). The loader only reads and validates; it runs nothing. Every problem is reported with the
/// path of the case file it belongs to, and a directory load reports the problems of every file at once.
/// </summary>
public sealed class EvalCaseLoader
{
    /// <summary>The one case schema version this runtime reads, declared as top-level <c>schemaVersion</c>.</summary>
    public const int SupportedSchemaVersion = EvalCaseDocumentReader.SupportedSchemaVersion;

    /// <summary>The pattern a case runs with when it names none: the CLI and <see cref="RunRequest"/> default.</summary>
    public const string DefaultPattern = "critic-revision";

    /// <summary>
    /// The directory directly under the case root that holds fixture data (such as fixture modes) rather than
    /// cases. Cases may reference files inside it; no case is loaded from it.
    /// </summary>
    public const string FixturesDirectoryName = "_fixtures";

    private const string CaseFileExtension = ".json";

    // Same comment and trailing-comma tolerance as the mode manifest.
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _casesRoot;
    private readonly string _fixturesRoot;
    private readonly string _modesRoot;

    /// <param name="casesRoot">
    /// The eval case root. Trusted: cases and their input files must be below it, and a case's
    /// <c>modesRoot</c> must be it or a directory below it.
    /// </param>
    /// <param name="modesRoot">The modes directory a case uses when it declares no <c>modesRoot</c>.</param>
    public EvalCaseLoader(string casesRoot, string modesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(casesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(modesRoot);
        _casesRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(casesRoot));
        _fixturesRoot = Path.Combine(_casesRoot, FixturesDirectoryName);
        _modesRoot = Path.GetFullPath(modesRoot);
    }

    /// <summary>Loads one case file. Throws <see cref="EvalCaseLoadException"/> listing every problem found.</summary>
    public async Task<EvalCase> LoadAsync(string caseFilePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caseFilePath);
        var problems = new EvalCaseProblems(Path.GetFullPath(caseFilePath));
        return await TryLoadAsync(Path.GetFullPath(caseFilePath), problems, cancellationToken)
            ?? throw new EvalCaseLoadException(problems.Messages);
    }

    /// <summary>
    /// Loads every <c>.json</c> case under <paramref name="directory"/> (the whole case root when null),
    /// recursively, skipping the fixtures directory and linked directories. Cases are returned in ordinal
    /// order of their root-relative path written with <c>/</c>, so the order is the same on every OS. One bad
    /// file does not hide another: the exception lists the problems of all of them.
    /// </summary>
    public async Task<IReadOnlyList<EvalCase>> LoadDirectoryAsync(
        string? directory = null,
        CancellationToken cancellationToken = default)
    {
        var start = directory is null
            ? _casesRoot
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        if (DescribeDirectoryProblem(start) is { } problem)
        {
            throw new EvalCaseLoadException([$"Eval case directory '{start}' {problem}"]);
        }

        var cases = new List<EvalCase>();
        var problems = new List<string>();
        foreach (var file in Discover(start))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileProblems = new EvalCaseProblems(file);
            if (await TryLoadAsync(file, fileProblems, cancellationToken) is { } evalCase)
            {
                cases.Add(evalCase);
            }

            problems.AddRange(fileProblems.Messages);
        }

        return problems.Count == 0 ? cases : throw new EvalCaseLoadException(problems);
    }

    private string? DescribeDirectoryProblem(string directory)
    {
        if (directory != _casesRoot && !EvalCasePaths.IsBelow(_casesRoot, directory))
        {
            return $"is outside the eval case root '{_casesRoot}'.";
        }

        if (IsFixturePath(directory))
        {
            return $"is in the reserved '{FixturesDirectoryName}' directory, which holds fixture data, not cases.";
        }

        if (EvalCasePaths.FindLink(_casesRoot, directory) is { } link && directory != _casesRoot)
        {
            return $"passes through a symbolic link or junction at '{link}'; links are not allowed.";
        }

        return Directory.Exists(directory) ? null : "was not found.";
    }

    private List<string> Discover(string start)
    {
        var files = new List<string>();
        try
        {
            Collect(start, files);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new EvalCaseLoadException(
                $"Eval case directory '{start}' could not be read: {exception.Message}",
                exception);
        }

        return files.OrderBy(RootRelative, StringComparer.Ordinal).ToList();
    }

    private void Collect(string directory, List<string> files)
    {
        files.AddRange(Directory.EnumerateFiles(directory).Where(IsCaseFileName));
        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!IsFixturePath(child) && !EvalCasePaths.IsLink(child))
            {
                Collect(child, files);
            }
        }
    }

    private string RootRelative(string path) =>
        Path.GetRelativePath(_casesRoot, path).Replace('\\', '/');

    private static bool IsCaseFileName(string path) =>
        string.Equals(Path.GetExtension(path), CaseFileExtension, StringComparison.Ordinal);

    private bool IsFixturePath(string path) =>
        path == _fixturesRoot || EvalCasePaths.IsBelow(_fixturesRoot, path);

    /// <summary>Returns the case, or null after adding every problem found to <paramref name="problems"/>.</summary>
    private async Task<EvalCase?> TryLoadAsync(
        string path,
        EvalCaseProblems problems,
        CancellationToken cancellationToken)
    {
        if (DescribeSelectionProblem(path) is { } selectionProblem)
        {
            problems.Add(selectionProblem);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            using var document = JsonDocument.Parse(json, DocumentOptions);
            var declared = EvalCaseDocumentReader.Read(
                document.RootElement,
                Path.GetFileNameWithoutExtension(path),
                problems);
            return declared is null ? null : await ResolveAsync(path, declared, problems, cancellationToken);
        }
        catch (JsonException exception)
        {
            problems.Add($"is not valid JSON: {DescribeJsonError(exception)}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            problems.Add($"could not be read: {exception.Message}");
        }

        return null;
    }

    private string? DescribeSelectionProblem(string path)
    {
        if (!EvalCasePaths.IsBelow(_casesRoot, path))
        {
            return $"is outside the eval case root '{_casesRoot}'.";
        }

        if (IsFixturePath(path))
        {
            return $"is in the reserved '{FixturesDirectoryName}' directory, which holds fixture data, not cases.";
        }

        if (!IsCaseFileName(path))
        {
            return $"is not a '{CaseFileExtension}' file.";
        }

        if (EvalCasePaths.FindLink(_casesRoot, path) is { } link)
        {
            return $"passes through a symbolic link or junction at '{link}'; links are not allowed.";
        }

        if (Directory.Exists(path))
        {
            return "is a directory, not a case file.";
        }

        return File.Exists(path) ? null : "was not found.";
    }

    private async Task<EvalCase?> ResolveAsync(
        string path,
        EvalCaseDocument declared,
        EvalCaseProblems problems,
        CancellationToken cancellationToken)
    {
        // References are checked even when the document already has problems, so one load reports them all.
        var caseDirectory = Path.GetDirectoryName(path)!;
        var inputFile = declared.InputFile is null
            ? null
            : ResolveReference(
                caseDirectory, "inputFile", declared.InputFile, allowRoot: false, File.Exists, "a file", problems);
        var modesRoot = declared.ModesRoot is null
            ? _modesRoot
            : ResolveReference(
                caseDirectory,
                "modesRoot",
                declared.ModesRoot,
                allowRoot: true,
                Directory.Exists,
                "a directory",
                problems);

        var input = declared.Input;
        if (inputFile is not null)
        {
            input = await File.ReadAllTextAsync(inputFile, cancellationToken);
            if (string.IsNullOrWhiteSpace(input))
            {
                problems.Add(
                    $"field 'inputFile' ('{declared.InputFile}') names '{inputFile}', which is empty or contains " +
                    "only whitespace.");
            }
        }

        // Any null below was reported as a problem, so a file with no problems is complete.
        return problems.Count > 0
            ? null
            : new EvalCase(
                path,
                declared.Name!,
                declared.Description!,
                declared.Mode!,
                declared.Lens,
                declared.Pattern ?? DefaultPattern,
                input!,
                inputFile,
                modesRoot!,
                declared.Expect!);
    }

    private string? ResolveReference(
        string caseDirectory,
        string field,
        string reference,
        bool allowRoot,
        Func<string, bool> exists,
        string expectedKind,
        EvalCaseProblems problems)
    {
        var target = EvalCasePaths.Resolve(_casesRoot, caseDirectory, reference, allowRoot, out var problem);
        if (target is null)
        {
            problems.Add($"field '{field}' ('{reference}') {problem}.");
            return null;
        }

        if (exists(target))
        {
            return target;
        }

        var found = File.Exists(target) || Directory.Exists(target) ? "is not" : "was not found; it must be";
        problems.Add($"field '{field}' ('{reference}') names '{target}', which {found} {expectedKind}.");
        return null;
    }

    private static string DescribeJsonError(JsonException exception)
    {
        // The first sentence only (the rest repeats the position); split on ". " so ".NET" stays whole.
        var summary = exception.Message.Split(". ")[0].TrimEnd('.') + ".";
        return exception.LineNumber is { } line
            ? $"{summary} (line {line + 1}, byte {(exception.BytePositionInLine ?? 0) + 1})"
            : summary;
    }
}
