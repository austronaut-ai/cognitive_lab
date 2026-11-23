using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Exceptions;
using CognitiveRuntime.Core.Runtime;

namespace CognitiveRuntime.Core.Evaluation;

/// <summary>
/// Runs eval cases through the normal orchestrator on the mock provider and compares each case's
/// expectations with the run's structured evaluation. Cases run one at a time in a fixed order, and a case
/// that cannot run becomes that case's Error result instead of stopping the suite.
/// </summary>
public sealed class EvalSuiteRunner
{
    /// <summary>Cases carry no provider: they always run on the mock provider.</summary>
    private const string Provider = "mock";

    private readonly Func<string, Orchestrator> _createOrchestrator;
    private readonly string _outputRoot;

    /// <param name="createOrchestrator">Creates the normal orchestrator for a modes root. The host composes it.</param>
    /// <param name="outputRoot">
    /// The suite output root. Each case's runs are written under <c>&lt;outputRoot&gt;/&lt;mode&gt;/&lt;case name&gt;</c>.
    /// </param>
    public EvalSuiteRunner(Func<string, Orchestrator> createOrchestrator, string outputRoot)
    {
        ArgumentNullException.ThrowIfNull(createOrchestrator);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);
        _createOrchestrator = createOrchestrator;
        _outputRoot = Path.GetFullPath(outputRoot);
    }

    /// <summary>
    /// Runs every case, in ordinal order of its <see cref="EvalCase.SourcePath"/> written with <c>/</c> (the order
    /// <see cref="EvalCaseLoader"/> loads a directory in, the same on every OS), whatever order
    /// <paramref name="cases"/> is in.
    /// Only cancellation of <paramref name="cancellationToken"/> stops the suite; any other failure of a
    /// case is reported as that case's Error result.
    /// </summary>
    public async Task<EvalSuiteResult> RunAsync(
        IReadOnlyList<EvalCase> cases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        cancellationToken.ThrowIfCancellationRequested();

        var results = new List<EvalCaseResult>(cases.Count);
        foreach (var evalCase in cases.OrderBy(OrderKey, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await RunCaseAsync(evalCase, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new EvalSuiteResult(results);
    }

    /// <summary>
    /// The source path written with <c>/</c>: Windows and Linux then run a corpus in the same order, the one the
    /// loader uses. Case is kept, so the order is still ordinal.
    /// </summary>
    private static string OrderKey(EvalCase evalCase)
    {
        ArgumentNullException.ThrowIfNull(evalCase);
        return evalCase.SourcePath.Replace('\\', '/');
    }

    private async Task<EvalCaseResult> RunCaseAsync(EvalCase evalCase, CancellationToken cancellationToken)
    {
        RunResult run;
        try
        {
            var caseOutputRoot = Path.Combine(
                _outputRoot,
                RequireSafeComponent("mode", evalCase.Mode),
                RequireSafeComponent("name", evalCase.Name));
            run = await _createOrchestrator(evalCase.ModesRoot).RunAsync(
                new RunRequest(
                    evalCase.Mode,
                    evalCase.Input,
                    Provider,
                    caseOutputRoot,
                    InputSource: evalCase.InputFile,
                    Lens: evalCase.Lens,
                    Pattern: evalCase.Pattern),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Error(
                evalCase,
                EvalSuiteDiagnostics.Describe(exception),
                (exception as RuntimeRunException)?.OutputDirectory);
        }

        return run.Evaluation is { } evaluation
            ? Compare(evalCase, evaluation, run.OutputDirectory)
            : Error(evalCase, "The run completed without a structured evaluation.", run.OutputDirectory);
    }

    private static EvalCaseResult Compare(EvalCase evalCase, EvalReport evaluation, string outputDirectory)
    {
        var actual = ToOutcome(evaluation.Passed);
        var checks = CompareChecks(evalCase, name => FindCheckOutcome(evaluation, name));
        var passed = actual == evalCase.Expect.Evaluation && checks.All(check => check.Matches);
        return new EvalCaseResult(
            evalCase,
            passed ? EvalCaseStatus.Passed : EvalCaseStatus.Failed,
            actual,
            checks,
            ErrorMessage: null,
            outputDirectory);
    }

    private static EvalCaseResult Error(EvalCase evalCase, string message, string? outputDirectory) =>
        new(
            evalCase,
            EvalCaseStatus.Error,
            ActualEvaluation: null,
            CompareChecks(evalCase, _ => null),
            message,
            outputDirectory);

    /// <summary>One comparison per asserted check, in ordinal name order rather than dictionary order.</summary>
    private static EvalCheckComparison[] CompareChecks(
        EvalCase evalCase,
        Func<string, EvalCaseOutcome?> findActual) =>
        evalCase.Expect.Checks
            .OrderBy(check => check.Key, StringComparer.Ordinal)
            .Select(check => new EvalCheckComparison(check.Key, check.Value, findActual(check.Key)))
            .ToArray();

    /// <summary>
    /// The outcome of the check named exactly <paramref name="name"/> (ordinal), or null when the evaluation
    /// has none. Checks that share the name pass only when all of them pass.
    /// </summary>
    private static EvalCaseOutcome? FindCheckOutcome(EvalReport evaluation, string name)
    {
        var named = evaluation.Checks
            .Where(check => string.Equals(check.Name, name, StringComparison.Ordinal))
            .ToArray();
        return named.Length == 0 ? null : ToOutcome(named.All(check => check.Passed));
    }

    private static EvalCaseOutcome ToOutcome(bool passed) => passed ? EvalCaseOutcome.Pass : EvalCaseOutcome.Fail;

    private static string RequireSafeComponent(string field, string? value) =>
        EvalCasePaths.IsSafeComponent(value)
            ? value!
            : throw new ArgumentException(
                $"Case {field} '{value}' cannot be used as an output directory name: it must be a single " +
                "path component with no separators, traversal, or reserved names.");
}
