using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Exceptions;
using CognitiveRuntime.Core.Runtime;

namespace CognitiveRuntime.Core.Evaluation;

/// <summary>
/// Turns the exception that stopped one eval case into its error message. The message must be the same on
/// every run of the same case, so it carries no generated run ID and no generated output path; the run
/// directory is reported separately on the case result.
/// </summary>
internal static class EvalSuiteDiagnostics
{
    private const string RunPrefix = "Run '";
    private const string RunPrefixEnd = "' failed: ";

    public static string Describe(Exception exception)
    {
        // The orchestrator wraps a failed run; the cause is what classifies it.
        var cause = exception is RuntimeRunException { InnerException: { } inner } ? inner : exception;
        if (cause is IOException or UnauthorizedAccessException)
        {
            // File-system messages name the path that failed, which is usually inside the generated run
            // directory. The type alone is stable; the detail is in the run's own artifacts.
            return $"The run failed with a file-system error ({cause.GetType().Name}).";
        }

        return exception is RuntimeRunException
            ? RemoveRunPrefix(exception.Message)
            : RuntimeFailureFactory.Create(exception, RuntimeFailureCategory.Runtime).SafeMessage;
    }

    /// <summary>Drops the orchestrator's <c>Run '&lt;id&gt;' failed: </c> prefix, keeping its sanitized message.</summary>
    private static string RemoveRunPrefix(string message)
    {
        if (!message.StartsWith(RunPrefix, StringComparison.Ordinal))
        {
            return message;
        }

        // A run ID holds only letters, digits, and hyphens, so the first match ends the prefix.
        var end = message.IndexOf(RunPrefixEnd, RunPrefix.Length, StringComparison.Ordinal);
        return end < 0 ? message : message[(end + RunPrefixEnd.Length)..];
    }
}
