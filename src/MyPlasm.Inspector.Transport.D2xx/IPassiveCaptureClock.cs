using System.Diagnostics;

namespace MyPlasm.Inspector.Transport.D2xx;

internal interface IPassiveCaptureClock
{
    DateTimeOffset UtcNow { get; }

    TimeSpan Elapsed { get; }

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemPassiveCaptureClock : IPassiveCaptureClock
{
    private readonly long _startedTimestamp = Stopwatch.GetTimestamp();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public TimeSpan Elapsed => Stopwatch.GetElapsedTime(_startedTimestamp);

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}
