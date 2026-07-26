using System.Text;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class CampaignValidationTests
{
    [Theory]
    [InlineData("capture-summary.json")]
    [InlineData("capture-report.md")]
    [InlineData("phase-timeline.csv")]
    [InlineData("transaction-classes.csv")]
    [InlineData("payload-variability.json")]
    [InlineData("hashes.sha256")]
    public async Task MissingRequiredReportFailsClosed(string fileName)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        File.Delete(Path.Combine(fixture.AnalysisDirectories[0], fileName));

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(workspace, fixture.AnalysisDirectories));
        Assert.False(Directory.Exists(workspace.PathFor("comparison")));
    }

    [Theory]
    [InlineData("traffic.jsonl")]
    [InlineData("private-capture.zip")]
    [InlineData("vendor.dll")]
    [InlineData("payload.bin")]
    public async Task ExtraRawArchiveBinaryOrPayloadFileIsRejected(
        string fileName)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        await File.WriteAllTextAsync(
            Path.Combine(fixture.AnalysisDirectories[0], fileName),
            "synthetic prohibited extra");

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(workspace, fixture.AnalysisDirectories));
    }

    [Fact]
    public async Task ManifestHashMismatchFailsBeforeOutputCreation()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        await File.AppendAllTextAsync(
            Path.Combine(
                fixture.AnalysisDirectories[0],
                "capture-summary.json"),
            "\n");

        CampaignInputValidationException exception =
            await Assert.ThrowsAsync<CampaignInputValidationException>(
                () => CompareAsync(workspace, fixture.AnalysisDirectories));
        Assert.Contains("SHA-256", exception.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(workspace.PathFor("comparison")));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task MalformedUnknownOrDuplicateManifestEntriesAreRejected(
        string kind)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string manifestPath = Path.Combine(
            fixture.AnalysisDirectories[0],
            "hashes.sha256");
        string manifest = await File.ReadAllTextAsync(manifestPath);
        string firstLine = manifest.Split(
            '\n',
            StringSplitOptions.RemoveEmptyEntries)[0];
        string replacement = kind switch
        {
            "malformed" => "not-a-sha256  capture-report.md\n",
            "unknown" =>
                $"{new string('A', 64)}  raw-payload.bin\n{manifest}",
            _ => $"{firstLine}\n{manifest}"
        };
        await File.WriteAllTextAsync(
            manifestPath,
            replacement,
            new UTF8Encoding(false));

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(workspace, fixture.AnalysisDirectories));
        Assert.False(Directory.Exists(workspace.PathFor("comparison")));
    }

    [Fact]
    public async Task MalformedJsonAndCsvAreRejectedAfterManifestVerification()
    {
        using TestWorkspace jsonWorkspace = new();
        SyntheticCampaignFixture jsonFixture =
            await SyntheticAnalysisCampaign.CreateAsync(jsonWorkspace);
        await File.WriteAllTextAsync(
            Path.Combine(
                jsonFixture.AnalysisDirectories[0],
                "capture-summary.json"),
            "{\"broken\":\n",
            new UTF8Encoding(false));
        await SyntheticAnalysisCampaign.RefreshManifestEntryAsync(
            jsonFixture.AnalysisDirectories[0],
            "capture-summary.json");
        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(jsonWorkspace, jsonFixture.AnalysisDirectories));

        using TestWorkspace csvWorkspace = new();
        SyntheticCampaignFixture csvFixture =
            await SyntheticAnalysisCampaign.CreateAsync(csvWorkspace);
        await File.WriteAllTextAsync(
            Path.Combine(
                csvFixture.AnalysisDirectories[0],
                "transaction-classes.csv"),
            "wrong,header\n",
            new UTF8Encoding(false));
        await SyntheticAnalysisCampaign.RefreshManifestEntryAsync(
            csvFixture.AnalysisDirectories[0],
            "transaction-classes.csv");
        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(csvWorkspace, csvFixture.AnalysisDirectories));
    }

    [Theory]
    [InlineData("tool_version", "9.9.9")]
    [InlineData("recorder_schema_version", 2)]
    public async Task IncompatibleAnalyzerVersionOrSchemaIsRejected(
        string property,
        object value)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        await SyntheticAnalysisCampaign.RewriteJsonAndManifestAsync(
            fixture.AnalysisDirectories[0],
            "capture-summary.json",
            root => root[property] = value switch
            {
                int number => number,
                string text => text,
                _ => throw new InvalidOperationException()
            });

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(workspace, fixture.AnalysisDirectories));
    }

    [Fact]
    public async Task MissingAnalysisDirectoryIsRejectedAsInvalidEvidence()
    {
        using TestWorkspace workspace = new();
        string[] missing =
        [
            workspace.PathFor("missing-1"),
            workspace.PathFor("missing-2"),
            workspace.PathFor("missing-3")
        ];

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(workspace, missing));
        Assert.False(Directory.Exists(workspace.PathFor("comparison")));
    }

    [Fact]
    public async Task DuplicateNormalizedAndLinkResolvedInputsAreRejected()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string normalizedAlias = Path.Combine(
            fixture.AnalysisDirectories[0],
            ".");
        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(
                workspace,
                [
                    fixture.AnalysisDirectories[0],
                    normalizedAlias,
                    fixture.AnalysisDirectories[2]
                ]));

        string link = workspace.PathFor("analysis-link");
        try
        {
            Directory.CreateSymbolicLink(
                link,
                fixture.AnalysisDirectories[0]);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => CompareAsync(
                workspace,
                [
                    fixture.AnalysisDirectories[0],
                    link,
                    fixture.AnalysisDirectories[2]
                ]));
    }

    [Theory]
    [InlineData("same")]
    [InlineData("child")]
    [InlineData("normalized")]
    [InlineData("parent")]
    public async Task InputOutputDirectoryOverlapIsRejected(string kind)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = kind switch
        {
            "same" => fixture.AnalysisDirectories[0],
            "child" => Path.Combine(
                fixture.AnalysisDirectories[0],
                "comparison"),
            "normalized" => Path.Combine(
                fixture.AnalysisDirectories[0],
                "unused",
                ".."),
            _ => workspace.Root
        };

        await Assert.ThrowsAsync<CampaignPathCollisionException>(
            () => new CampaignComparator().CompareAsync(
                new CampaignComparisonRequest(
                    fixture.AnalysisDirectories,
                    output,
                    Overwrite: true)));
    }

    [Fact]
    public async Task LinkResolvedInputOutputCollisionIsRejected()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string link = workspace.PathFor("output-link");
        try
        {
            Directory.CreateSymbolicLink(
                link,
                fixture.AnalysisDirectories[1]);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                PlatformNotSupportedException)
        {
            return;
        }

        await Assert.ThrowsAsync<CampaignPathCollisionException>(
            () => new CampaignComparator().CompareAsync(
                new CampaignComparisonRequest(
                    fixture.AnalysisDirectories,
                    link,
                    Overwrite: true)));
    }

    [Fact]
    public async Task ComparisonNeverChangesInputBytesOrTimestamps()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        Dictionary<string, (byte[] Bytes, DateTime Timestamp)> before =
            fixture.AnalysisDirectories
                .SelectMany(Directory.GetFiles)
                .ToDictionary(
                    path => path,
                    path => (
                        File.ReadAllBytes(path),
                        File.GetLastWriteTimeUtc(path)),
                    StringComparer.OrdinalIgnoreCase);

        await CompareAsync(workspace, fixture.AnalysisDirectories);

        foreach ((string path, (byte[] bytes, DateTime timestamp)) in before)
        {
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(path));
        }
    }

    private static Task<CampaignComparisonResult> CompareAsync(
        TestWorkspace workspace,
        IReadOnlyList<string> directories) =>
        new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                directories,
                workspace.PathFor("comparison")));
}
