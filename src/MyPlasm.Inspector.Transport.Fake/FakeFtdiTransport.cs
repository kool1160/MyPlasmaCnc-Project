using System.Collections.Concurrent;
using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Transport.Fake;

public sealed class FakeFtdiTransport : IControllerTransport
{
    private readonly IReadOnlyList<FtdiDeviceInfo> _devices;
    private readonly ConcurrentQueue<byte> _receiveBytes = new();
    private readonly List<TransmissionRecord> _transmissions = [];
    private readonly object _gate = new();
    private string? _openSerialNumber;

    public FakeFtdiTransport(IEnumerable<FtdiDeviceInfo>? devices = null)
    {
        _devices = (devices ?? CreateDefaultDevices()).ToArray();
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _openSerialNumber is not null;
            }
        }
    }

    public IReadOnlyList<TransmissionRecord> Transmissions
    {
        get
        {
            lock (_gate)
            {
                return _transmissions.ToArray();
            }
        }
    }

    public ValueTask<IReadOnlyList<FtdiDeviceInfo>> EnumerateDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? openSerialNumber;
        lock (_gate)
        {
            openSerialNumber = _openSerialNumber;
        }

        IReadOnlyList<FtdiDeviceInfo> snapshot = _devices
            .Select(device => device with
            {
                IsOpen = string.Equals(
                    device.SerialNumber,
                    openSerialNumber,
                    StringComparison.Ordinal)
            })
            .ToArray();

        return ValueTask.FromResult(snapshot);
    }

    public ValueTask OpenAsync(string serialNumber, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serialNumber);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_devices.Any(device =>
                string.Equals(device.SerialNumber, serialNumber, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Fake FTDI device '{serialNumber}' was not found.");
        }

        lock (_gate)
        {
            if (_openSerialNumber is not null)
            {
                throw new InvalidOperationException("A fake FTDI device is already open.");
            }

            _openSerialNumber = serialNumber;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _openSerialNumber = null;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        int bytesRead = 0;
        while (bytesRead < buffer.Length && _receiveBytes.TryDequeue(out byte value))
        {
            buffer.Span[bytesRead++] = value;
        }

        return ValueTask.FromResult(bytesRead);
    }

    public ValueTask WriteAsync(
        ValidatedCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOpen();

        TransmissionRecord record = new(
            DateTimeOffset.UtcNow,
            command.Name,
            command.Intent,
            command.Payload.ToArray());

        lock (_gate)
        {
            _transmissions.Add(record);
        }

        return ValueTask.CompletedTask;
    }

    public void QueueReceiveBytes(ReadOnlySpan<byte> bytes)
    {
        foreach (byte value in bytes)
        {
            _receiveBytes.Enqueue(value);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync();
    }

    private static IReadOnlyList<FtdiDeviceInfo> CreateDefaultDevices() =>
    [
        new(0, FtdiDeviceInfo.MyPlasmDescription, "FAKE-MYPLASM-001", "FT232R", 0x0403, 0x6001, false),
        new(1, "Unrelated FTDI Device", "FAKE-OTHER-001", "FT232H", 0x0403, 0x6014, false)
    ];

    private void EnsureOpen()
    {
        if (!IsOpen)
        {
            throw new InvalidOperationException("No fake FTDI device is open.");
        }
    }
}
