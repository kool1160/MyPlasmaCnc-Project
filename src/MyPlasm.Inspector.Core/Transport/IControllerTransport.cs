namespace MyPlasm.Inspector.Core.Transport;

public interface IControllerTransport : IAsyncDisposable
{
    bool IsOpen { get; }

    ValueTask<IReadOnlyList<FtdiDeviceInfo>> EnumerateDevicesAsync(
        CancellationToken cancellationToken = default);

    ValueTask OpenAsync(string serialNumber, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ValidatedCommand command, CancellationToken cancellationToken = default);
}
