using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class CampaignDuplicateEvidenceTests
{
    [Fact]
    public async Task TwoByteIdenticalCompleteAnalysisSetsAreRejected()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string copy = CampaignTestAssertions.CopyAnalysisDirectory(
            workspace,
            fixture.AnalysisDirectories[0],
            "analysis-copy");
        string output = workspace.PathFor("comparison");

        CampaignInputValidationException exception =
            await Assert.ThrowsAsync<CampaignInputValidationException>(
                () => new CampaignComparator().CompareAsync(
                    new CampaignComparisonRequest(
                        [
                            fixture.AnalysisDirectories[0],
                            copy,
                            fixture.AnalysisDirectories[2]
                        ],
                        output)));
        Assert.Contains(
            "Duplicate complete sanitized analysis sets",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task ThreeByteIdenticalCompleteAnalysisSetsAreRejected()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string copyOne = CampaignTestAssertions.CopyAnalysisDirectory(
            workspace,
            fixture.AnalysisDirectories[0],
            "analysis-copy-1");
        string copyTwo = CampaignTestAssertions.CopyAnalysisDirectory(
            workspace,
            fixture.AnalysisDirectories[0],
            "analysis-copy-2");

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => new CampaignComparator().CompareAsync(
                new CampaignComparisonRequest(
                    [
                        fixture.AnalysisDirectories[0],
                        copyOne,
                        copyTwo
                    ],
                    workspace.PathFor("comparison"))));
    }

    [Fact]
    public async Task ThreeStructurallyDistinctAnalysisSetsContinueToPass()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");

        CampaignComparisonResult result =
            await new CampaignComparator().CompareAsync(
                new CampaignComparisonRequest(
                    fixture.AnalysisDirectories,
                    output));

        Assert.Equal(3, result.RunCount);
        await CampaignTestAssertions.AssertCompleteVerifiedSetAsync(output);
    }
}
