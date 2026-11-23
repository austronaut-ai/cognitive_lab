namespace CognitiveRuntime.Core.Exceptions;

/// <summary>
/// One or more eval case files could not be loaded. Every problem names the file it belongs to; the message
/// is the problems joined one per line.
/// </summary>
public sealed class EvalCaseLoadException : Exception
{
    public EvalCaseLoadException(IReadOnlyList<string> problems)
        : base(string.Join(Environment.NewLine, problems))
    {
        Problems = problems;
    }

    public EvalCaseLoadException(string problem, Exception innerException)
        : base(problem, innerException)
    {
        Problems = [problem];
    }

    /// <summary>Every problem found, in file order and then in the order the loader checks the rules.</summary>
    public IReadOnlyList<string> Problems { get; }
}
