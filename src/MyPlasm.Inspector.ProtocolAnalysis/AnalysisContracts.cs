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

public sealed class AnalysisOutputException : Exception
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
