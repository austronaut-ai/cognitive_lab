namespace CognitiveRuntime.Core.Exceptions;

public sealed class ModeLoadException : Exception
{
    public ModeLoadException(string message)
        : base(message)
    {
    }

    public ModeLoadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal ModeLoadException(string message, IReadOnlyList<string> requiredLenses)
        : base(message)
    {
        RequiredLenses = requiredLenses;
    }

    /// <summary>
    /// Set only when the mode cannot load without a lens: the lens names it
    /// offers, in loader order. Lets a catalog list them without parsing the message.
    /// </summary>
    internal IReadOnlyList<string>? RequiredLenses { get; }
}

public sealed class ModelProviderException : Exception
{
    public ModelProviderException(string message)
        : base(message)
    {
    }

    public ModelProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException(string budgetKind, long limit, long observed)
        : base(
            $"Execution budget '{budgetKind}' exceeded: limit {limit}, " +
            $"observed {observed}.")
    {
        BudgetKind = budgetKind;
        Limit = limit;
        Observed = observed;
    }

    public string BudgetKind { get; }

    public long Limit { get; }

    public long Observed { get; }
}

public sealed class RuntimeRunException : Exception
{
    public RuntimeRunException(string message, string outputDirectory, Exception innerException)
        : base(message, innerException)
    {
        OutputDirectory = outputDirectory;
    }

    public string OutputDirectory { get; }
}
