namespace MyPlasm.Inspector.ProtocolAnalysis;

public sealed record AnalysisRequest(
    string InputPath,
    string OutputDirectory,
    string? ExpectedSha256 = null,
    bool Overwrite = false);

public sealed record AnalysisProgress(long RecordsProcessed, long LinesRead);

public sealed record AnalysisResult(
    string InputSha256,
    long RecordCount,
    long TransactionCount,
    IReadOnlyDictionary<string, string> OutputSha256);

public sealed record CampaignComparisonRequest(
    IReadOnlyList<string> AnalysisDirectories,
    string OutputDirectory,
    bool Overwrite = false);

public sealed record CampaignComparisonResult(
    int RunCount,
    int StableTransactionClassCount,
    IReadOnlyDictionary<string, string> OutputSha256);

public sealed class CaptureValidationException : Exception
{
    public CaptureValidationException(long lineNumber, string message)
        : base($"Line {lineNumber}: {message}")
    {
        LineNumber = lineNumber;
    }

    public long LineNumber { get; }
}

public sealed class InputHashMismatchException : Exception
{
    public InputHashMismatchException(string expected, string actual)
        : base($"Input SHA-256 mismatch. Expected {expected}; actual {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    public string Expected { get; }

    public string Actual { get; }
}

public class AnalysisOutputException : Exception
{
    public AnalysisOutputException(string message)
        : base(message)
    {
    }

    public AnalysisOutputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class InputOutputPathCollisionException : AnalysisOutputException
{
    public InputOutputPathCollisionException(string reportFileName)
        : base(
            $"The input evidence path collides with analyzer output '{reportFileName}'. " +
            "Choose a different output directory; --overwrite never permits replacing input evidence.")
    {
        ReportFileName = reportFileName;
    }

    public string ReportFileName { get; }
}

public sealed class CampaignInputValidationException : Exception
{
    public CampaignInputValidationException(string message)
        : base(message)
    {
    }

    public CampaignInputValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public sealed class CampaignPathCollisionException : AnalysisOutputException
{
    public CampaignPathCollisionException(string message)
        : base(message)
    {
    }
}
