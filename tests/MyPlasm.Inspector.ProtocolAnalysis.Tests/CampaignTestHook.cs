using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

internal sealed class CampaignTestHook : ICampaignComparisonTestHook
{
    public Action<CampaignInputCheckpoint, string>? InputAction { get; init; }

    public Action<CampaignPublicationCheckpoint, string?>? PublicationAction
    {
        get;
        init;
    }

    public void OnInputCheckpoint(
        CampaignInputCheckpoint checkpoint,
        string analysisDirectory) =>
        InputAction?.Invoke(checkpoint, analysisDirectory);

    public void OnPublicationCheckpoint(
        CampaignPublicationCheckpoint checkpoint,
        string? reportFileName) =>
        PublicationAction?.Invoke(checkpoint, reportFileName);
}
