using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.ProtocolAnalyzer;

public static class AnalyzerCli
{
    public static async Task<int> RunAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        if (arguments.Length == 0)
        {
            await error.WriteLineAsync("A command is required.").ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
            return 2;
        }

        return arguments[0] switch
        {
            "analyze" => await RunAnalyzeAsync(
                    arguments,
                    output,
                    error,
                    cancellationToken)
                .ConfigureAwait(false),
            "compare" => await RunCompareAsync(
                    arguments,
                    output,
                    error,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => await WriteUnknownCommandAsync(arguments[0], error)
                .ConfigureAwait(false)
        };
    }

    private static async Task<int> RunAnalyzeAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseAnalyze(
                arguments,
                out AnalysisRequest? request,
                out string? usageError))
        {
            await error.WriteLineAsync(usageError).ConfigureAwait(false);
            await error.WriteLineAsync(AnalyzeUsage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            InlineProgress progress = new(update =>
            {
                output.WriteLine(
                    $"Validated {update.RecordsProcessed:N0} records ({update.LinesRead:N0} lines); raw payloads are not printed.");
            });
            CaptureAnalyzer analyzer = new();
            AnalysisResult result = await analyzer.AnalyzeAsync(
                    request!,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            await output.WriteLineAsync(
                    $"Analysis complete: {result.RecordCount:N0} records, {result.TransactionCount:N0} deterministic transactions.")
                .ConfigureAwait(false);
            await output.WriteLineAsync($"Input SHA-256: {result.InputSha256}")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                    $"Reports written to: {Path.GetFullPath(request!.OutputDirectory)}")
                .ConfigureAwait(false);
            return 0;
        }
        catch (InputHashMismatchException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 3;
        }
        catch (CaptureValidationException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 4;
        }
        catch (AnalysisOutputException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 5;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or DirectoryNotFoundException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Analysis canceled.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync($"Unexpected analyzer failure: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<int> RunCompareAsync(
        string[] arguments,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!TryParseCompare(
                arguments,
                out CampaignComparisonRequest? request,
                out string? usageError))
        {
            await error.WriteLineAsync(usageError).ConfigureAwait(false);
            await error.WriteLineAsync(CompareUsage).ConfigureAwait(false);
            return 2;
        }

        try
        {
            CampaignComparisonResult result =
                await new CampaignComparator().CompareAsync(
                        request!,
                        cancellationToken)
                    .ConfigureAwait(false);
            await output.WriteLineAsync(
                    $"Comparison complete: {result.RunCount} verified runs; " +
                    $"{result.StableTransactionClassCount} transaction classes stable across all three captures.")
                .ConfigureAwait(false);
            await output.WriteLineAsync(
                    $"Reports written to: {Path.GetFullPath(request!.OutputDirectory)}")
                .ConfigureAwait(false);
            return 0;
        }
        catch (CampaignInputValidationException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 4;
        }
        catch (AnalysisOutputException exception)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 5;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                FileNotFoundException or
                DirectoryNotFoundException)
        {
            await error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Comparison canceled.").ConfigureAwait(false);
            return 1;
        }
        catch (Exception exception)
        {
            await error.WriteLineAsync(
                    $"Unexpected comparison failure: {exception.Message}")
                .ConfigureAwait(false);
            return 1;
        }
    }

    private const string Usage =
        "Usage:\n" +
        "  MyPlasm.ProtocolAnalyzer analyze --input <traffic.jsonl> --output <directory> [--expected-sha256 <hash>] [--overwrite]\n" +
        "  MyPlasm.ProtocolAnalyzer compare --analysis <directory> --analysis <directory> --analysis <directory> --output <directory> [--overwrite]";

    private const string AnalyzeUsage =
        "Usage: MyPlasm.ProtocolAnalyzer analyze --input <traffic.jsonl> --output <directory> [--expected-sha256 <hash>] [--overwrite]";

    private const string CompareUsage =
        "Usage: MyPlasm.ProtocolAnalyzer compare --analysis <directory> --analysis <directory> --analysis <directory> --output <directory> [--overwrite]";

    private static bool TryParseAnalyze(
        string[] arguments,
        out AnalysisRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        if (arguments.Length == 0 ||
            !string.Equals(arguments[0], "analyze", StringComparison.Ordinal))
        {
            error = "The required command is 'analyze'.";
            return false;
        }

        string? input = null;
        string? output = null;
        string? expectedSha256 = null;
        bool overwrite = false;
        HashSet<string> seen = new(StringComparer.Ordinal);

        for (int index = 1; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (!seen.Add(option))
            {
                error = $"Option '{option}' was supplied more than once.";
                return false;
            }

            if (string.Equals(option, "--overwrite", StringComparison.Ordinal))
            {
                overwrite = true;
                continue;
            }

            if (option is not ("--input" or "--output" or "--expected-sha256"))
            {
                error = $"Unknown option '{option}'.";
                return false;
            }

            if (++index >= arguments.Length || arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Option '{option}' requires a value.";
                return false;
            }

            switch (option)
            {
                case "--input":
                    input = arguments[index];
                    break;
                case "--output":
                    output = arguments[index];
                    break;
                case "--expected-sha256":
                    expectedSha256 = arguments[index];
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(output))
        {
            error = "Both --input and --output are required.";
            return false;
        }

        request = new AnalysisRequest(input, output, expectedSha256, overwrite);
        return true;
    }

    private static bool TryParseCompare(
        string[] arguments,
        out CampaignComparisonRequest? request,
        out string? error)
    {
        request = null;
        error = null;
        if (arguments.Length == 0 ||
            !string.Equals(arguments[0], "compare", StringComparison.Ordinal))
        {
            error = "The required command is 'compare'.";
            return false;
        }

        List<string> analysisDirectories = [];
        string? output = null;
        bool overwrite = false;
        bool outputSeen = false;
        bool overwriteSeen = false;

        for (int index = 1; index < arguments.Length; index++)
        {
            string option = arguments[index];
            if (option == "--overwrite")
            {
                if (overwriteSeen)
                {
                    error = "Option '--overwrite' was supplied more than once.";
                    return false;
                }

                overwriteSeen = true;
                overwrite = true;
                continue;
            }

            if (option is not ("--analysis" or "--output"))
            {
                error = $"Unknown option '{option}'.";
                return false;
            }

            if (++index >= arguments.Length ||
                arguments[index].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"Option '{option}' requires a value.";
                return false;
            }

            if (option == "--analysis")
            {
                analysisDirectories.Add(arguments[index]);
                continue;
            }

            if (outputSeen)
            {
                error = "Option '--output' was supplied more than once.";
                return false;
            }

            outputSeen = true;
            output = arguments[index];
        }

        if (analysisDirectories.Count != 3 ||
            string.IsNullOrWhiteSpace(output))
        {
            error =
                "Exactly three --analysis directories and one --output directory are required.";
            return false;
        }

        request = new CampaignComparisonRequest(
            analysisDirectories,
            output,
            overwrite);
        return true;
    }

    private static async Task<int> WriteUnknownCommandAsync(
        string command,
        TextWriter error)
    {
        await error.WriteLineAsync($"Unknown command '{command}'.")
            .ConfigureAwait(false);
        await error.WriteLineAsync(Usage).ConfigureAwait(false);
        return 2;
    }

    private sealed class InlineProgress(Action<AnalysisProgress> report)
        : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => report(value);
    }
}
