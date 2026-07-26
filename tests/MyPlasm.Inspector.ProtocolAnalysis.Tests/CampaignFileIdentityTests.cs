using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class CampaignFileIdentityTests
{
    [Fact]
    public async Task InputReportLinkedToFinalOutputIsRejectedWithOverwrite()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        string input = Path.Combine(
            fixture.AnalysisDirectories[0],
            "capture-summary.json");
        string final = Path.Combine(output, "campaign-summary.json");
        File.Copy(input, final);
        File.Delete(input);
        Assert.True(
            CampaignTestAssertions.TryCreateHardLink(input, final),
            "The deterministic input/output hard-link fixture could not be created.");

        Dictionary<string, (byte[] Bytes, DateTime Timestamp)> before =
            SnapshotInputs(fixture.AnalysisDirectories);
        await Assert.ThrowsAnyAsync<AnalysisOutputException>(
            () => CompareAsync(
                fixture.AnalysisDirectories,
                output,
                overwrite: true));
        AssertInputsUnchanged(before);
        Assert.Equal(
            await File.ReadAllBytesAsync(final),
            await File.ReadAllBytesAsync(input));
    }

    [Fact]
    public async Task OutputReportLinkedToInputIsRejectedWithoutChangingInput()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        Dictionary<string, (byte[] Bytes, DateTime Timestamp)> before =
        SnapshotInputs(fixture.AnalysisDirectories);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        try
        {
            File.CreateSymbolicLink(
                Path.Combine(output, "campaign-summary.json"),
                Path.Combine(
                    fixture.AnalysisDirectories[0],
                    "capture-summary.json"));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAnyAsync<AnalysisOutputException>(
            () => CompareAsync(
                fixture.AnalysisDirectories,
                output,
                overwrite: true));
        AssertInputsUnchanged(before);
        Assert.Single(Directory.GetFiles(output));
    }

    [Fact]
    public async Task RequiredReportsSharingOnePhysicalHardLinkAreRejected()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string first = Path.Combine(
            fixture.AnalysisDirectories[0],
            "capture-report.md");
        string second = Path.Combine(
            fixture.AnalysisDirectories[1],
            "capture-report.md");
        File.Delete(second);
        Assert.True(
            CampaignTestAssertions.TryCreateHardLink(second, first),
            "The deterministic hard-link fixture could not be created.");

        Dictionary<string, (byte[] Bytes, DateTime Timestamp)> before =
            SnapshotInputs(fixture.AnalysisDirectories);
        string output = workspace.PathFor("comparison");
        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(
                fixture.AnalysisDirectories,
                output,
                overwrite: true));
        Assert.False(Directory.Exists(output));
        AssertInputsUnchanged(before);
    }

    [Fact]
    public async Task OutputHardLinkToInputIsRejectedWithOverwrite()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        string final = Path.Combine(output, "campaign-report.md");
        Assert.True(
            CampaignTestAssertions.TryCreateHardLink(
                final,
                Path.Combine(
                    fixture.AnalysisDirectories[0],
                    "capture-report.md")),
            "The deterministic output hard-link fixture could not be created.");

        Dictionary<string, (byte[] Bytes, DateTime Timestamp)> before =
            SnapshotInputs(fixture.AnalysisDirectories);
        await Assert.ThrowsAsync<CampaignPathCollisionException>(
            () => CompareAsync(
                fixture.AnalysisDirectories,
                output,
                overwrite: true));
        AssertInputsUnchanged(before);
        Assert.True(File.Exists(final));
    }

    [Fact]
    public async Task ExistingOutputReportsSharingOnePhysicalFileAreRejected()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        string first = Path.Combine(output, "campaign-summary.json");
        string second = Path.Combine(output, "campaign-report.md");
        await File.WriteAllTextAsync(first, "old report");
        Assert.True(
            CampaignTestAssertions.TryCreateHardLink(second, first),
            "The deterministic output hard-link fixture could not be created.");
        byte[] before = await File.ReadAllBytesAsync(first);
        DateTime timestamp = File.GetLastWriteTimeUtc(first);

        await Assert.ThrowsAsync<CampaignPathCollisionException>(
            () => CompareAsync(
                fixture.AnalysisDirectories,
                output,
                overwrite: true));

        Assert.Equal(before, await File.ReadAllBytesAsync(first));
        Assert.Equal(before, await File.ReadAllBytesAsync(second));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(first));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(second));
    }

    [Fact]
    public async Task ExistingOutputReportReparsePointIsRejected()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        Dictionary<string, (byte[] Bytes, DateTime Timestamp)> before =
            SnapshotInputs(fixture.AnalysisDirectories);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        string target = workspace.PathFor("linked-output-target");
        Directory.CreateDirectory(target);
        string link = Path.Combine(output, "campaign-summary.json");
        Assert.True(
            CampaignTestAssertions.TryCreateDirectoryLink(link, target),
            "The deterministic output reparse-point fixture could not be created.");

        try
        {
            await Assert.ThrowsAsync<CampaignPathCollisionException>(
                () => CompareAsync(
                    fixture.AnalysisDirectories,
                    output,
                    overwrite: true));
            AssertInputsUnchanged(before);
            Assert.True(Directory.Exists(link));
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }

    private static Task<CampaignComparisonResult> CompareAsync(
        IReadOnlyList<string> directories,
        string output,
        bool overwrite) =>
        new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                directories,
                output,
                overwrite));

    private static Dictionary<string, (byte[] Bytes, DateTime Timestamp)>
        SnapshotInputs(IEnumerable<string> directories) =>
        directories
            .SelectMany(Directory.GetFiles)
            .ToDictionary(
                path => path,
                path => (
                    File.ReadAllBytes(path),
                    File.GetLastWriteTimeUtc(path)),
                StringComparer.OrdinalIgnoreCase);

    private static void AssertInputsUnchanged(
        IReadOnlyDictionary<string, (byte[] Bytes, DateTime Timestamp)> before)
    {
        foreach ((string path, (byte[] bytes, DateTime timestamp)) in before)
        {
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
    }
}
