namespace CognitiveRuntime.Core.Evaluation;

/// <summary>
/// Path rules for the eval case corpus: everything a case names must stay inside the corpus root and must
/// not pass through a link below it. Files must be strictly below the root; a directory reference may be the
/// root itself. The configured root is the trusted base and is not checked.
/// </summary>
internal static class EvalCasePaths
{
    /// <summary>
    /// True when <paramref name="path"/> is strictly below <paramref name="root"/>. Both must be full paths.
    /// Ordinal on every OS, as in the mode loader: a case-only sibling is a different directory on a
    /// case-sensitive volume, and the trailing separator keeps <c>evals-backup</c> out of <c>evals</c>.
    /// </summary>
    public static bool IsBelow(string root, string path)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(root);
        var prefix = Path.EndsInDirectorySeparator(trimmed) ? trimmed : trimmed + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.Ordinal) && path.Length > prefix.Length;
    }

    /// <summary>
    /// Rooted on any platform: a leading separator (including UNC), a drive letter with or without a
    /// separator (<c>C:\x</c>, <c>C:x</c>), or whatever the current OS calls rooted. Rejected everywhere so
    /// a case means the same thing on every machine.
    /// </summary>
    public static bool IsRooted(string reference) =>
        reference.StartsWith('/') ||
        reference.StartsWith('\\') ||
        (reference.Length >= 2 && char.IsAsciiLetter(reference[0]) && reference[1] == ':') ||
        Path.IsPathRooted(reference);

    /// <summary>
    /// True when <paramref name="value"/> can name one directory on any OS: not empty, rooted, <c>.</c> or
    /// <c>..</c>; no separator, control character, or character Windows forbids in a file name; no trailing
    /// dot or space; and not a Windows device name such as <c>CON</c> or <c>COM1.txt</c>. Windows rules are
    /// applied everywhere so a case means the same thing on every machine.
    /// </summary>
    public static bool IsSafeComponent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || IsRooted(value))
        {
            return false;
        }

        if (value.Any(character => char.IsControl(character) || ForbiddenNameCharacters.Contains(character)) ||
            value[^1] is '.' or ' ')
        {
            return false;
        }

        // Windows resolves a device name whatever its extension or trailing spaces: "NUL.txt" is still NUL.
        var stem = value.Split('.')[0].TrimEnd(' ');
        return !WindowsDeviceNames.Contains(stem);
    }

    private const string ForbiddenNameCharacters = "/\\:*?\"<>|";

    private static readonly HashSet<string> WindowsDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "LPT¹", "LPT²", "LPT³"
    };

    /// <summary>
    /// Resolves a case-relative reference to a full path inside <paramref name="root"/>. Either separator
    /// style is accepted, and <c>..</c> is allowed when the normalized target stays inside the root. The
    /// target must be strictly below the root unless <paramref name="allowRoot"/> is set, which also accepts
    /// the root itself (a directory reference such as a modes root). Returns null and sets
    /// <paramref name="problem"/> when the reference is rooted, malformed, outside the root, or behind a link.
    /// </summary>
    public static string? Resolve(
        string root,
        string caseDirectory,
        string reference,
        bool allowRoot,
        out string? problem)
    {
        if (IsRooted(reference))
        {
            problem = "must be a path relative to the case file, not a rooted path";
            return null;
        }

        string target;
        try
        {
            target = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(Path.Combine(caseDirectory, reference.Replace('\\', '/'))));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problem = $"is not a valid path: {exception.Message}";
            return null;
        }

        if (string.Equals(target, root, StringComparison.Ordinal))
        {
            // The root is the trusted base: nothing below it was traversed, so there is no link to look for.
            problem = allowRoot ? null : $"resolves to the eval case root '{root}' itself, not to a path inside it";
            return allowRoot ? target : null;
        }

        if (!IsBelow(root, target))
        {
            problem = $"resolves to '{target}', which is not inside the eval case root '{root}'";
            return null;
        }

        if (FindLink(root, target) is { } link)
        {
            problem = $"passes through a symbolic link or junction at '{link}'; links are not allowed";
            return null;
        }

        problem = null;
        return target;
    }

    /// <summary>
    /// The first symbolic link, junction, or other reparse point on the way from just below
    /// <paramref name="root"/> down to <paramref name="path"/> (inclusive), or null. A missing or unreadable
    /// component is not a link; the caller's existence check reports it.
    /// </summary>
    public static string? FindLink(string root, string path)
    {
        var current = root;
        foreach (var segment in Path.GetRelativePath(root, path)
                     .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (IsLink(current))
            {
                return current;
            }
        }

        return null;
    }

    public static bool IsLink(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
