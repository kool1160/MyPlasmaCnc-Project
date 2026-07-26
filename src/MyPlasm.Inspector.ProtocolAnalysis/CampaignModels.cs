namespace MyPlasm.Inspector.ProtocolAnalysis;

internal sealed record CampaignAnalysisSet(
    string CanonicalFingerprint,
    string AnalyzerToolVersion,
    int RecorderSchemaVersion,
    string InputCaptureSha256,
    IReadOnlyDictionary<string, string> ReportHashes,
    CampaignRunStructure Structure,
    IReadOnlyDictionary<string, CampaignTransactionClass> TransactionClasses,
    IReadOnlyDictionary<string, CampaignVariabilityFamily> VariabilityFamilies,
    IReadOnlyList<CampaignPhase> Phases,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>
        TransactionClassCountsBySession);

internal sealed record CampaignRunStructure(
    long RecordCount,
    IReadOnlyDictionary<string, long> FunctionCounts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> StatusCounts,
    IReadOnlyDictionary<string, long> PhaseCounts,
    int OpenSessionCount,
    long MatchedPairs,
    long UnmatchedWrites,
    long UnexpectedReads,
    long RedundantCloses,
    long FailedCloses,
    long UnclosedHandles,
    long ReconnectTransitions,
    DistributionReport TransactionLatencyMicroseconds,
    DistributionReport TransactionCadenceMicroseconds,
    IReadOnlyDictionary<string, DistributionReport> FunctionCadenceMicroseconds);

internal sealed record CampaignTransactionClass(
    string ClassId,
    string WriteClassId,
    string ReadClassId,
    int WriteLength,
    int ReadLength,
    long Count,
    ulong FirstWriteSequence,
    ulong LastReadSequence,
    DistributionReport LatencyMicroseconds,
    DistributionReport QueuePolls);

internal sealed record CampaignVariabilityFamily(
    string FamilyId,
    string Direction,
    int Length,
    long SampleCount,
    int FixedPrefixLength,
    int FixedSuffixLength,
    IReadOnlyList<BytePositionReport> Positions);

internal sealed record CampaignPhase(
    string Phase,
    string SessionLabel,
    ulong StartSequence,
    ulong EndSequence);

internal sealed record CampaignRunReport(
    string RunLabel,
    string AnalysisSetSha256,
    string AnalyzerToolVersion,
    int RecorderSchemaVersion,
    string InputCaptureSha256,
    IReadOnlyDictionary<string, string> ReportSha256,
    long RecordCount,
    IReadOnlyDictionary<string, long> FunctionCounts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> StatusCounts,
    IReadOnlyDictionary<string, long> PhaseCounts,
    int OpenSessionCount,
    long MatchedPairCount,
    long UnmatchedWriteCount,
    long UnexpectedReadCount,
    long RedundantCloseCount,
    long FailedCloseCount,
    long UnclosedHandleCount,
    long ReconnectTransitionCount,
    DistributionReport TransactionLatencyMicroseconds,
    DistributionReport TransactionCadenceMicroseconds,
    IReadOnlyDictionary<string, DistributionReport> FunctionCadenceMicroseconds);

internal sealed record CampaignClassRunReport(
    string RunLabel,
    long Count,
    ulong? FirstWriteSequence,
    ulong? LastReadSequence,
    IReadOnlyDictionary<string, long> CountsBySession,
    IReadOnlyList<string> OverlappingPhases,
    DistributionReport? LatencyMicroseconds,
    DistributionReport? QueuePolls);

internal sealed record CampaignTransactionClassReport(
    string ClassId,
    string WriteClassId,
    string ReadClassId,
    int WriteLength,
    int ReadLength,
    int PresentInRuns,
    string Presence,
    string Classification,
    bool StableAcrossAllThreeCaptures,
    string StabilityRule,
    IReadOnlyList<CampaignClassRunReport> Runs);

internal sealed record CampaignVariabilityRunReport(
    string RunLabel,
    bool Present,
    long SampleCount,
    int? FixedPrefixLength,
    int? FixedSuffixLength,
    IReadOnlyList<BytePositionReport> Positions);

internal sealed record CampaignVariabilityFamilyReport(
    string FamilyId,
    string Direction,
    int Length,
    int PresentInRuns,
    bool MetricsEqualAcrossPresentRuns,
    IReadOnlyList<CampaignVariabilityRunReport> Runs);

internal sealed record CampaignClassificationReport(
    string Confirmed,
    string Hypothesis,
    string Unknown);

internal sealed record CampaignSummaryReport(
    string ToolVersion,
    int ComparisonSchemaVersion,
    int RunCount,
    string CanonicalRunOrderingRule,
    IReadOnlyList<CampaignRunReport> Runs,
    IReadOnlyList<CampaignTransactionClassReport> TransactionClasses,
    IReadOnlyList<CampaignVariabilityFamilyReport> VariabilityFamilies,
    CampaignClassificationReport ClassificationDiscipline);

internal sealed record CampaignReportBundle(
    CampaignSummaryReport Summary,
    IReadOnlyList<CampaignTransactionClassReport> TransactionClasses,
    IReadOnlyList<CampaignVariabilityFamilyReport> VariabilityFamilies);
