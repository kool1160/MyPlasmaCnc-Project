using System.Text.Json;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class AnalyzerBehaviorTests
{
    [Fact]
    public async Task NormalReconnectCaptureProducesSanitizedStructuralReports()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.ListDevices();
        capture.Open(11);
        capture.Baud(11);
        capture.Data(11);
        capture.Flow(11);
        capture.Latency(11);
        capture.BitMode(11);
        capture.Write(11, "A1B2");
        capture.Queue(11);
        capture.Queue(11, 2);
        capture.Read(11, "C3D4", 8);
        capture.Close(11);
        capture.Close(11, status: 1);
        capture.ListDevices();
        capture.Open(22);
        capture.Write(22, "E5");
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));
        byte[] originalInput = await File.ReadAllBytesAsync(input);
        string expectedHash = await CaptureAnalyzer.CalculateSha256Async(input);

        AnalysisResult result = await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis"), expectedHash));

        Assert.Equal(16, result.RecordCount);
        Assert.Equal(1, result.TransactionCount);
        Assert.Equal(originalInput, await File.ReadAllBytesAsync(input));
        AssertRequiredOutputs(workspace.PathFor("analysis"));
        string hashManifest = await File.ReadAllTextAsync(
            workspace.PathFor("analysis/hashes.sha256"));
        foreach ((string fileName, string sha256) in result.OutputSha256)
        {
            Assert.Equal(
                sha256,
                await CaptureAnalyzer.CalculateSha256Async(
                    workspace.PathFor($"analysis/{fileName}")));
            Assert.Contains($"{sha256}  {fileName}", hashManifest, StringComparison.Ordinal);
        }

        using JsonDocument summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/capture-summary.json")));
        JsonElement root = summary.RootElement;
        Assert.Equal(1, root.GetProperty("transactions").GetProperty("matched").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("transactions").GetProperty("unmatched_writes").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("anomalies").GetProperty("redundant_closes").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("anomalies").GetProperty("unclosed_handles").GetInt64());
        Assert.Equal(2, root.GetProperty("handle_sessions").GetArrayLength());
        JsonElement comparison = root.GetProperty("session_class_frequency_comparison");
        Assert.NotEmpty(comparison.EnumerateArray());
        Assert.All(
            comparison.EnumerateArray(),
            item => Assert.Equal(
                2,
                item.GetProperty("counts_by_session").EnumerateObject().Count()));

        string phases = await File.ReadAllTextAsync(
            workspace.PathFor("analysis/phase-timeline.csv"));
        Assert.Contains("process_start_pre_open", phases, StringComparison.Ordinal);
        Assert.Contains("configuration_call", phases, StringComparison.Ordinal);
        Assert.Contains("sustained_exchange_interval", phases, StringComparison.Ordinal);
        Assert.Contains("reconnect_transition", phases, StringComparison.Ordinal);
        Assert.Contains("redundant_close", phases, StringComparison.Ordinal);
        Assert.Contains("unclosed_handle_at_capture_end", phases, StringComparison.Ordinal);

        foreach (string report in Directory.GetFiles(workspace.PathFor("analysis")))
        {
            string content = await File.ReadAllTextAsync(report);
            Assert.DoesNotContain("A1B2", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("C3D4", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("write_hex", content, StringComparison.Ordinal);
            Assert.DoesNotContain("read_hex", content, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTHETIC-DEVICE", content, StringComparison.Ordinal);
            Assert.DoesNotContain("0x00002000", content, StringComparison.Ordinal);
            Assert.DoesNotContain(workspace.Root, content, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task PairingNeverCrossesSuccessfulCloseAndReopenBoundary()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.Open(44);
        capture.Write(44, "0102");
        capture.Close(44);
        capture.Open(44);
        capture.Read(44, "0304", 2);
        capture.Close(44);
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis")));

        using JsonDocument summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/capture-summary.json")));
        JsonElement transactions = summary.RootElement.GetProperty("transactions");
        Assert.Equal(0, transactions.GetProperty("matched").GetInt64());
        Assert.Equal(1, transactions.GetProperty("unmatched_writes").GetInt64());
        Assert.Equal(1, transactions.GetProperty("unexpected_reads").GetInt64());
    }

    [Fact]
    public async Task MultipleHandlesAndFailedCallsRemainFirstClassObservations()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.Open(1);
        capture.Open(2);
        capture.Write(1, "1111");
        capture.Read(2, "2222", 2);
        capture.Write(1, "3333", status: 4, actualCount: 0);
        capture.Read(1, string.Empty, 8, status: 4);
        capture.Open(0, status: 2);
        capture.Close(1);
        capture.Close(2);
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis")));

        using JsonDocument summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/capture-summary.json")));
        JsonElement root = summary.RootElement;
        Assert.Equal(
            1,
            root.GetProperty("anomalies").GetProperty("failed_opens").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("anomalies").GetProperty("failed_writes").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("anomalies").GetProperty("failed_reads").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("transactions").GetProperty("unmatched_writes").GetInt64());
        Assert.Equal(
            1,
            root.GetProperty("transactions").GetProperty("unexpected_reads").GetInt64());
    }

    [Fact]
    public async Task QueuePollsAndLatencyUseDocumentedDeterministicRule()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.Open(3);
        capture.Write(3, "1010");
        capture.Queue(3);
        capture.Queue(3);
        capture.Read(3, "2020", 2);
        capture.Close(3);
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis")));

        using JsonDocument summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/capture-summary.json")));
        JsonElement transactions = summary.RootElement.GetProperty("transactions");
        Assert.Equal(
            2,
            transactions.GetProperty("queue_polls_per_transaction")
                .GetProperty("minimum")
                .GetInt32());
        Assert.Equal(
            3_000,
            transactions.GetProperty("latency_microseconds")
                .GetProperty("minimum")
                .GetInt32());
    }

    [Fact]
    public async Task PartialWriteAndEmptyReadArePairedButMarkedUncertain()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.Open(6);
        capture.Write(6, "AABB", actualCount: 1);
        capture.Read(6, string.Empty, 4);
        capture.Close(6);
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis")));

        using JsonDocument summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/capture-summary.json")));
        JsonElement transactions = summary.RootElement.GetProperty("transactions");
        Assert.Equal(1, transactions.GetProperty("matched").GetInt64());
        Assert.Equal(1, transactions.GetProperty("pairing_uncertainties").GetInt64());
    }

    private static void AssertRequiredOutputs(string directory)
    {
        string[] expected =
        [
            "capture-summary.json",
            "capture-report.md",
            "phase-timeline.csv",
            "transaction-classes.csv",
            "payload-variability.json",
            "hashes.sha256"
        ];
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            Directory.GetFiles(directory)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));
    }
}
