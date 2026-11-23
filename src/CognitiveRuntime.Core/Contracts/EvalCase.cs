namespace CognitiveRuntime.Core.Contracts;

/// <summary>The outcome an eval case expects: of the whole evaluation, or of one named check.</summary>
public enum EvalCaseOutcome
{
    Pass,
    Fail
}

/// <summary>
/// What an eval case asserts about its run's <c>eval_report.md</c>: the overall result and the checks it
/// names. A check the case does not list is not asserted. Check names compare ordinally and are written as
/// they appear in the report (for example <c>output contract satisfied</c>).
/// </summary>
public sealed record EvalCaseExpectation(
    EvalCaseOutcome Evaluation,
    IReadOnlyDictionary<string, EvalCaseOutcome> Checks);

/// <summary>
/// One validated, fully resolved offline eval case. Every path is absolute, the input text is already read,
/// and defaults are applied, so a consumer never re-reads the case file. A case carries no provider: cases
/// always run on the mock provider, which is what keeps them network-free.
/// </summary>
/// <param name="SourcePath">The case file this case was loaded from.</param>
/// <param name="Name">The case name; equals the file name without <c>.json</c>.</param>
/// <param name="Description">What the case covers, for a human reader.</param>
/// <param name="Mode">The mode to run.</param>
/// <param name="Lens">The lens to run the mode with, when the case names one.</param>
/// <param name="Pattern">The orchestration pattern; the CLI default when the case names none.</param>
/// <param name="Input">The run input text, inline or read from <paramref name="InputFile"/>.</param>
/// <param name="InputFile">The file the input was read from, or null for inline input.</param>
/// <param name="ModesRoot">The modes directory to load <paramref name="Mode"/> from.</param>
/// <param name="Expect">The expected evaluation outcome and named checks.</param>
public sealed record EvalCase(
    string SourcePath,
    string Name,
    string Description,
    string Mode,
    string? Lens,
    string Pattern,
    string Input,
    string? InputFile,
    string ModesRoot,
    EvalCaseExpectation Expect);
