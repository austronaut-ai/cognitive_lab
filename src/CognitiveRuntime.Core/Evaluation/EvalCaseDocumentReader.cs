using System.Text.Json;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Modes;

namespace CognitiveRuntime.Core.Evaluation;

/// <summary>Collects the problems found in one eval case file, each prefixed with that file's path.</summary>
internal sealed class EvalCaseProblems(string caseFilePath)
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    public int Count => _messages.Count;

    public void Add(string problem) => _messages.Add($"Eval case '{caseFilePath}' {problem}");
}

/// <summary>
/// The values of a case file as declared, with no path resolved. A field is null when it was omitted or when
/// its value was rejected (and reported), so the document is complete only when its file has no problems.
/// The fields that did decode can still be checked further, which is how a bad reference is reported
/// alongside an unrelated schema problem.
/// </summary>
internal sealed record EvalCaseDocument(
    string? Name,
    string? Description,
    string? Mode,
    string? Lens,
    string? Pattern,
    string? Input,
    string? InputFile,
    string? ModesRoot,
    EvalCaseExpectation? Expect);

/// <summary>
/// Validates the JSON shape of an eval case and reports every problem it finds, not only the first. Property
/// names compare ignoring case, as the mode manifest's do, and each may be declared once. An explicit
/// <c>null</c> is a supplied value, never an omission.
/// </summary>
internal static class EvalCaseDocumentReader
{
    public const int SupportedSchemaVersion = 1;

    private const string SchemaVersionField = "schemaVersion";
    private const int ShownValueLength = 60;

    private static readonly string[] CaseFields =
    [
        SchemaVersionField, "name", "description", "mode", "lens", "pattern",
        "input", "inputFile", "modesRoot", "expect"
    ];

    private static readonly string[] ExpectFields = ["evaluation", "checks"];

    /// <summary>
    /// Returns the values that decoded, adding a problem for each one that did not; the caller builds a case
    /// only when the file ends with no problems. Returns null when the file cannot be interpreted at all: the
    /// root is not an object, the text cannot be decoded, or <c>schemaVersion</c> is unsupported. An
    /// unsupported <c>schemaVersion</c> is the only problem reported for its file: the rest of a file written
    /// for another schema is not interpreted.
    /// </summary>
    public static EvalCaseDocument? Read(JsonElement root, string expectedName, EvalCaseProblems problems)
    {
        try
        {
            return ReadDeclared(root, expectedName, problems);
        }
        catch (InvalidOperationException exception)
        {
            // JsonDocument checks syntax only. Decoding a property name or string whose escapes do not form
            // valid UTF-16 (an unpaired \uD800 surrogate, say) throws here, wherever in the file it sits.
            problems.Add($"contains a property name or string value that cannot be decoded: {exception.Message}");
            return null;
        }
    }

    private static EvalCaseDocument? ReadDeclared(JsonElement root, string expectedName, EvalCaseProblems problems)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"must be a JSON object, not {Show(root)}.");
            return null;
        }

        if (!HasSupportedSchemaVersion(root, problems))
        {
            return null;
        }

        var fields = ReadFields(root, string.Empty, CaseFields, problems);

        var name = RequiredString(fields, "name", problems);
        if (name is not null && !string.Equals(name, expectedName, StringComparison.Ordinal))
        {
            problems.Add(
                $"field 'name' is '{name}' but must equal the file name without .json, '{expectedName}'.");
        }

        var description = RequiredString(fields, "description", problems);
        var mode = RequiredString(fields, "mode", problems);
        RequireSafeName("mode", mode, problems);
        var lens = OptionalString(fields, "lens", problems);
        RequireSafeName("lens", lens, problems);
        var pattern = OptionalString(fields, "pattern", problems);
        var (input, inputFile) = ReadInputChoice(fields, problems);
        var modesRoot = OptionalString(fields, "modesRoot", problems);
        var expect = ReadExpect(fields, problems);

        return new EvalCaseDocument(name, description, mode, lens, pattern, input, inputFile, modesRoot, expect);
    }

    /// <summary>
    /// Checks the raw <c>schemaVersion</c> token, as the mode loader does for <c>mode.json</c>: exactly one
    /// declaration (names compare ignoring case) holding the integer 1. A quoted or fractional number fails.
    /// </summary>
    private static bool HasSupportedSchemaVersion(JsonElement root, EvalCaseProblems problems)
    {
        var declarations = root.EnumerateObject()
            .Where(property => string.Equals(property.Name, SchemaVersionField, StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value)
            .ToList();
        if (declarations.Count > 1)
        {
            problems.Add(
                $"field '{SchemaVersionField}' is declared {declarations.Count} times (names compare ignoring " +
                $"case); declare it once, as {SchemaVersionField} {SupportedSchemaVersion}.");
            return false;
        }

        // The raw token decides, so 1.0 and 1e0 (which the number parser would accept as 1) are rejected.
        if (declarations.Count == 1 &&
            declarations[0].ValueKind == JsonValueKind.Number &&
            declarations[0].GetRawText() == "1")
        {
            return true;
        }

        var found = declarations.Count == 1 ? $"is {Show(declarations[0])}" : "is missing";
        problems.Add(
            $"field '{SchemaVersionField}' {found}; this runtime supports only {SchemaVersionField} " +
            $"{SupportedSchemaVersion} (an integer).");
        return false;
    }

    /// <summary>
    /// Indexes an object's properties by their canonical name, reporting each unknown property and each
    /// property declared more than once. A duplicated property keeps its first value.
    /// </summary>
    private static Dictionary<string, JsonElement> ReadFields(
        JsonElement element,
        string scope,
        string[] allowed,
        EvalCaseProblems problems)
    {
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var duplicated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            var canonical = allowed.FirstOrDefault(
                field => string.Equals(field, property.Name, StringComparison.OrdinalIgnoreCase));
            if (canonical is null)
            {
                problems.Add(
                    $"has unknown property '{scope}{property.Name}'; allowed properties: " +
                    $"{string.Join(", ", allowed)}.");
            }
            else if (!fields.TryAdd(canonical, property.Value) && duplicated.Add(canonical))
            {
                problems.Add(
                    $"field '{scope}{canonical}' is declared more than once (names compare ignoring case).");
            }
        }

        return fields;
    }

    private static string? RequiredString(
        Dictionary<string, JsonElement> fields,
        string field,
        EvalCaseProblems problems)
    {
        if (fields.ContainsKey(field))
        {
            return OptionalString(fields, field, problems);
        }

        problems.Add($"is missing required field '{field}'.");
        return null;
    }

    private static string? OptionalString(
        Dictionary<string, JsonElement> fields,
        string field,
        EvalCaseProblems problems)
    {
        if (!fields.TryGetValue(field, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString();
        }

        problems.Add($"field '{field}' must be a non-empty string, not {Show(value)}.");
        return null;
    }

    private static void RequireSafeName(string field, string? name, EvalCaseProblems problems)
    {
        if (name is not null && !FileModeLoader.IsSafeName(name))
        {
            problems.Add($"field '{field}' is '{name}', which is not a valid name: {FileModeLoader.NameRule}");
        }
    }

    /// <summary>
    /// Exactly one of <c>input</c> and <c>inputFile</c>, decided by presence so null cannot hide one. Each
    /// declared value is still validated, so a case declaring both also learns what is wrong with either.
    /// </summary>
    private static (string? Input, string? InputFile) ReadInputChoice(
        Dictionary<string, JsonElement> fields,
        EvalCaseProblems problems)
    {
        var hasInput = fields.ContainsKey("input");
        var hasInputFile = fields.ContainsKey("inputFile");
        if (hasInput == hasInputFile)
        {
            problems.Add(
                $"declares {(hasInput ? "both" : "neither")} 'input' {(hasInput ? "and" : "nor")} 'inputFile'; " +
                "declare exactly one.");
        }

        return (OptionalString(fields, "input", problems), OptionalString(fields, "inputFile", problems));
    }

    private static EvalCaseExpectation? ReadExpect(
        Dictionary<string, JsonElement> fields,
        EvalCaseProblems problems)
    {
        if (!fields.TryGetValue("expect", out var expect))
        {
            problems.Add("is missing required field 'expect'.");
            return null;
        }

        if (expect.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"field 'expect' must be an object, not {Show(expect)}.");
            return null;
        }

        var before = problems.Count;
        var expectFields = ReadFields(expect, "expect.", ExpectFields, problems);

        EvalCaseOutcome? evaluation = null;
        if (expectFields.TryGetValue("evaluation", out var declaredEvaluation))
        {
            evaluation = ReadOutcome(declaredEvaluation, "expect.evaluation", problems);
        }
        else
        {
            problems.Add("is missing required field 'expect.evaluation'.");
        }

        var checks = new Dictionary<string, EvalCaseOutcome>(StringComparer.Ordinal);
        if (!expectFields.TryGetValue("checks", out var declaredChecks))
        {
            problems.Add("is missing required field 'expect.checks' (use {} to assert no named check).");
        }
        else if (declaredChecks.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"field 'expect.checks' must be an object, not {Show(declaredChecks)}.");
        }
        else
        {
            ReadChecks(declaredChecks, checks, problems);
        }

        return problems.Count > before ? null : new EvalCaseExpectation(evaluation!.Value, checks);
    }

    private static void ReadChecks(
        JsonElement declaredChecks,
        Dictionary<string, EvalCaseOutcome> checks,
        EvalCaseProblems problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in declaredChecks.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(check.Name))
            {
                problems.Add("field 'expect.checks' contains a check with a blank name.");
                continue;
            }

            if (!seen.Add(check.Name))
            {
                problems.Add($"field 'expect.checks' declares check '{check.Name}' more than once.");
                continue;
            }

            if (ReadOutcome(check.Value, $"expect.checks.{check.Name}", problems) is { } outcome)
            {
                checks[check.Name] = outcome;
            }
        }
    }

    private static EvalCaseOutcome? ReadOutcome(JsonElement value, string field, EvalCaseProblems problems)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        switch (text)
        {
            case "pass":
                return EvalCaseOutcome.Pass;
            case "fail":
                return EvalCaseOutcome.Fail;
            default:
                problems.Add($"field '{field}' must be \"pass\" or \"fail\" (lowercase), not {Show(value)}.");
                return null;
        }
    }

    /// <summary>A short rendering of a declared value for a diagnostic: its kind, or its (truncated) raw text.</summary>
    private static string Show(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                return "an object";
            case JsonValueKind.Array:
                return "an array";
            default:
                var raw = value.GetRawText();
                return raw.Length <= ShownValueLength ? raw : string.Concat(raw.AsSpan(0, ShownValueLength), "...");
        }
    }
}
