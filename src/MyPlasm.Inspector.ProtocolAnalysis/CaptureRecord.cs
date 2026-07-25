namespace MyPlasm.Inspector.ProtocolAnalysis;

internal sealed record CaptureRecord(
    int SchemaVersion,
    Guid SessionId,
    DateTimeOffset TimestampUtc,
    long ElapsedMicroseconds,
    uint ProcessId,
    uint ThreadId,
    string Function,
    ulong Sequence,
    ulong HandleId,
    uint Status,
    string FlushTrigger,
    uint? RequestedCount,
    uint? ActualCount,
    byte[]? Payload,
    uint? QueueCount,
    uint? DeviceCount,
    IReadOnlyDictionary<string, long> Settings);
