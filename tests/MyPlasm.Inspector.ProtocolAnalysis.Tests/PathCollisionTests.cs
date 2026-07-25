using MyPlasm.Inspector.ProtocolAnalysis;
using MyPlasm.ProtocolAnalyzer;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class PathCollisionTests
{
    public static TheoryData<string, bool> ReportCollisions => new()
    {
        { "capture-summary.json", false },
        { "capture-summary.json", true },
        { "capture-report.md", false },
        { "capture-report.md", true },
        { "phase-timeline.csv", false },
        { "phase-timeline.csv", true },
        { "transaction-classes.csv", false },
        { "transaction-classes.csv", true },
        { "payload-variability.json", false },
        { "payload-variability.json", true },
        { "hashes.sha256", false },
        { "hashes.sha256", true }
    };

    [Theory]
    [MemberData(nameof(ReportCollisions))]
    public async Task EveryReportCollisionFailsBeforeWritingAndPreservesInput(
        string reportFileName,
        bool overwrite)
    {
        using TestWorkspace workspace = new();
        string evidenceDirectory = workspace.PathFor("evidence");
        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(
            Path.Combine(evidenceDirectory, reportFileName));
        byte[] original = await File.ReadAllBytesAsync(input);
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(input);

        InputOutputPathCollisionException exception =
            await Assert.ThrowsAsync<InputOutputPathCollisionException>(
                () => new CaptureAnalyzer().AnalyzeAsync(
                    new AnalysisRequest(input, evidenceDirectory, Overwrite: overwrite)));

        Assert.Equal(reportFileName, exception.ReportFileName);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Equal(originalWriteTime, File.GetLastWriteTimeUtc(input));
        Assert.Equal([reportFileName], Directory.GetFiles(evidenceDirectory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task RelativePathAliasesAreRejectedBeforeWriting()
    {
        using TestWorkspace workspace = new();
        string evidenceDirectory = workspace.PathFor("evidence");
        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(
            Path.Combine(evidenceDirectory, "capture-summary.json"));
        byte[] original = await File.ReadAllBytesAsync(input);
        string aliasedInput = Path.Combine(evidenceDirectory, ".", "capture-summary.json");
        string aliasedOutput = Path.Combine(evidenceDirectory, "unused", "..");

        await Assert.ThrowsAsync<InputOutputPathCollisionException>(
            () => new CaptureAnalyzer().AnalyzeAsync(
                new AnalysisRequest(aliasedInput, aliasedOutput, Overwrite: true)));

        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(evidenceDirectory));
    }

    [Fact]
    public async Task CollisionFailsBeforeHashOrSchemaValidation()
    {
        using TestWorkspace workspace = new();
        string evidenceDirectory = workspace.PathFor("evidence");
        Directory.CreateDirectory(evidenceDirectory);
        string input = Path.Combine(evidenceDirectory, "hashes.sha256");
        byte[] original = [0xFF, 0x00, 0x7B];
        await File.WriteAllBytesAsync(input, original);

        await Assert.ThrowsAsync<InputOutputPathCollisionException>(
            () => new CaptureAnalyzer().AnalyzeAsync(
                new AnalysisRequest(
                    input,
                    evidenceDirectory,
                    ExpectedSha256: new string('0', 64),
                    Overwrite: true)));

        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(evidenceDirectory));
    }

    [Fact]
    public async Task ExistingLegacyTemporaryNameIsNeverUsedOrModified()
    {
        using TestWorkspace workspace = new();
        string evidenceDirectory = workspace.PathFor("evidence");
        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(
            Path.Combine(evidenceDirectory, "capture-summary.json.tmp"));
        byte[] original = await File.ReadAllBytesAsync(input);

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, evidenceDirectory, Overwrite: true));

        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.True(File.Exists(Path.Combine(evidenceDirectory, "capture-summary.json")));
        Assert.DoesNotContain(
            Directory.GetFiles(evidenceDirectory).Select(Path.GetFileName),
            name => name is not null && name.StartsWith(".", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryLinkAliasIsRejectedWhenLinksAreAvailable()
    {
        using TestWorkspace workspace = new();
        string evidenceDirectory = workspace.PathFor("evidence");
        Directory.CreateDirectory(evidenceDirectory);
        string aliasDirectory = workspace.PathFor("evidence-link");
        try
        {
            Directory.CreateSymbolicLink(aliasDirectory, evidenceDirectory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return;
        }

        SyntheticCapture capture = new();
        capture.ListDevices();
        string input = await capture.WriteAsync(
            Path.Combine(evidenceDirectory, "capture-summary.json"));
        byte[] original = await File.ReadAllBytesAsync(input);

        await Assert.ThrowsAsync<InputOutputPathCollisionException>(
            () => new CaptureAnalyzer().AnalyzeAsync(
                new AnalysisRequest(input, aliasDirectory, Overwrite: true)));

        Assert.Equal(original, await File.ReadAllBytesAsync(input));
        Assert.Single(Directory.GetFiles(evidenceDirectory));
    }

    [Fact]
    public async Task CliReportsCollisionAsOutputFailureWithoutPrintingEvidence()
    {
        using TestWorkspace workspace = new();
        string evidenceDirectory = workspace.PathFor("evidence");
        SyntheticCapture capture = new();
        capture.Write(1, "FACEB00C");
        string input = await capture.WriteAsync(
            Path.Combine(evidenceDirectory, "capture-report.md"));
        byte[] original = await File.ReadAllBytesAsync(input);
        StringWriter output = new();
        StringWriter error = new();

        int exitCode = await AnalyzerCli.RunAsync(
            [
                "analyze",
                "--input",
                input,
                "--output",
                evidenceDirectory,
                "--overwrite"
            ],
            output,
            error);

        Assert.Equal(5, exitCode);
        Assert.Contains("collides", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FACEB00C", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, await File.ReadAllBytesAsync(input));
    }
}
