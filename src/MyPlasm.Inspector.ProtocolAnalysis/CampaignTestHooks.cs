namespace MyPlasm.Inspector.ProtocolAnalysis;

internal enum CampaignInputCheckpoint
{
    AfterInitialHashes
}

internal enum CampaignPublicationCheckpoint
{
    AfterStagedReport,
    AfterStagedManifest,
    AfterStagedSetVerification,
    AfterBackedUpReport,
    AfterPublishedReport,
    AfterRollbackRestoredReport
}

internal interface ICampaignComparisonTestHook
{
    void OnInputCheckpoint(
        CampaignInputCheckpoint checkpoint,
        string analysisDirectory);

    void OnPublicationCheckpoint(
        CampaignPublicationCheckpoint checkpoint,
        string? reportFileName);
}
