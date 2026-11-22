namespace CognitiveRuntime.Cli;

internal enum ModesCommand
{
    /// <summary>Only used for <c>modes --help</c>: show every subcommand.</summary>
    Group,
    Validate,
    List
}

/// <summary>
/// Arguments for <c>modes validate &lt;mode&gt; [--lens &lt;name&gt;] [--modes-root &lt;path&gt;]</c>
/// and <c>modes list [--modes-root &lt;path&gt;]</c>.
/// Kept separate from <see cref="CliOptions"/> because these commands have no
/// input file, provider, or output root.
/// </summary>
internal sealed record ModeValidationArguments(
    string? ModeName,
    string? ModesRoot,
    bool ShowHelp,
    string? Lens = null,
    ModesCommand Command = ModesCommand.Validate)
{
    public const string Usage =
        "Usage: modes validate <mode> [--lens <name>] [--modes-root <path>]";

    public const string ListUsage =
        "Usage: modes list [--modes-root <path>]";

    public static bool IsModesCommand(IReadOnlyList<string> args) =>
        args.Count > 0 &&
        string.Equals(args[0], "modes", StringComparison.OrdinalIgnoreCase);

    public static ModeValidationArguments Parse(IReadOnlyList<string> args)
    {
        var rest = args.Skip(1).ToList();

        if (rest.Count == 1 && IsHelpFlag(rest[0]))
        {
            return Help(ModesCommand.Group);
        }

        if (rest.Count == 2 && IsHelpFlag(rest[1]))
        {
            if (IsValidate(rest[0]))
            {
                return Help(ModesCommand.Validate);
            }

            if (IsList(rest[0]))
            {
                return Help(ModesCommand.List);
            }
        }

        if (rest.Count == 0)
        {
            throw new CliUsageException(
                "The 'modes' command requires a subcommand: validate or list.");
        }

        if (IsList(rest[0]))
        {
            return ParseList(rest);
        }

        if (!IsValidate(rest[0]))
        {
            throw new CliUsageException(
                $"Unknown 'modes' subcommand '{rest[0]}'. Available subcommands: validate, list.");
        }

        return ParseValidate(rest);
    }

    private static ModeValidationArguments Help(ModesCommand command) =>
        new(null, null, ShowHelp: true, Command: command);

    private static ModeValidationArguments ParseList(List<string> rest)
    {
        string? modesRoot = null;

        for (var index = 1; index < rest.Count; index++)
        {
            var argument = rest[index];

            if (IsModesRootOption(argument))
            {
                modesRoot = ReadModesRoot(rest, ref index, modesRoot);
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                throw new CliUsageException(
                    $"Option '{argument}' is not supported by 'modes list'. " +
                    "Only '--modes-root' is accepted.");
            }

            throw new CliUsageException(
                $"Unexpected argument '{argument}'. 'modes list' takes no positional arguments.");
        }

        return new ModeValidationArguments(
            null, modesRoot, ShowHelp: false, Command: ModesCommand.List);
    }

    private static ModeValidationArguments ParseValidate(List<string> rest)
    {
        string? modeName = null;
        string? modesRoot = null;
        string? lens = null;

        for (var index = 1; index < rest.Count; index++)
        {
            var argument = rest[index];

            if (IsModesRootOption(argument))
            {
                modesRoot = ReadModesRoot(rest, ref index, modesRoot);
                continue;
            }

            if (string.Equals(argument, "--lens", StringComparison.OrdinalIgnoreCase))
            {
                if (lens is not null)
                {
                    throw new CliUsageException(
                        "Option '--lens' may be given only once.");
                }

                // Lens names start with a letter or digit, so anything dash-led is an option, not a value.
                if (index + 1 >= rest.Count ||
                    rest[index + 1].StartsWith("-", StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(rest[index + 1]))
                {
                    throw new CliUsageException(
                        "Option '--lens' requires a lens name.");
                }

                lens = rest[++index];
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                throw new CliUsageException(
                    $"Option '{argument}' is not supported by 'modes validate'. " +
                    "Only '--lens' and '--modes-root' are accepted.");
            }

            if (modeName is not null)
            {
                throw new CliUsageException(
                    $"Unexpected extra argument '{argument}'. " +
                    "'modes validate' takes exactly one mode name.");
            }

            if (string.IsNullOrWhiteSpace(argument))
            {
                throw new CliUsageException("The mode name must not be blank.");
            }

            modeName = argument;
        }

        if (modeName is null)
        {
            throw new CliUsageException(
                "'modes validate' requires a mode name.");
        }

        return new ModeValidationArguments(modeName, modesRoot, ShowHelp: false, lens);
    }

    /// <summary>Reads the value after <c>--modes-root</c>; shared by both subcommands.</summary>
    private static string ReadModesRoot(List<string> rest, ref int index, string? current)
    {
        if (current is not null)
        {
            throw new CliUsageException(
                "Option '--modes-root' may be given only once.");
        }

        if (index + 1 >= rest.Count ||
            rest[index + 1].StartsWith("-", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(rest[index + 1]))
        {
            throw new CliUsageException(
                "Option '--modes-root' requires a path value.");
        }

        return rest[++index];
    }

    private static bool IsModesRootOption(string value) =>
        string.Equals(value, "--modes-root", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidate(string value) =>
        string.Equals(value, "validate", StringComparison.OrdinalIgnoreCase);

    private static bool IsList(string value) =>
        string.Equals(value, "list", StringComparison.OrdinalIgnoreCase);

    private static bool IsHelpFlag(string value) =>
        string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase);
}
