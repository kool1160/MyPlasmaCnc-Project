namespace MyPlasm.Inspector.ProtocolAnalysis;

internal sealed record DistributionReport(
    long Count,
    double? Minimum,
    double? Median,
    double? P95,
    double? P99,
    double? Mean,
    double? Maximum);

internal sealed record SequenceReport(
    ulong First,
    ulong Last,
    long GapCount,
    ulong MissingSequenceCount);

internal sealed record CallLengthReport(
    DistributionReport WriteRequested,
    DistributionReport WriteActual,
    DistributionReport ReadRequested,
    DistributionReport ReadActual);

internal sealed record TransactionSummaryReport(
    long Matched,
    long ConfirmedByRule,
    long UnmatchedWrites,
    long UnexpectedReads,
    long PairingUncertainties,
    DistributionReport LatencyMicroseconds,
    DistributionReport QueuePollsPerTransaction,
    DistributionReport CadenceMicroseconds);

internal sealed record AnomalyReport(
    long FailedOpens,
    long FailedWrites,
    long FailedReads,
    long FailedCloses,
    long RedundantCloses,
    long UnclosedHandles,
    long SuccessfulWritesWithoutOpenHandle,
    long SuccessfulReadsWithoutOpenHandle);

internal sealed record ConfigurationReport(
    ulong Sequence,
    DateTimeOffset TimestampUtc,
    string Function,
    IReadOnlyDictionary<string, long> Values);

internal sealed record HandleSessionReport(
    string SessionLabel,
    ulong OpenSequence,
    DateTimeOffset OpenTimestampUtc,
    uint OpenStatus,
    ulong? CloseSequence,
    DateTimeOffset? CloseTimestampUtc,
    uint? CloseStatus,
    bool IsClosed,
    long QueuePollCount,
    long? QueuePollDurationMicroseconds,
    IReadOnlyList<ConfigurationReport> Configuration,
    IReadOnlyDictionary<string, long> PayloadClassCounts,
    IReadOnlyDictionary<string, long> TransactionClassCounts);

internal sealed record SessionClassFrequencyReport(
    string ClassKind,
    string ClassId,
    IReadOnlyDictionary<string, long> CountsBySession);

internal sealed record CaptureSummaryReport(
    string ToolVersion,
    int RecorderSchemaVersion,
    string InputFileName,
    string InputSha256,
    long RecordCount,
    int RecorderSessionCount,
    SequenceReport Sequence,
    DateTimeOffset FirstTimestampUtc,
    DateTimeOffset LastTimestampUtc,
    IReadOnlyDictionary<string, long> FunctionCounts,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> StatusCounts,
    IReadOnlyDictionary<string, DistributionReport> FunctionCadenceMicroseconds,
    CallLengthReport ByteCountDistributions,
    TransactionSummaryReport Transactions,
    AnomalyReport Anomalies,
    IReadOnlyList<HandleSessionReport> HandleSessions,
    IReadOnlyList<SessionClassFrequencyReport> SessionClassFrequencyComparison);

internal sealed record PayloadClassReport(
    string ClassId,
    string Direction,
    int Length,
    string Sha256,
    long Count,
    ulong FirstSequence,
    ulong LastSequence);

internal sealed record BytePositionReport(
    int Index,
    int UniqueValueCount,
    double EntropyBits);

internal sealed record PayloadFamilyReport(
    string FamilyId,
    string Direction,
    int Length,
    long SampleCount,
    int FixedPrefixLength,
    int FixedSuffixLength,
    IReadOnlyList<BytePositionReport> Positions);

internal sealed record PayloadVariabilityReport(
    string Classification,
    string Rule,
    IReadOnlyList<PayloadClassReport> ExactPayloadClasses,
    IReadOnlyList<PayloadFamilyReport> SameLengthFamilies);

internal sealed record PhaseRow(
    string Classification,
    string Phase,
    string SessionLabel,
    ulong StartSequence,
    ulong EndSequence,
    long StartElapsedMicroseconds,
    long EndElapsedMicroseconds,
    uint? Status,
    string EvidenceRule);

internal sealed record TransactionClassReport(
    string ClassId,
    string WriteClassId,
    string ReadClassId,
    long Count,
    ulong FirstWriteSequence,
    ulong LastReadSequence,
    DistributionReport LatencyMicroseconds,
    DistributionReport QueuePolls);

internal sealed record ReportBundle(
    CaptureSummaryReport Summary,
    PayloadVariabilityReport PayloadVariability,
    IReadOnlyList<PhaseRow> Phases,
    IReadOnlyList<TransactionClassReport> TransactionClasses);
