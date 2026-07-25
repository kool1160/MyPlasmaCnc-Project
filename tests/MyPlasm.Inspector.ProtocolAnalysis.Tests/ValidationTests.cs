using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class ValidationTests
{
    [Fact]
    public async Task DuplicateSequenceFailsWithLineNumber()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        JsonObject first = capture.ListDevices();
        JsonObject second = capture.ListDevices();
        capture.SetSequence(second, first["sequence"]!.GetValue<ulong>());
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Equal(2, exception.LineNumber);
        Assert.Contains("sequence", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GappedIncreasingSequenceIsPreservedAndReported()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.ListDevices();
        JsonObject second = capture.Open(5);
        capture.SetSequence(second, 4);
        capture.Close(5);
        JsonObject third = capture.Records[2];
        capture.SetSequence(third, 5);
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        await AnalyzeAsync(workspace, input);

        using JsonDocument summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/capture-summary.json")));
        JsonElement sequence = summary.RootElement.GetProperty("sequence");
        Assert.Equal(1, sequence.GetProperty("gap_count").GetInt64());
        Assert.Equal(2UL, sequence.GetProperty("missing_sequence_count").GetUInt64());
    }

    [Fact]
    public async Task MalformedJsonFailsWithExactLine()
    {
        using TestWorkspace workspace = new();
        string input = workspace.PathFor("traffic.jsonl");
        await File.WriteAllTextAsync(input, "{}\n{\"broken\":\n", Encoding.UTF8);

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Equal(1, exception.LineNumber);
        Assert.Contains("schema_version", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedSecondJsonLineIsReportedAsLineTwo()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(workspace.PathFor("valid.jsonl"));
        string valid = await File.ReadAllTextAsync(input);
        input = workspace.PathFor("traffic.jsonl");
        await File.WriteAllTextAsync(input, valid + "{\"broken\":\n", Encoding.UTF8);

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Equal(2, exception.LineNumber);
        Assert.Contains("malformed JSON", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-type")]
    [InlineData("unsupported-schema")]
    [InlineData("invalid-hex")]
    [InlineData("odd-hex")]
    [InlineData("length-mismatch")]
    [InlineData("read-bound")]
    public async Task InvalidSchemaAndPayloadCasesFailClosed(string failure)
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        JsonObject record = failure == "read-bound"
            ? capture.Read(1, "AA", 1)
            : capture.Write(1, "AABB");

        switch (failure)
        {
            case "missing":
                record.Remove("status");
                break;
            case "wrong-type":
                record["status"] = "zero";
                break;
            case "unsupported-schema":
                record["schema_version"] = 2;
                break;
            case "invalid-hex":
                record["write_hex"] = "AAGG";
                break;
            case "odd-hex":
                record["write_hex"] = "ABC";
                break;
            case "length-mismatch":
                record["requested_count"] = 3;
                break;
            case "read-bound":
                record["requested_count"] = 0;
                break;
        }

        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Equal(1, exception.LineNumber);
    }

    [Fact]
    public async Task UnknownAdditionalFieldsAreIgnoredSafely()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        JsonObject record = capture.ListDevices();
        record["future_extension"] = new JsonObject
        {
            ["opaque"] = true
        };
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        AnalysisResult result = await AnalyzeAsync(workspace, input);

        Assert.Equal(1, result.RecordCount);
    }

    [Fact]
    public async Task SessionChangeFailsWithLineNumber()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.ListDevices();
        JsonObject second = capture.ListDevices();
        second["session_id"] = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE";
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Equal(2, exception.LineNumber);
        Assert.Contains("session_id changed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyCaptureFailsClosed()
    {
        using TestWorkspace workspace = new();
        string input = workspace.PathFor("traffic.jsonl");
        await File.WriteAllTextAsync(input, "\n \n", Encoding.UTF8);

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Contains("no nonempty", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUtf8FailsWithLineNumber()
    {
        using TestWorkspace workspace = new();
        string input = workspace.PathFor("traffic.jsonl");
        await File.WriteAllBytesAsync(input, [0x7B, 0x22, 0xFF, 0x22, 0x7D, 0x0A]);

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Equal(1, exception.LineNumber);
        Assert.Contains("invalid UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidUtf8AfterValidRecordReportsItsPhysicalLine()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));
        await using (FileStream stream = new(input, FileMode.Append, FileAccess.Write))
        {
            await stream.WriteAsync(new byte[] { 0x7B, 0x22, 0xFF, 0x22, 0x7D, 0x0A });
        }

        CaptureValidationException exception = await Assert.ThrowsAsync<CaptureValidationException>(
            () => AnalyzeAsync(workspace, input));

        Assert.Equal(2, exception.LineNumber);
        Assert.Contains("invalid UTF-8", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HashMismatchFailsBeforeAnalysisOrOutputCreation()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));
        string output = workspace.PathFor("analysis");

        InputHashMismatchException exception = await Assert.ThrowsAsync<InputHashMismatchException>(
            () => new CaptureAnalyzer().AnalyzeAsync(
                new AnalysisRequest(input, output, new string('0', 64))));

        Assert.NotEqual(exception.Expected, exception.Actual);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task NonemptyOutputRequiresExplicitOverwrite()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));
        string output = workspace.PathFor("analysis");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "keep.txt"), "unrelated");

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => new CaptureAnalyzer().AnalyzeAsync(new AnalysisRequest(input, output)));

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, output, Overwrite: true));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(Path.Combine(output, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(output, "capture-summary.json")));
    }

    private static Task<AnalysisResult> AnalyzeAsync(TestWorkspace workspace, string input) =>
        new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis")));
}
