namespace MyPlasm.Inspector.Transport.D2xx;

public interface IPassiveCaptureClock
{
    DateTimeOffset UtcNow { get; }

    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class SystemPassiveCaptureClock : IPassiveCaptureClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        new(Task.Delay(delay, cancellationToken));
}
