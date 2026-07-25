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

        if (!TryParse(arguments, out AnalysisRequest? request, out string? usageError))
        {
            await error.WriteLineAsync(usageError).ConfigureAwait(false);
            await error.WriteLineAsync(Usage).ConfigureAwait(false);
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

    private const string Usage =
        "Usage: MyPlasm.ProtocolAnalyzer analyze --input <traffic.jsonl> --output <directory> [--expected-sha256 <hash>] [--overwrite]";

    private static bool TryParse(
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

    private sealed class InlineProgress(Action<AnalysisProgress> report)
        : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => report(value);
    }
}
