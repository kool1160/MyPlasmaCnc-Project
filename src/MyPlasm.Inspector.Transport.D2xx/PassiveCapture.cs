using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Transport.D2xx;

public sealed record PassiveSessionEvent(
    DateTimeOffset TimestampUtc,
    string Operation,
    uint QueueDepth,
    uint RequestedCount,
    uint ReturnedCount,
    D2xxStatus Status,
    TimeSpan ElapsedSessionTime,
    string? ErrorMessage = null);

public sealed record PassiveCaptureChunk(
    DateTimeOffset TimestampUtc,
    uint QueueDepth,
    uint RequestedCount,
    uint ReturnedCount,
    D2xxStatus Status,
    byte[] Bytes);

public sealed class PassiveCaptureResult
{
    internal PassiveCaptureResult(
        DateTimeOffset startedUtc,
        DateTimeOffset stoppedUtc,
        string stopReason,
        IReadOnlyList<PassiveCaptureChunk> chunks,
        IReadOnlyList<PassiveSessionEvent> events,
        FtdiDeviceInfo selectedDevice,
        string? driverVersion)
    {
        StartedUtc = startedUtc;
        StoppedUtc = stoppedUtc;
        StopReason = stopReason;
        Chunks = chunks;
        Events = events;
        SelectedDevice = selectedDevice;
        DriverVersion = driverVersion;
    }

    public DateTimeOffset StartedUtc { get; }

    public DateTimeOffset StoppedUtc { get; }

    public DateTimeOffset? OpenedUtc { get; internal set; }

    public DateTimeOffset? ClosedUtc { get; internal set; }

    public string StopReason { get; internal set; }

    public IReadOnlyList<PassiveCaptureChunk> Chunks { get; }

    public IReadOnlyList<PassiveSessionEvent> Events { get; internal set; }

    public FtdiDeviceInfo SelectedDevice { get; }

    public string? DriverVersion { get; }

    public D2xxStatus? CloseStatus { get; internal set; }

    public int TotalBytes => Chunks.Sum(chunk => chunk.Bytes.Length);

    public int QueuePollCount => Events.Count(item => item.Operation == PassiveOperations.QueuePoll);

    public IReadOnlyList<string> Errors => Events
        .Where(item => !string.IsNullOrWhiteSpace(item.ErrorMessage))
        .Select(item => item.ErrorMessage!)
        .ToArray();
}

public static class PassiveOperations
{
    public const string Open = "open";
    public const string Metadata = "metadata";
    public const string QueuePoll = "queue-poll";
    public const string Read = "read";
    public const string Cancellation = "cancellation";
    public const string Error = "error";
    public const string Disconnect = "disconnect";
    public const string Close = "close";
}
