using System.Text;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class CampaignInputStabilityTests
{
    [Theory]
    [InlineData("mutate")]
    [InlineData("replace")]
    [InlineData("delete")]
    public async Task PostHashFileInstabilityIsDetectedBeforeOutput(
        string mode)
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string target = Path.Combine(
            fixture.AnalysisDirectories[0],
            "capture-report.md");
        string replacement = workspace.PathFor("replacement.md");
        await File.WriteAllTextAsync(
            replacement,
            "# replacement synthetic report\n",
            new UTF8Encoding(false));
        bool invoked = false;
        CampaignTestHook hook = new()
        {
            InputAction = (checkpoint, directory) =>
            {
                if (invoked ||
                    checkpoint != CampaignInputCheckpoint.AfterInitialHashes ||
                    directory != fixture.AnalysisDirectories[0])
                {
                    return;
                }

                invoked = true;
                switch (mode)
                {
                    case "mutate":
                        File.AppendAllText(target, "\nchanged");
                        break;
                    case "replace":
                        File.Move(replacement, target, overwrite: true);
                        break;
                    case "delete":
                        File.Delete(target);
                        break;
                }
            }
        };
        string output = workspace.PathFor("comparison");

        await Assert.ThrowsAsync<CampaignInputValidationException>(
            () => new CampaignComparator(hook).CompareAsync(
                new CampaignComparisonRequest(
                    fixture.AnalysisDirectories,
                    output)));
        Assert.True(invoked);
        Assert.False(Directory.Exists(output));
        CampaignTestAssertions.AssertNoTransactionArtifacts(
            workspace.Root,
            "comparison");
    }

    [Fact]
    public async Task RetargetedAnalysisDirectoryLinkIsDetectedBeforeOutput()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string link = workspace.PathFor("analysis-link");
        Assert.True(
            CampaignTestAssertions.TryCreateDirectoryLink(
                link,
                fixture.AnalysisDirectories[0]),
            "The deterministic directory-link fixture could not be created.");

        bool invoked = false;
        CampaignTestHook hook = new()
        {
            InputAction = (checkpoint, directory) =>
            {
                if (invoked ||
                    checkpoint != CampaignInputCheckpoint.AfterInitialHashes ||
                    directory != CampaignPathSafety.Normalize(link))
                {
                    return;
                }

                invoked = true;
                Directory.Delete(link);
                Assert.True(
                    CampaignTestAssertions.TryCreateDirectoryLink(
                        link,
                        fixture.AnalysisDirectories[1]));
            }
        };
        string output = workspace.PathFor("comparison");

        try
        {
            await Assert.ThrowsAsync<CampaignInputValidationException>(
                () => new CampaignComparator(hook).CompareAsync(
                    new CampaignComparisonRequest(
                        [
                            link,
                            fixture.AnalysisDirectories[1],
                            fixture.AnalysisDirectories[2]
                        ],
                        output)));
            Assert.True(invoked);
            Assert.False(Directory.Exists(output));
            CampaignTestAssertions.AssertNoTransactionArtifacts(
                workspace.Root,
                "comparison");
        }
        finally
        {
            if (Directory.Exists(link))
            {
                Directory.Delete(link);
            }
        }
    }
}
