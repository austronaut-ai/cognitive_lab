using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Evaluation;
using CognitiveRuntime.Core.Exceptions;

namespace CognitiveRuntime.Tests;

public sealed class EvalCaseLoaderTests
{
    private const string Expect = """
        "expect": { "evaluation": "pass", "checks": { "output contract satisfied": "pass" } }
        """;

    [Fact]
    public async Task LoadAsync_ResolvesInlineAndFileBackedCases()
    {
        using var corpus = new Corpus();
        corpus.Write("frame/notes/input.txt", "Input from a file.\n");
        Directory.CreateDirectory(corpus.PathOf("_fixtures/modes"));
        var inline = corpus.Write(
            "frame/inline.json",
            $$"""
            {
              "schemaVersion": 1,
              "name": "inline",
              "description": "Inline input with defaults.",
              "mode": "frame",
              "input": "Inline input.",
              {{Expect}}
            }
            """);
        var fileBacked = corpus.Write(
            "frame/file-backed.json",
            """
            {
              "schemaVersion": 1,
              "name": "file-backed",
              "description": "Every optional field.",
              "mode": "lens",
              "lens": "warcraft",
              "pattern": "single-pass",
              "inputFile": "notes\\..\\notes/input.txt",
              "modesRoot": "../_fixtures/modes",
              "expect": {
                "evaluation": "fail",
                "checks": { "output contract satisfied": "fail", "model call pairing": "pass" }
              }
            }
            """);

        var first = await corpus.Loader.LoadAsync(inline);
        var second = await corpus.Loader.LoadAsync(fileBacked);

        Assert.Equal(inline, first.SourcePath);
        Assert.Equal("inline", first.Name);
        Assert.Equal("Inline input with defaults.", first.Description);
        Assert.Equal("frame", first.Mode);
        Assert.Null(first.Lens);
        Assert.Equal(new RunRequest("frame", "input", "mock", "out").Pattern, first.Pattern);
        Assert.Equal("Inline input.", first.Input);
        Assert.Null(first.InputFile);
        Assert.Equal(corpus.ModesRoot, first.ModesRoot);
        Assert.Equal(EvalCaseOutcome.Pass, first.Expect.Evaluation);
        Assert.Equal(EvalCaseOutcome.Pass, Assert.Single(first.Expect.Checks).Value);

        Assert.Equal("warcraft", second.Lens);
        Assert.Equal("single-pass", second.Pattern);
        Assert.Equal("Input from a file.\n", second.Input);
        Assert.Equal(corpus.PathOf("frame/notes/input.txt"), second.InputFile);
        Assert.Equal(corpus.PathOf("_fixtures/modes"), second.ModesRoot);
        Assert.Equal(EvalCaseOutcome.Fail, second.Expect.Evaluation);
        Assert.Equal(EvalCaseOutcome.Fail, second.Expect.Checks["output contract satisfied"]);
        Assert.Equal(EvalCaseOutcome.Pass, second.Expect.Checks["model call pairing"]);
    }

    [Fact]
    public async Task LoadAsync_AcceptsCommentsTrailingCommasAndEmptyChecks()
    {
        using var corpus = new Corpus();
        var path = corpus.Write(
            "frame/lenient.json",
            """
            {
              // Cases tolerate the same JSON extras as mode.json.
              "schemaVersion": 1,
              "name": "lenient",
              "description": "Comments and trailing commas.",
              "mode": "frame",
              "input": "Input.",
              "expect": { "evaluation": "pass", "checks": {}, },
            }
            """);

        var evalCase = await corpus.Loader.LoadAsync(path);

        Assert.Empty(evalCase.Expect.Checks);
    }

    [Fact]
    public async Task LoadAsync_ComparesCheckNamesOrdinally()
    {
        using var corpus = new Corpus();
        var path = corpus.WriteCase(
            "frame/x.json",
            """
            "input": "Input.",
            "expect": { "evaluation": "pass", "checks": { "Critic phase ran": "pass", "critic phase ran": "fail" } }
            """);

        var checks = (await corpus.Loader.LoadAsync(path)).Expect.Checks;

        Assert.Equal(2, checks.Count);
        Assert.Equal(EvalCaseOutcome.Fail, checks["critic phase ran"]);
        Assert.False(checks.ContainsKey("CRITIC PHASE RAN"));
    }

    [Fact]
    public async Task LoadDirectoryAsync_LoadsCasesInOrdinalPathOrderAndSkipsFixtures()
    {
        using var corpus = new Corpus();
        // Written out of order. Ordinal order of the '/'-separated relative path puts uppercase first and
        // 'frame/' before 'frame2/' on every OS.
        corpus.WriteCase("frame2/a.json", Inline, name: "a");
        corpus.WriteCase("frame/sub/a.json", Inline, name: "a");
        corpus.WriteCase("frame/b.json", Inline, name: "b");
        corpus.WriteCase("Zeta/a.json", Inline, name: "a");
        corpus.Write("frame/notes.txt", "Not a case.");
        corpus.Write("_fixtures/modes/frame/mode.json", """{ "schemaVersion": 1, "name": "frame" }""");

        var all = await corpus.Loader.LoadDirectoryAsync();
        var frameOnly = await corpus.Loader.LoadDirectoryAsync(corpus.PathOf("frame"));

        Assert.Equal(
            ["Zeta/a.json", "frame/b.json", "frame/sub/a.json", "frame2/a.json"],
            all.Select(evalCase => corpus.Relative(evalCase.SourcePath)));
        Assert.Equal(
            ["frame/b.json", "frame/sub/a.json"],
            frameOnly.Select(evalCase => corpus.Relative(evalCase.SourcePath)));
    }

    [Fact]
    public async Task LoadDirectoryAsync_SkipsLinkedDirectories()
    {
        using var corpus = new Corpus();
        corpus.WriteCase("frame/a.json", Inline, name: "a");
        var outside = Path.Combine(corpus.Workspace.Root, "outside-cases");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "broken.json"), "not json");
        if (!TryCreateDirectoryLink(corpus.PathOf("linked"), outside))
        {
            return;
        }

        var cases = await corpus.Loader.LoadDirectoryAsync();

        Assert.Equal("a", Assert.Single(cases).Name);
    }

    [Fact]
    public async Task LoadDirectoryAsync_ReportsEveryProblemInEveryFile()
    {
        using var corpus = new Corpus();
        var good = corpus.WriteCase("frame/good.json", Inline, name: "good");
        var unknown = corpus.WriteCase(
            "frame/two-problems.json",
            $"""
            "input": "Input.", "provider": "github", "runMode": "real", {Expect}
            """,
            name: "other-name");
        var broken = corpus.Write("lens/broken.json", "{ \"schemaVersion\": 1,");

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(
            () => corpus.Loader.LoadDirectoryAsync());

        Assert.Equal(4, exception.Problems.Count);
        Assert.All(exception.Problems.Take(3), problem => Assert.Contains($"'{unknown}'", problem));
        Assert.Contains(exception.Problems, problem => problem.Contains("unknown property 'provider'"));
        Assert.Contains(exception.Problems, problem => problem.Contains("unknown property 'runMode'"));
        Assert.Contains(exception.Problems, problem => problem.Contains("must equal the file name"));
        Assert.Contains($"'{broken}' is not valid JSON", exception.Problems[3]);
        Assert.DoesNotContain(good, exception.Message);
        Assert.Equal(string.Join(Environment.NewLine, exception.Problems), exception.Message);
    }

    public static TheoryData<string, string> MalformedCases() => new()
    {
        // schemaVersion: one integer token equal to 1, checked before anything else is interpreted.
        { """{ "name": "x", "unknown": true }""", "field 'schemaVersion' is missing" },
        { Case("\"input\": \"Input.\"", schemaVersion: "2"), "field 'schemaVersion' is 2; this runtime supports only" },
        { Case("\"input\": \"Input.\"", schemaVersion: "\"1\""), "field 'schemaVersion' is \"1\"" },
        { Case("\"input\": \"Input.\"", schemaVersion: "1.0"), "field 'schemaVersion' is 1.0" },
        { Case("\"input\": \"Input.\"", schemaVersion: "1e0"), "field 'schemaVersion' is 1e0" },
        { Case("\"input\": \"Input.\"", schemaVersion: "null"), "field 'schemaVersion' is null" },
        { Case("\"input\": \"Input.\", \"SchemaVersion\": 1"), "field 'schemaVersion' is declared 2 times" },

        // Document shape.
        { "{ \"schemaVersion\": 1, ", "is not valid JSON" },
        { "", "is not valid JSON" },
        { "[]", "must be a JSON object, not an array" },
        { "null", "must be a JSON object, not null" },

        // Unknown properties, at both levels. There is deliberately no provider or run-mode field.
        { Case("\"input\": \"Input.\", \"provider\": \"mock\""), "has unknown property 'provider'" },
        { Case("\"input\": \"Input.\", \"runMode\": \"mock\""), "has unknown property 'runMode'" },
        {
            Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": {}, "trace": "pass" }"""),
            "has unknown property 'expect.trace'"
        },
        { Case("\"input\": \"Input.\", \"Mode\": \"frame\""), "field 'mode' is declared more than once" },

        // Required fields and their types; null is a supplied value, not an omission.
        { Case("\"input\": \"Input.\"", name: null), "is missing required field 'name'" },
        { Case("\"input\": \"Input.\"", name: "\"other\""), "field 'name' is 'other' but must equal the file name without .json, 'x'" },
        { Case("\"input\": \"Input.\"", name: "\"X\""), "must equal the file name" },
        { Case("\"input\": \"Input.\"", name: "7"), "field 'name' must be a non-empty string, not 7" },
        { Case("\"input\": \"Input.\"", description: null), "is missing required field 'description'" },
        { Case("\"input\": \"Input.\"", description: "\"  \""), "field 'description' must be a non-empty string" },
        { Case("\"input\": \"Input.\"", mode: null), "is missing required field 'mode'" },
        { Case("\"input\": \"Input.\"", mode: "null"), "field 'mode' must be a non-empty string, not null" },
        { Case("\"input\": \"Input.\"", mode: "\"../frame\""), "field 'mode' is '../frame', which is not a valid name" },
        { Case("\"input\": \"Input.\", \"lens\": null"), "field 'lens' must be a non-empty string, not null" },
        { Case("\"input\": \"Input.\", \"lens\": \"-h\""), "field 'lens' is '-h', which is not a valid name" },
        { Case("\"input\": \"Input.\", \"pattern\": \"\""), "field 'pattern' must be a non-empty string" },
        { Case("\"input\": \"Input.\", \"pattern\": null"), "field 'pattern' must be a non-empty string, not null" },
        { Case("\"input\": \"Input.\", \"modesRoot\": null"), "field 'modesRoot' must be a non-empty string, not null" },

        // Exactly one of input and inputFile, decided by presence.
        { Case("\"input\": \"Input.\", \"inputFile\": \"input.txt\""), "declares both 'input' and 'inputFile'" },
        { Case("\"input\": null, \"inputFile\": \"input.txt\""), "declares both 'input' and 'inputFile'" },
        { Case("\"input\": \"Input.\", \"inputFile\": null"), "declares both 'input' and 'inputFile'" },
        { Case("\"lens\": \"warcraft\""), "declares neither 'input' nor 'inputFile'" },
        { Case("\"input\": null"), "field 'input' must be a non-empty string, not null" },
        { Case("\"input\": \" \\n \""), "field 'input' must be a non-empty string" },
        { Case("\"input\": [\"Input.\"]"), "field 'input' must be a non-empty string, not an array" },
        { Case("\"inputFile\": null"), "field 'inputFile' must be a non-empty string, not null" },

        // Expectations: exact lowercase pass or fail.
        { Case("\"input\": \"Input.\"", expect: null), "is missing required field 'expect'" },
        { Case("\"input\": \"Input.\"", expect: "null"), "field 'expect' must be an object, not null" },
        { Case("\"input\": \"Input.\"", expect: "\"pass\""), "field 'expect' must be an object, not \"pass\"" },
        { Case("\"input\": \"Input.\"", expect: """{ "checks": {} }"""), "is missing required field 'expect.evaluation'" },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass" }"""), "is missing required field 'expect.checks'" },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": null }"""), "field 'expect.checks' must be an object, not null" },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": [] }"""), "field 'expect.checks' must be an object, not an array" },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "passed", "checks": {} }"""), "field 'expect.evaluation' must be \"pass\" or \"fail\" (lowercase), not \"passed\"" },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "PASS", "checks": {} }"""), "field 'expect.evaluation' must be \"pass\" or \"fail\"" },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": true, "checks": {} }"""), "field 'expect.evaluation' must be \"pass\" or \"fail\" (lowercase), not true" },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": null, "checks": {} }"""), "field 'expect.evaluation' must be \"pass\" or \"fail\" (lowercase), not null" },
        {
            Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": { "critic phase ran": "skip" } }"""),
            "field 'expect.checks.critic phase ran' must be \"pass\" or \"fail\" (lowercase), not \"skip\""
        },
        {
            Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": { "critic phase ran": true } }"""),
            "field 'expect.checks.critic phase ran' must be \"pass\" or \"fail\" (lowercase), not true"
        },
        {
            Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": { " ": "pass" } }"""),
            "contains a check with a blank name"
        },
        {
            Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": { "a": "pass", "a": "pass" } }"""),
            "declares check 'a' more than once"
        }
    };

    [Theory]
    [MemberData(nameof(MalformedCases))]
    public async Task LoadAsync_RejectsMalformedCasesWithFilePath(string json, string expectedProblem)
    {
        using var corpus = new Corpus();
        corpus.Write("frame/input.txt", "Input from a file.");
        var path = corpus.Write("frame/x.json", json);

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(() => corpus.Loader.LoadAsync(path));

        Assert.Contains(expectedProblem, exception.Message);
        Assert.All(exception.Problems, problem => Assert.StartsWith($"Eval case '{path}' ", problem));
    }

    [Fact]
    public async Task LoadAsync_ReportsOnlyTheSchemaVersionForAnUnsupportedSchema()
    {
        using var corpus = new Corpus();
        var path = corpus.Write("frame/x.json", """{ "schemaVersion": 2, "title": "A future shape." }""");

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(() => corpus.Loader.LoadAsync(path));

        Assert.Contains("schemaVersion", Assert.Single(exception.Problems));
    }

    public static TheoryData<string, string, string> InvalidReferences() => new()
    {
        // Escapes, including a sibling whose name starts with the root's name.
        { "inputFile", "../../outside.txt", "which is not inside the eval case root" },
        { "inputFile", "..\\..\\outside.txt", "which is not inside the eval case root" },
        { "inputFile", "../../evals-backup/input.txt", "which is not inside the eval case root" },
        { "modesRoot", "../../../modes", "which is not inside the eval case root" },
        { "modesRoot", "../../evals-backup/modes", "which is not inside the eval case root" },
        { "inputFile", "..", "resolves to the eval case root" },

        // Rooted on any platform.
        { "inputFile", "/etc/hostname", "must be a path relative to the case file" },
        { "inputFile", "\\\\server\\share\\input.txt", "must be a path relative to the case file" },
        { "inputFile", "C:\\input.txt", "must be a path relative to the case file" },
        { "inputFile", "C:input.txt", "must be a path relative to the case file" },
        { "modesRoot", "/modes", "must be a path relative to the case file" },
        { "modesRoot", "D:modes", "must be a path relative to the case file" },

        // Missing targets and targets of the wrong kind.
        { "inputFile", "missing.txt", "which was not found; it must be a file" },
        { "inputFile", "../_fixtures/modes", "which is not a file" },
        { "inputFile", "blank.txt", "which is empty or contains only whitespace" },
        { "modesRoot", "../_fixtures/absent", "which was not found; it must be a directory" },
        { "modesRoot", "input.txt", "which is not a directory" }
    };

    [Theory]
    [MemberData(nameof(InvalidReferences))]
    public async Task LoadAsync_RejectsInvalidReferencesWithFilePath(
        string field,
        string reference,
        string expectedProblem)
    {
        using var corpus = new Corpus();
        corpus.Write("frame/input.txt", "Input from a file.");
        corpus.Write("frame/blank.txt", " \n");
        Directory.CreateDirectory(corpus.PathOf("_fixtures/modes"));
        // Real targets outside the root, so only containment can reject them.
        corpus.Write("../outside.txt", "Outside input.");
        corpus.Write("../evals-backup/input.txt", "Sibling input.");
        Directory.CreateDirectory(corpus.PathOf("../evals-backup/modes"));
        var declared = System.Text.Json.JsonSerializer.Serialize(reference);
        var body = field == "inputFile"
            ? $"\"inputFile\": {declared}"
            : $"\"input\": \"Input.\", \"modesRoot\": {declared}";
        var path = corpus.Write("frame/x.json", Case(body));

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(() => corpus.Loader.LoadAsync(path));

        var problem = Assert.Single(exception.Problems);
        Assert.StartsWith($"Eval case '{path}' field '{field}' ('{reference}') ", problem);
        Assert.Contains(expectedProblem, problem);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData("..\\")]
    [InlineData("../frame/..")]
    public async Task LoadAsync_AcceptsModesRootThatIsTheCaseRoot(string reference)
    {
        using var corpus = new Corpus();
        var declared = System.Text.Json.JsonSerializer.Serialize(reference);
        var path = corpus.Write("frame/x.json", Case($"\"input\": \"Input.\", \"modesRoot\": {declared}"));

        var evalCase = await corpus.Loader.LoadAsync(path);

        Assert.Equal(corpus.Root, evalCase.ModesRoot);
    }

    [Fact]
    public async Task LoadAsync_ReportsInvalidReferencesAlongsideSchemaProblems()
    {
        using var corpus = new Corpus();
        var path = corpus.Write(
            "frame/x.json",
            Case(
                "\"inputFile\": \"missing.txt\", \"modesRoot\": \"../../outside\", \"provider\": \"mock\"",
                name: "\"other\""));

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(() => corpus.Loader.LoadAsync(path));

        Assert.Equal(4, exception.Problems.Count);
        Assert.All(exception.Problems, problem => Assert.StartsWith($"Eval case '{path}' ", problem));
        Assert.Contains(exception.Problems, problem => problem.Contains("has unknown property 'provider'"));
        Assert.Contains(exception.Problems, problem => problem.Contains("must equal the file name"));
        Assert.Contains(
            exception.Problems,
            problem => problem.Contains("field 'inputFile' ('missing.txt')") && problem.Contains("was not found"));
        Assert.Contains(
            exception.Problems,
            problem => problem.Contains("field 'modesRoot' ('../../outside')") &&
                problem.Contains("not inside the eval case root"));
    }

    [Fact]
    public async Task LoadAsync_ValidatesTheInputFileOfACaseDeclaringBothInputs()
    {
        using var corpus = new Corpus();
        var path = corpus.Write("frame/x.json", Case("\"input\": \"Input.\", \"inputFile\": \"missing.txt\""));

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(() => corpus.Loader.LoadAsync(path));

        Assert.Equal(2, exception.Problems.Count);
        Assert.Contains("declares both 'input' and 'inputFile'", exception.Problems[0]);
        Assert.Contains("field 'inputFile' ('missing.txt')", exception.Problems[1]);
    }

    public static TheoryData<string> UndecodableCases() => new()
    {
        // Syntactically valid JSON whose escapes are not valid UTF-16: an unpaired surrogate.
        { Case("\"input\": \"Input.\", \"bad\\uD800\": 1") },
        { Case("\"input\": \"Input.\"", description: "\"\\uD800\"") },
        { Case("\"input\": \"Input.\"", name: "\"x\\uDC00\"") },
        { Case("\"input\": \"\\uD800 input\"") },
        { Case("\"inputFile\": \"\\uD800.txt\"") },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "\uD800", "checks": {} }""") },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "checks": { "\uD800": "pass" } }""") },
        { Case("\"input\": \"Input.\"", expect: """{ "evaluation": "pass", "\uD800": {} }""") }
    };

    [Theory]
    [MemberData(nameof(UndecodableCases))]
    public async Task LoadAsync_ReportsUndecodableTextWithFilePath(string json)
    {
        using var corpus = new Corpus();
        var path = corpus.Write("frame/x.json", json);

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(() => corpus.Loader.LoadAsync(path));

        Assert.All(exception.Problems, problem => Assert.StartsWith($"Eval case '{path}' ", problem));
        Assert.Contains(exception.Problems, problem => problem.Contains("cannot be decoded"));
    }

    [Fact]
    public async Task LoadDirectoryAsync_ReportsUndecodableTextAndKeepsLoadingLaterFiles()
    {
        using var corpus = new Corpus();
        var badName = corpus.Write("frame/a.json", Case("\"input\": \"Input.\", \"bad\\uD800\": 1", name: "\"a\""));
        var badValue = corpus.Write(
            "frame/b.json",
            Case("\"input\": \"Input.\"", name: "\"b\"", description: "\"\\uD800\""));
        var later = corpus.WriteCase("frame/c.json", "\"input\": \"Input.\", \"provider\": \"mock\"", name: "c");

        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(
            () => corpus.Loader.LoadDirectoryAsync());

        Assert.Contains(
            exception.Problems,
            problem => problem.StartsWith($"Eval case '{badName}' ") && problem.Contains("cannot be decoded"));
        Assert.Contains(
            exception.Problems,
            problem => problem.StartsWith($"Eval case '{badValue}' ") && problem.Contains("cannot be decoded"));
        Assert.Contains(
            exception.Problems,
            problem => problem.StartsWith($"Eval case '{later}' ") && problem.Contains("unknown property 'provider'"));
    }

    [Fact]
    public async Task LoadAsync_RejectsReferencesThroughLinks()
    {
        using var corpus = new Corpus();
        var outside = Path.Combine(corpus.Workspace.Root, "outside");
        Directory.CreateDirectory(Path.Combine(outside, "modes"));
        File.WriteAllText(Path.Combine(outside, "input.txt"), "Outside input.");
        var inputCase = corpus.Write("frame/x.json", Case("\"inputFile\": \"linked/input.txt\""));
        var modesCase = corpus.Write(
            "lens/x.json",
            Case("\"input\": \"Input.\", \"modesRoot\": \"../frame/linked/modes\""));
        if (!TryCreateDirectoryLink(corpus.PathOf("frame/linked"), outside))
        {
            return;
        }

        var inputFailure = await Assert.ThrowsAsync<EvalCaseLoadException>(
            () => corpus.Loader.LoadAsync(inputCase));
        var modesFailure = await Assert.ThrowsAsync<EvalCaseLoadException>(
            () => corpus.Loader.LoadAsync(modesCase));

        Assert.Contains("field 'inputFile'", inputFailure.Message);
        Assert.Contains("symbolic link or junction", inputFailure.Message);
        Assert.Contains("field 'modesRoot'", modesFailure.Message);
        Assert.Contains("symbolic link or junction", modesFailure.Message);
    }

    [Fact]
    public async Task LoadAsync_RejectsSelectionsThatAreNotCaseFiles()
    {
        using var corpus = new Corpus();
        corpus.Write("frame/notes.txt", "Not a case.");
        corpus.Write("_fixtures/modes/frame/mode.json", Case(Inline, name: "\"mode\""));
        corpus.Write("../outside/x.json", Case(Inline));
        Directory.CreateDirectory(corpus.PathOf("frame/dir.json"));

        await AssertSelectionProblemAsync(corpus, "frame/missing.json", "was not found");
        await AssertSelectionProblemAsync(corpus, "frame/notes.txt", "is not a '.json' file");
        await AssertSelectionProblemAsync(corpus, "frame/dir.json", "is a directory, not a case file");
        await AssertSelectionProblemAsync(corpus, "_fixtures/modes/frame/mode.json", "reserved '_fixtures' directory");
        await AssertSelectionProblemAsync(corpus, "../outside/x.json", "is outside the eval case root");
    }

    [Fact]
    public async Task LoadDirectoryAsync_RejectsDirectoriesThatCannotHoldCases()
    {
        using var corpus = new Corpus();
        Directory.CreateDirectory(corpus.PathOf("_fixtures/modes"));
        Directory.CreateDirectory(corpus.PathOf("../outside"));

        foreach (var (directory, expected) in new[]
                 {
                     ("missing", "was not found"),
                     ("_fixtures", "reserved '_fixtures' directory"),
                     ("_fixtures/modes", "reserved '_fixtures' directory"),
                     ("../outside", "is outside the eval case root"),
                     ("..", "is outside the eval case root")
                 })
        {
            var path = corpus.PathOf(directory);
            var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(
                () => corpus.Loader.LoadDirectoryAsync(path));

            Assert.StartsWith($"Eval case directory '{path}' ", exception.Message);
            Assert.Contains(expected, exception.Message);
        }
    }

    [Fact]
    public async Task Load_PreservesCancellation()
    {
        using var corpus = new Corpus();
        var path = corpus.WriteCase("frame/x.json", Inline);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => corpus.Loader.LoadAsync(path, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => corpus.Loader.LoadDirectoryAsync(cancellationToken: cancellation.Token));
    }

    private static readonly string Inline = $"\"input\": \"Input.\", {Expect}";

    private static async Task AssertSelectionProblemAsync(Corpus corpus, string relativePath, string expected)
    {
        var path = corpus.PathOf(relativePath);
        var exception = await Assert.ThrowsAsync<EvalCaseLoadException>(() => corpus.Loader.LoadAsync(path));

        var problem = Assert.Single(exception.Problems);
        Assert.StartsWith($"Eval case '{path}' ", problem);
        Assert.Contains(expected, problem);
    }

    /// <summary>
    /// Builds case JSON for a file named <c>x.json</c>. Each named argument is the raw JSON value of that
    /// field, or null to leave the field out. <paramref name="body"/> supplies the remaining properties;
    /// when it declares its own <c>expect</c>, the default one is left out.
    /// </summary>
    private static string Case(
        string body,
        string? schemaVersion = "1",
        string? name = "\"x\"",
        string? description = "\"A test case.\"",
        string? mode = "\"frame\"",
        string? expect = """{ "evaluation": "pass", "checks": {} }""")
    {
        var properties = new List<string>();
        void Add(string field, string? value)
        {
            if (value is not null)
            {
                properties.Add($"\"{field}\": {value}");
            }
        }

        Add("schemaVersion", schemaVersion);
        Add("name", name);
        Add("description", description);
        Add("mode", mode);
        properties.Add(body);
        if (!body.Contains("\"expect\"", StringComparison.Ordinal))
        {
            Add("expect", expect);
        }

        return "{ " + string.Join(", ", properties) + " }";
    }

    private static bool TryCreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    /// <summary>A temporary <c>examples/evals</c> root with a sibling default modes directory.</summary>
    private sealed class Corpus : IDisposable
    {
        public Corpus()
        {
            Root = Path.Combine(Workspace.Root, "examples", "evals");
            Directory.CreateDirectory(Root);
            Loader = new EvalCaseLoader(Root, Workspace.ModesRoot);
        }

        public TestWorkspace Workspace { get; } = new();

        public string Root { get; }

        public string ModesRoot => Workspace.ModesRoot;

        public EvalCaseLoader Loader { get; }

        public string PathOf(string relativePath) => Path.GetFullPath(Path.Combine(Root, relativePath));

        public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');

        public string Write(string relativePath, string content)
        {
            var path = PathOf(relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>Writes a valid case named after its file, with <paramref name="body"/> as its remaining fields.</summary>
        public string WriteCase(string relativePath, string body, string name = "x") =>
            Write(relativePath, Case(body, name: $"\"{name}\""));

        public void Dispose() => Workspace.Dispose();
    }
}
