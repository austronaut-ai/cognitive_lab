namespace CognitiveRuntime.Core.Contracts;

/// <summary>How one eval case ended in a suite run.</summary>
public enum EvalCaseStatus
{
    /// <summary>The run completed and its evaluation and every asserted check matched the case.</summary>
    Passed,

    /// <summary>The run completed but its evaluation or an asserted check did not match the case.</summary>
    Failed,

    /// <summary>The case could not be run to a completed evaluation.</summary>
    Error
}

/// <summary>
/// One asserted check compared with the run's evaluation. <paramref name="Actual"/> is null when the
/// evaluation has no check of that name (or the case errored), which is a mismatch.
/// </summary>
public sealed record EvalCheckComparison(string Name, EvalCaseOutcome Expected, EvalCaseOutcome? Actual)
{
    public bool Matches => Actual == Expected;
}

/// <summary>
/// The result of one eval case. Carries no timestamp, duration, or run ID, so two runs of the same case
/// differ only in <paramref name="OutputDirectory"/>.
/// </summary>
/// <param name="Case">The case that was run.</param>
/// <param name="Status">Passed, Failed, or Error.</param>
/// <param name="ActualEvaluation">The run's overall evaluation; null when the case errored.</param>
/// <param name="Checks">One comparison per asserted check, in ordinal order of the check name.</param>
/// <param name="ErrorMessage">Why the case errored; null unless <paramref name="Status"/> is Error.</param>
/// <param name="OutputDirectory">The run's output directory; null when the case errored before one existed.</param>
public sealed record EvalCaseResult(
    EvalCase Case,
    EvalCaseStatus Status,
    EvalCaseOutcome? ActualEvaluation,
    IReadOnlyList<EvalCheckComparison> Checks,
    string? ErrorMessage,
    string? OutputDirectory);

/// <summary>The results of a suite run: one per case, in the order the cases were run.</summary>
public sealed record EvalSuiteResult(IReadOnlyList<EvalCaseResult> Cases)
{
    public int Passed => Count(EvalCaseStatus.Passed);

    public int Failed => Count(EvalCaseStatus.Failed);

    public int Errored => Count(EvalCaseStatus.Error);

    private int Count(EvalCaseStatus status) => Cases.Count(result => result.Status == status);
}
