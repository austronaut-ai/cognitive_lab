using CognitiveRuntime.Core.Abstractions;
using CognitiveRuntime.Core.Artifacts;
using CognitiveRuntime.Core.Contracts;
using CognitiveRuntime.Core.Evaluation;
using CognitiveRuntime.Core.Exceptions;
using CognitiveRuntime.Core.Experiments.Dungeons;
using CognitiveRuntime.Core.Models;
using CognitiveRuntime.Core.Modes;
using CognitiveRuntime.Core.Persistence;
using CognitiveRuntime.Core.Runtime;
using CognitiveRuntime.Core.Runtime.Orchestration;
using CognitiveRuntime.Core.Tools;
using CognitiveRuntime.Core.Tracing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CognitiveRuntime.Cli;

internal sealed class CliApplication
{
    private readonly Orchestrator _orchestrator;
    private readonly DungeonExperimentRunner _dungeonExperimentRunner;
    private readonly ILogger<CliApplication> _logger;

    public CliApplication(
        Orchestrator orchestrator,
        DungeonExperimentRunner dungeonExperimentRunner,
        ILogger<CliApplication> logger)
    {
        _orchestrator = orchestrator;
        _dungeonExperimentRunner = dungeonExperimentRunner;
        _logger = logger;
    }

    public static Task<int> RunAsync(
        string[] args,
        CancellationToken cancellationToken) =>
        RunAsync(args, Console.Out, Console.Error, cancellationToken);

    /// <summary>
    /// Writer-based entry point. <paramref name="output"/> and
    /// <paramref name="error"/> apply to the <c>modes validate</c> command;
    /// run and experiment commands keep writing to the console.
    /// </summary>
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (ModeValidationArguments.IsModesCommand(args))
        {
            return await RunModesAsync(
                args,
                output,
                error,
                cancellationToken);
        }

        var configuration = new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

        CliOptions options;
        try
        {
            options = CliOptions.Parse(args, configuration);
        }
        catch (CliUsageException exception)
        {
            error.WriteLine(exception.Message);
            error.WriteLine();
            WriteHelp(error);
            return ExitCodes.UsageError;
        }

        if (options.ShowHelp)
        {
            WriteHelp(output);
            return ExitCodes.Success;
        }

        if (options.Experiment is null && !File.Exists(options.InputPath))
        {
            Console.Error.WriteLine(
                $"Input file '{options.InputPath}' does not exist.");
            return ExitCodes.UsageError;
        }

        var services = BuildServices(options, configuration);
        await using var serviceProvider = services.BuildServiceProvider();
        var application = serviceProvider.GetRequiredService<CliApplication>();

        try
        {
            if (options.Experiment is not null)
            {
                return await application.ExecuteExperimentAsync(
                    options,
                    cancellationToken);
            }

            var input = await File.ReadAllTextAsync(
                options.InputPath,
                cancellationToken);
            return await application.ExecuteAsync(
                options,
                input,
                cancellationToken);
        }
        catch (RuntimeRunException exception)
        {
            Console.Error.WriteLine(exception.Message);
            Console.Error.WriteLine(
                $"Partial artifacts: {exception.OutputDirectory}");
            return ExitCodes.FromOutcome(RunOutcome.RuntimeFailed);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Run cancelled.");
            return ExitCodes.FromOutcome(RunOutcome.Cancelled);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Fatal error: {exception.Message}");
            return ExitCodes.FromOutcome(RunOutcome.RuntimeFailed);
        }
    }

    private static async Task<int> RunModesAsync(
        string[] args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ModeValidationArguments parsed;
        try
        {
            parsed = ModeValidationArguments.Parse(args);
        }
        catch (CliUsageException exception)
        {
            return ReportModesUsage(error, exception.Message, UsageFor(args));
        }

        if (parsed.ShowHelp)
        {
            WriteModesHelp(output, parsed.Command);
            return ExitCodes.Success;
        }

        if (!TryResolveModesRoot(parsed.ModesRoot, out var modesRoot, out var rootProblem))
        {
            return ReportModesUsage(error, rootProblem, UsageFor(args));
        }

        return parsed.Command == ModesCommand.List
            ? await ListModesAsync(modesRoot, output, error, cancellationToken)
            : await ValidateModeAsync(parsed, modesRoot, output, error, cancellationToken);
    }

    private static string[] UsageFor(string[] args)
    {
        if (args.Length > 1 &&
            string.Equals(args[1], "list", StringComparison.OrdinalIgnoreCase))
        {
            return [ModeValidationArguments.ListUsage];
        }

        if (args.Length > 1 &&
            string.Equals(args[1], "validate", StringComparison.OrdinalIgnoreCase))
        {
            return [ModeValidationArguments.Usage];
        }

        return [ModeValidationArguments.Usage, ModeValidationArguments.ListUsage];
    }

    private static bool TryResolveModesRoot(
        string? requested,
        out string modesRoot,
        out string problem)
    {
        try
        {
            modesRoot = Path.GetFullPath(
                requested ?? Path.Combine(Environment.CurrentDirectory, "modes"));
            problem = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            modesRoot = string.Empty;
            problem = $"Invalid --modes-root '{requested}': {exception.Message}";
            return false;
        }
    }

    private static async Task<int> ListModesAsync(
        string modesRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ModeCatalog catalog;
        try
        {
            catalog = await new FileModeLoader(modesRoot).ListAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine("Listing of modes cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(
                $"Modes root '{modesRoot}' could not be read: {exception.Message}");
            return ExitCodes.RuntimeFailed;
        }

        WriteCatalog(output, modesRoot, catalog);
        foreach (var diagnostic in catalog.Diagnostics)
        {
            error.WriteLine(DescribeDiagnostic(diagnostic));
        }

        return catalog.Diagnostics.Count == 0
            ? ExitCodes.Success
            : ExitCodes.RuntimeFailed;
    }

    private static string DescribeDiagnostic(ModeCatalogDiagnostic diagnostic) =>
        diagnostic.Lens is null
            ? $"Mode '{diagnostic.Directory}' is invalid: {diagnostic.Reason}"
            : $"Mode '{diagnostic.Directory}' with lens '{diagnostic.Lens}' is invalid: " +
              diagnostic.Reason;

    private static void WriteCatalog(TextWriter output, string modesRoot, ModeCatalog catalog)
    {
        if (catalog.Entries.Count == 0)
        {
            output.WriteLine($"No valid modes found under '{modesRoot}'.");
            return;
        }

        foreach (var entry in catalog.Entries)
        {
            output.WriteLine($"Mode: {entry.Name}");
            output.WriteLine($"  Version: {entry.Version}");
            output.WriteLine($"  Description: {entry.Description}");
            output.WriteLine(
                "  Phases: " + string.Join(
                    ", ",
                    entry.Phases.Select(phase =>
                        $"{phase.Name} ({phase.Kind.ToString().ToLowerInvariant()})")));
            if (entry.Lenses.Count > 0)
            {
                output.WriteLine($"  Lenses: {string.Join(", ", entry.Lenses)}");
            }
        }
    }

    private static async Task<int> ValidateModeAsync(
        ModeValidationArguments parsed,
        string modesRoot,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var modeName = parsed.ModeName!;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await new FileModeLoader(modesRoot).LoadAsync(
                modeName,
                parsed.Lens,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            error.WriteLine($"Validation of mode '{modeName}' cancelled.");
            return ExitCodes.Cancelled;
        }
        catch (ModeLoadException exception)
        {
            error.WriteLine($"Mode '{modeName}' is invalid: {exception.Message}");
            return ExitCodes.RuntimeFailed;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            error.WriteLine(
                $"Mode '{modeName}' could not be read under '{modesRoot}': " +
                exception.Message);
            return ExitCodes.RuntimeFailed;
        }

        output.WriteLine(parsed.Lens is null
            ? $"Mode '{modeName}' is valid."
            : $"Mode '{modeName}' with lens '{parsed.Lens}' is valid.");
        return ExitCodes.Success;
    }

    private static int ReportModesUsage(TextWriter error, string message, string[] usage)
    {
        error.WriteLine(message);
        foreach (var line in usage)
        {
            error.WriteLine(line);
        }

        return ExitCodes.UsageError;
    }

    private static void WriteModesHelp(TextWriter writer, ModesCommand command)
    {
        if (command != ModesCommand.List)
        {
            WriteValidationHelp(writer);
        }

        if (command == ModesCommand.Group)
        {
            writer.WriteLine();
        }

        if (command != ModesCommand.Validate)
        {
            WriteListHelp(writer);
        }
    }

    private static void WriteListHelp(TextWriter writer)
    {
        writer.WriteLine(ModeValidationArguments.ListUsage);
        writer.WriteLine();
        writer.WriteLine(
            "Lists every valid mode under the modes root with its version, description, " +
            "and phases in declared order. A mode whose prompts live under lens variants is " +
            "listed with the lenses that load. Loading uses the same loader as validate. " +
            "No model call is made and no run output is written.");
        writer.WriteLine(
            "Valid modes go to stdout. Invalid directories, with the reason, go to stderr " +
            "and never hide the valid modes.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --modes-root <path>   Defaults to ./modes.");
        writer.WriteLine();
        writer.WriteLine("Exit codes:");
        writer.WriteLine("  0    Every directory is a valid mode (an empty root is fine).");
        writer.WriteLine(
            "  1    Some directories or lenses are invalid or unreadable (the valid modes are " +
            "still listed), or the modes root could not be read.");
        writer.WriteLine("  2    Usage error.");
        writer.WriteLine("  130  Cancelled.");
    }

    private static void WriteValidationHelp(TextWriter writer)
    {
        writer.WriteLine(ModeValidationArguments.Usage);
        writer.WriteLine();
        writer.WriteLine(
            "Loads the mode manifest and prompts through the runtime loader. " +
            "A mode whose prompts live under lens variants needs --lens. " +
            "No model call is made and no run output is written.");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --lens <name>         Prompt variant to validate, as in a run.");
        writer.WriteLine("  --modes-root <path>   Defaults to ./modes.");
        writer.WriteLine();
        writer.WriteLine("Exit codes:");
        writer.WriteLine("  0    Mode is valid.");
        writer.WriteLine("  1    Mode is invalid or could not be read.");
        writer.WriteLine("  2    Usage error.");
        writer.WriteLine("  130  Cancelled.");
    }

    private async Task<int> ExecuteAsync(
        CliOptions options,
        string input,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Running mode {Mode} with provider {Provider}.",
            options.Mode,
            options.ModelProvider);

        var result = await _orchestrator.RunAsync(
            new RunRequest(
                options.Mode,
                input,
                options.ModelProvider,
                options.OutputRoot,
                options.WriteHtmlView,
                options.InputPath,
                options.Lens,
                options.Pattern,
                options.PipelineStages,
                options.ScatterModes),
            cancellationToken);

        Console.WriteLine($"Run ID: {result.RunId}");
        Console.WriteLine($"Output: {result.OutputDirectory}");
        Console.WriteLine(
            $"Evaluation: {(result.Outcome == RunOutcome.Success ? "PASS" : "FAIL")}");
        if (result.HtmlViewPath is not null)
        {
            Console.WriteLine($"HTML: {result.HtmlViewPath}");
        }

        return ExitCodes.FromOutcome(result.Outcome);
    }

    private async Task<int> ExecuteExperimentAsync(
        CliOptions options,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Running experiment {Experiment} with provider {Provider}.",
            options.Experiment,
            options.ModelProvider);

        var result = await _dungeonExperimentRunner.RunAsync(
            new DungeonExperimentRequest(
                options.Brief ?? CliOptions.DungeonBuilderBrief,
                options.ModelProvider,
                options.OutputRoot,
                options.ExperimentsRoot,
                InputSource: null,
                FlareGameRoot: options.FlareGameRoot),
            cancellationToken);

        Console.WriteLine($"Run ID: {result.Run.RunId}");
        Console.WriteLine($"Output: {result.Run.OutputDirectory}");
        Console.WriteLine(
            $"Evaluation: {(result.Run.Outcome == RunOutcome.Success ? "PASS" : "FAIL")}");
        Console.WriteLine(
            result.WinnerIndex is null
                ? "Winner: none"
                : $"Winner: candidate {result.WinnerIndex:D2}");
        if (result.TmxPath is not null)
        {
            Console.WriteLine($"TMX: {result.TmxPath}");
        }
        if (result.FlareModPath is not null)
        {
            Console.WriteLine($"Flare mod: {result.FlareModPath}");
        }

        return ExitCodes.FromOutcome(result.Run.Outcome);
    }

    private static ServiceCollection BuildServices(
        CliOptions options,
        IConfiguration configuration)
    {
        var services = new ServiceCollection();

        services.AddLogging(
            builder => builder
                .SetMinimumLevel(LogLevel.Information)
                .AddSimpleConsole(console =>
                {
                    console.SingleLine = true;
                    console.TimestampFormat = "HH:mm:ss ";
                }));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(RunBudget.Default);
        services.AddSingleton<IRunIdGenerator, GuidRunIdGenerator>();
        services.AddSingleton<HttpClient>(
            _ => new HttpClient
            {
                Timeout = TimeSpan.FromMinutes(2)
            });

        services.AddSingleton(
            new GitHubModelsOptions(
                configuration["GITHUB_MODELS_ENDPOINT"]
                    ?? "https://models.github.ai/inference",
                configuration["GITHUB_MODELS_TOKEN"]
                    ?? configuration["GITHUB_TOKEN"],
                configuration["GITHUB_MODELS_MODEL"]
                    ?? "openai/gpt-4.1",
                configuration["GITHUB_MODELS_API_VERSION"]
                    ?? "2026-03-10"));
        services.AddSingleton(
            new AzureFoundryOptions(
                configuration["AZURE_FOUNDRY_ENDPOINT"],
                configuration["AZURE_FOUNDRY_API_KEY"],
                configuration["AZURE_FOUNDRY_DEPLOYMENT"],
                configuration["AZURE_FOUNDRY_API_VERSION"]));
        services.AddSingleton(
            new OpenRouterOptions(
                configuration["OPENROUTER_ENDPOINT"]
                    ?? "https://openrouter.ai/api/v1",
                configuration["OPENROUTER_API_KEY"],
                configuration["OPENROUTER_MODEL"],
                bool.TryParse(
                    configuration["OPENROUTER_ENABLE_CODE_EXECUTION"],
                    out var enableCodeExecution) && enableCodeExecution));

        services.AddSingleton<IModeLoader>(
            _ => new FileModeLoader(options.ModesRoot));
        services.AddSingleton<IRunStateStore>(
            _ => DatabaseRunStateStoreFactory.Create(configuration));
        services.AddSingleton<IArtifactStore>(
            _ => ArtifactStoreFactory.Create(
                options.OutputRoot,
                configuration));
        services.AddSingleton<ISemanticIndex, NullSemanticIndex>();
        services.AddSingleton<IArtifactWriter, ArtifactWriter>();
        services.AddSingleton<IRunViewWriter, HtmlRunViewWriter>();
        services.AddSingleton<ITraceSessionFactory, JsonTraceSessionFactory>();
        services.AddSingleton<OutputContractValidator>();
        services.AddSingleton<LoopEfficacyEvaluator>();
        services.AddSingleton<IEvalRunner, EvalRunner>();
        services.AddSingleton<PhaseRunner>();
        services.AddSingleton<PatternExecutionPlanValidator>();
        services.AddSingleton<PatternExecutor>();
        services.AddSingleton<IOrchestrationPattern, CriticRevisionPattern>();
        services.AddSingleton<IOrchestrationPattern, SinglePassPattern>();
        services.AddSingleton<IOrchestrationPattern, LinearPipelinePattern>();
        services.AddSingleton<IOrchestrationPattern, ScatterGatherPattern>();
        services.AddSingleton<IOrchestrationPatternFactory, OrchestrationPatternFactory>();

        services.AddSingleton<IModelClient, MockModelClient>();
        services.AddSingleton<IModelClient>(
            provider => new GitHubModelsClient(
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<GitHubModelsOptions>()));
        services.AddSingleton<IModelClient>(
            provider => new AzureFoundryModelClient(
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<AzureFoundryOptions>()));
        services.AddSingleton<IModelClient>(
            provider => new OpenRouterModelClient(
                provider.GetRequiredService<HttpClient>(),
                provider.GetRequiredService<OpenRouterOptions>()));
        services.AddSingleton<IModelClientFactory, ModelClientFactory>();

        services.AddSingleton(new ToolPolicy(Array.Empty<string>()));
        services.AddSingleton<IToolExecutor, ToolExecutor>();
        services.AddSingleton<IToolProvider, MockToolProvider>();
        services.AddSingleton<IToolProvider, McpToolProvider>();

        services.AddSingleton<DungeonArtifactWriter>();
        services.AddSingleton<DungeonExperimentDefinitionLoader>();
        services.AddSingleton<DungeonPlanVerifier>();
        services.AddSingleton<DungeonCompiler>();
        services.AddSingleton<DungeonExperimentRunner>();

        services.AddSingleton<Orchestrator>();
        services.AddSingleton<CliApplication>();

        return services;
    }

    private static void WriteHelp(TextWriter writer)
    {
        writer.WriteLine("Cognitive Runtime Lab");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine(
            "  dotnet run --project src/CognitiveRuntime.Cli -- " +
            "--mode <name> --input <path> [options]");
        writer.WriteLine(
            "  dotnet run --project src/CognitiveRuntime.Cli -- " +
            "modes validate <mode> [--lens <name>] [--modes-root <path>]");
        writer.WriteLine(
            "  dotnet run --project src/CognitiveRuntime.Cli -- " +
            "modes list [--modes-root <path>]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --mode <name>            Mode directory name.");
        writer.WriteLine("  --input <path>            Input text file.");
        writer.WriteLine(
            "  --run-mode <provider>     mock, github-models, openrouter, " +
            "or azure-foundry.");
        writer.WriteLine("  --modes-root <path>       Defaults to ./modes.");
        writer.WriteLine("  --output-root <path>      Defaults to ./outputs.");
        writer.WriteLine("  --html                    Write read-only index.html.");
        writer.WriteLine(
            "  --lens <name>             Prompt lens subdirectory " +
            "(e.g. warcraft for the lens mode).");
        writer.WriteLine(
            "  --pattern <name>          Orchestration pattern: single-pass, " +
            "critic-revision (default), or linear-pipeline.");
        writer.WriteLine(
            "  --pipeline <mode,mode,..> Stage modes for --pattern linear-pipeline.");
        writer.WriteLine(
            "  --experiment <name>       Run a bounded experiment instead of a " +
            "mode (e.g. dungeon-builder).");
        writer.WriteLine(
            "  --experiments-root <path> Defaults to ./experiments.");
        writer.WriteLine(
            "  --brief <text>            Overrides the experiment brief.");
        writer.WriteLine(
            "  --flare-game-root <path>  Optional local Flare game-data " +
            "checkout to copy tilesheets from.");
        writer.WriteLine("  --help                    Show this help.");
    }
}
