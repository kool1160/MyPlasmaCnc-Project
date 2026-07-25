using MyPlasm.Inspector.ProtocolAnalysis;
using MyPlasm.ProtocolAnalyzer;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class CliTests
{
    [Fact]
    public async Task AnalyzeCommandRunsEndToEndWithoutPrintingPayloads()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        capture.Open(1);
        capture.Write(1, "FACEB00C");
        capture.Read(1, "C001D00D", 4);
        capture.Close(1);
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));
        string hash = await CaptureAnalyzer.CalculateSha256Async(input);
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await AnalyzerCli.RunAsync(
            [
                "analyze",
                "--input",
                input,
                "--output",
                workspace.PathFor("analysis"),
                "--expected-sha256",
                hash
            ],
            output,
            error);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains("Analysis complete", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("FACEB00C", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("C001D00D", output.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(workspace.PathFor("analysis/hashes.sha256")));
    }

    [Fact]
    public async Task UsageHashValidationAndSchemaFailuresHaveDistinctNonzeroExitCodes()
    {
        using TestWorkspace workspace = new();
        StringWriter output = new();
        StringWriter error = new();
        int usage = await AnalyzerCli.RunAsync([], output, error);
        Assert.Equal(2, usage);

        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        int hash = await AnalyzerCli.RunAsync(
            [
                "analyze",
                "--input",
                input,
                "--output",
                workspace.PathFor("hash-output"),
                "--expected-sha256",
                new string('0', 64)
            ],
            output,
            error);
        Assert.Equal(3, hash);

        await File.WriteAllTextAsync(workspace.PathFor("invalid.jsonl"), "{\"no\":\"schema\"}\n");
        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        int validation = await AnalyzerCli.RunAsync(
            [
                "analyze",
                "--input",
                workspace.PathFor("invalid.jsonl"),
                "--output",
                workspace.PathFor("invalid-output")
            ],
            output,
            error);
        Assert.Equal(4, validation);
        Assert.Contains("Line 1", error.ToString(), StringComparison.Ordinal);
    }
}
