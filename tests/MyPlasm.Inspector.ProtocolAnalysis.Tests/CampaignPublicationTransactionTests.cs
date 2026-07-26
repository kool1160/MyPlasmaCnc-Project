using System.Text;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class CampaignPublicationTransactionTests
{
    [Theory]
    [InlineData("campaign-summary.json")]
    [InlineData("campaign-report.md")]
    [InlineData("stable-transaction-classes.csv")]
    [InlineData("class-frequency-by-run.csv")]
    [InlineData("run-structure-comparison.csv")]
    public async Task FailureAfterStagingAnyDataReportLeavesNoPartialSet(
        string reportName)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        CampaignTestHook hook = ThrowAt(
            CampaignPublicationCheckpoint.AfterStagedReport,
            reportName);

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => CompareAsync(fixture, output, hook, overwrite: false));
        AssertNoKnownReports(output);
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    [Theory]
    [InlineData("manifest")]
    [InlineData("verification")]
    public async Task FailureAfterManifestOrVerificationLeavesNoPartialSet(
        string point)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        CampaignPublicationCheckpoint checkpoint = point == "manifest"
            ? CampaignPublicationCheckpoint.AfterStagedManifest
            : CampaignPublicationCheckpoint.AfterStagedSetVerification;
        CampaignTestHook hook = ThrowAt(
            checkpoint,
            checkpoint == CampaignPublicationCheckpoint.AfterStagedManifest
                ? "hashes.sha256"
                : null);

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => CompareAsync(fixture, output, hook, overwrite: false));
        AssertNoKnownReports(output);
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    [Theory]
    [InlineData("campaign-summary.json")]
    [InlineData("campaign-report.md")]
    [InlineData("stable-transaction-classes.csv")]
    [InlineData("class-frequency-by-run.csv")]
    [InlineData("run-structure-comparison.csv")]
    [InlineData("hashes.sha256")]
    public async Task FailureAfterBackingUpAnyReportRestoresCompletePriorSet(
        string reportName)
    {
        await AssertPriorSetRestoredAsync(
            CampaignPublicationCheckpoint.AfterBackedUpReport,
            reportName);
    }

    [Theory]
    [InlineData("campaign-summary.json")]
    [InlineData("class-frequency-by-run.csv")]
    [InlineData("run-structure-comparison.csv")]
    [InlineData("hashes.sha256")]
    public async Task FailureAfterPublishingSelectedReportsRestoresPriorSet(
        string reportName)
    {
        await AssertPriorSetRestoredAsync(
            CampaignPublicationCheckpoint.AfterPublishedReport,
            reportName);
    }

    [Fact]
    public async Task FailureAfterFirstPublishWithoutPriorSetLeavesNoPartialSet()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => CompareAsync(
                fixture,
                output,
                ThrowAt(
                    CampaignPublicationCheckpoint.AfterPublishedReport,
                    "campaign-summary.json"),
                overwrite: false));

        AssertNoKnownReports(output);
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    [Fact]
    public async Task FailureRestoresIncompleteOldSetAndUnrelatedFile()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, "campaign-summary.json"),
            "old summary");
        await File.WriteAllTextAsync(
            Path.Combine(output, "campaign-report.md"),
            "old report");
        await File.WriteAllTextAsync(
            Path.Combine(output, "unrelated.txt"),
            "preserve");
        Dictionary<string, byte[]> prior =
            CampaignTestAssertions.SnapshotKnownReports(output);

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => CompareAsync(
                fixture,
                output,
                ThrowAt(
                    CampaignPublicationCheckpoint.AfterPublishedReport,
                    "class-frequency-by-run.csv"),
                overwrite: true));

        CampaignTestAssertions.AssertKnownReportsEqual(output, prior);
        Assert.Equal(
            "preserve",
            await File.ReadAllTextAsync(
                Path.Combine(output, "unrelated.txt")));
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    [Theory]
    [InlineData("campaign-summary.json")]
    [InlineData("campaign-report.md")]
    [InlineData("stable-transaction-classes.csv")]
    [InlineData("class-frequency-by-run.csv")]
    [InlineData("run-structure-comparison.csv")]
    [InlineData("hashes.sha256")]
    public async Task RollbackContinuesAfterInjectedRestorationFailure(
        string rollbackReportName)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        await new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                fixture.AnalysisDirectories,
                output));
        Dictionary<string, byte[]> prior =
            CampaignTestAssertions.SnapshotKnownReports(output);
        string unrelated = Path.Combine(output, "operator-notes.txt");
        await File.WriteAllTextAsync(
            unrelated,
            "preserve this unrelated evidence",
            new UTF8Encoding(false));
        CampaignTestHook hook = new()
        {
            PublicationAction = (checkpoint, name) =>
            {
                if (checkpoint ==
                        CampaignPublicationCheckpoint.AfterPublishedReport &&
                    name == "class-frequency-by-run.csv")
                {
                    throw new InvalidOperationException(
                        "Injected publication failure.");
                }

                if (checkpoint ==
                        CampaignPublicationCheckpoint
                            .AfterRollbackRestoredReport &&
                    name == rollbackReportName)
                {
                    throw new InvalidOperationException(
                        "Injected rollback checkpoint failure.");
                }
            }
        };

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => CompareAsync(fixture, output, hook, overwrite: true));
        CampaignTestAssertions.AssertKnownReportsEqual(output, prior);
        Assert.Equal(
            "preserve this unrelated evidence",
            await File.ReadAllTextAsync(unrelated));
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    [Fact]
    public async Task OverwriteReplacesValidSetAndPreservesUnrelatedFiles()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        await new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                fixture.AnalysisDirectories,
                output));
        byte[] priorSummary = await File.ReadAllBytesAsync(
            Path.Combine(output, "campaign-summary.json"));
        string unrelated = Path.Combine(output, "operator-notes.txt");
        await File.WriteAllTextAsync(
            unrelated,
            "unrelated",
            new UTF8Encoding(false));
        await SyntheticAnalysisCampaign.RewriteJsonAndManifestAsync(
            fixture.AnalysisDirectories[0],
            "capture-summary.json",
            root => root["input_sha256"] = new string('A', 64));

        await new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                fixture.AnalysisDirectories,
                output,
                Overwrite: true));

        byte[] currentSummary = await File.ReadAllBytesAsync(
            Path.Combine(output, "campaign-summary.json"));
        Assert.False(priorSummary.SequenceEqual(currentSummary));
        Assert.Equal("unrelated", await File.ReadAllTextAsync(unrelated));
        await CampaignTestAssertions.AssertCompleteVerifiedSetAsync(output);
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    [Fact]
    public async Task OverwriteSafelyReplacesIncompleteKnownSet()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(
            Path.Combine(output, "campaign-summary.json"),
            "incomplete old report",
            new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(output, "unrelated.txt"),
            "keep",
            new UTF8Encoding(false));

        await new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                fixture.AnalysisDirectories,
                output,
                Overwrite: true));

        await CampaignTestAssertions.AssertCompleteVerifiedSetAsync(output);
        Assert.Equal(
            "keep",
            await File.ReadAllTextAsync(
                Path.Combine(output, "unrelated.txt")));
    }

    [Fact]
    public async Task NonemptyOutputIsRefusedWithoutOverwrite()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        Directory.CreateDirectory(output);
        string unrelated = Path.Combine(output, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "keep");

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => new CampaignComparator().CompareAsync(
                new CampaignComparisonRequest(
                    fixture.AnalysisDirectories,
                    output)));
        Assert.Equal("keep", await File.ReadAllTextAsync(unrelated));
        AssertNoKnownReports(output);
    }

    private static async Task AssertPriorSetRestoredAsync(
        CampaignPublicationCheckpoint checkpoint,
        string reportName)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");
        await new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                fixture.AnalysisDirectories,
                output));
        Dictionary<string, byte[]> prior =
            CampaignTestAssertions.SnapshotKnownReports(output);
        string unrelated = Path.Combine(output, "unrelated.txt");
        await File.WriteAllTextAsync(unrelated, "preserve");

        await Assert.ThrowsAsync<AnalysisOutputException>(
            () => CompareAsync(
                fixture,
                output,
                ThrowAt(checkpoint, reportName),
                overwrite: true));

        CampaignTestAssertions.AssertKnownReportsEqual(output, prior);
        Assert.Equal("preserve", await File.ReadAllTextAsync(unrelated));
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    private static CampaignTestHook ThrowAt(
        CampaignPublicationCheckpoint checkpoint,
        string? reportName) =>
        new()
        {
            PublicationAction = (actualCheckpoint, actualName) =>
            {
                if (actualCheckpoint == checkpoint &&
                    actualName == reportName)
                {
                    throw new InvalidOperationException(
                        $"Injected failure after {checkpoint}: {reportName}");
                }
            }
        };

    private static Task<CampaignComparisonResult> CompareAsync(
        SyntheticCampaignFixture fixture,
        string output,
        CampaignTestHook hook,
        bool overwrite) =>
        new CampaignComparator(hook).CompareAsync(
            new CampaignComparisonRequest(
                fixture.AnalysisDirectories,
                output,
                overwrite));

    private static void AssertNoKnownReports(string output)
    {
        if (!Directory.Exists(output))
        {
            return;
        }

        Assert.DoesNotContain(
            CampaignTestAssertions.ReportNames,
            name => File.Exists(Path.Combine(output, name)));
    }
}
