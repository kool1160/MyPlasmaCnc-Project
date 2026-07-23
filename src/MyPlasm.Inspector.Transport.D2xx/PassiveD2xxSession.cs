using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Transport.D2xx;

public sealed class PassiveD2xxSession : IAsyncDisposable
{
    private readonly object _eventGate = new();
    private readonly ID2xxNativeApi _nativeApi;
    private readonly IOriginalMyPlasmProcessDetector _processDetector;
    private readonly IPassiveCaptureClock _clock;
    private readonly List<PassiveSessionEvent> _events = [];
    private D2xxSafeHandle? _handle;
    private bool _disposed;

    public PassiveD2xxSession(
        ID2xxNativeApi nativeApi,
        FtdiDeviceInfo selectedDevice,
        IOriginalMyPlasmProcessDetector processDetector,
        IPassiveCaptureClock? clock = null)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        SelectedDevice = selectedDevice ?? throw new ArgumentNullException(nameof(selectedDevice));
        _processDetector = processDetector ?? throw new ArgumentNullException(nameof(processDetector));
        _clock = clock ?? new SystemPassiveCaptureClock();
        SessionStartedUtc = _clock.UtcNow;
    }

    public FtdiDeviceInfo SelectedDevice { get; }

    public DateTimeOffset SessionStartedUtc { get; }

    public DateTimeOffset? OpenedUtc { get; private set; }

    public DateTimeOffset? ClosedUtc { get; private set; }

    public string? DriverVersion { get; private set; }

    public D2xxStatus? CloseStatus => _handle?.CloseStatus;

    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };

    public IReadOnlyList<PassiveSessionEvent> Events
    {
        get
        {
            lock (_eventGate)
            {
                return _events.ToArray();
            }
        }
    }

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsOpen)
        {
            throw new InvalidOperationException("The passive D2XX session is already open.");
        }

        ValidateSelectedDevice();
        if (_processDetector.IsRunning())
        {
            throw new InvalidOperationException("Close the original MyPlasm software before opening the device.");
        }

        D2xxStatus openStatus = _nativeApi.OpenExBySerialNumber(SelectedDevice.SerialNumber, out nint rawHandle);
        AddEvent(PassiveOperations.Open, status: openStatus, error: openStatus == D2xxStatus.Ok ? null : $"FT_OpenEx returned {openStatus}.");
        if (openStatus != D2xxStatus.Ok)
        {
            return ValueTask.FromException(new InvalidOperationException($"FT_OpenEx failed: {openStatus}."));
        }

        if (rawHandle == 0)
        {
            AddEvent(PassiveOperations.Error, status: D2xxStatus.InvalidHandle, error: "FT_OpenEx returned a null handle.");
            return ValueTask.FromException(new InvalidOperationException("FT_OpenEx returned a null handle."));
        }

        _handle = new D2xxSafeHandle(_nativeApi, rawHandle);
        OpenedUtc = _clock.UtcNow;
        D2xxStatus driverStatus = _nativeApi.GetDriverVersion(rawHandle, out uint driverVersion);
        DriverVersion = driverStatus == D2xxStatus.Ok ? D2xxVersion.Format(driverVersion) : null;
        AddEvent(
            PassiveOperations.Metadata,
            status: driverStatus,
            error: driverStatus == D2xxStatus.Ok ? null : $"FT_GetDriverVersion returned {driverStatus}.");
        return ValueTask.CompletedTask;
    }

    public D2xxStatus PollQueueStatus(out uint queueDepth)
    {
        nint handle = GetHandle();
        D2xxStatus status = _nativeApi.GetQueueStatus(handle, out queueDepth);
        AddEvent(
            PassiveOperations.QueuePoll,
            queueDepth,
            status: status,
            error: status == D2xxStatus.Ok ? null : $"FT_GetQueueStatus returned {status}.");
        return status;
    }

    public PassiveReadResult Read(uint queueDepth, byte[] buffer, uint requestedCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (requestedCount > buffer.Length)
        {
            const string message = "Requested byte count exceeds the receive buffer length.";
            AddEvent(PassiveOperations.Error, queueDepth, requestedCount, status: D2xxStatus.InvalidParameter, error: message);
            return new PassiveReadResult(D2xxStatus.InvalidParameter, [], 0, message);
        }

        nint handle = GetHandle();
        D2xxStatus status = _nativeApi.Read(handle, buffer, requestedCount, out uint returnedCount);
        if (returnedCount > requestedCount || returnedCount > buffer.Length)
        {
            string message = $"FT_Read returned invalid count {returnedCount}; requested {requestedCount}, buffer {buffer.Length}.";
            AddEvent(PassiveOperations.Error, queueDepth, requestedCount, returnedCount, status, message);
            return new PassiveReadResult(status, [], returnedCount, message);
        }

        byte[] bytes = status == D2xxStatus.Ok
            ? buffer.AsSpan(0, checked((int)returnedCount)).ToArray()
            : [];
        string? error = status == D2xxStatus.Ok ? null : $"FT_Read returned {status}.";
        AddEvent(PassiveOperations.Read, queueDepth, requestedCount, returnedCount, status, error);
        return new PassiveReadResult(status, bytes, returnedCount, error);
    }

    public ValueTask<D2xxStatus?> CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_handle is null)
        {
            return ValueTask.FromResult<D2xxStatus?>(CloseStatus);
        }

        bool closed = _handle.TryClose(out D2xxStatus status);
        AddEvent(
            PassiveOperations.Close,
            status: status,
            error: closed ? null : $"FT_Close returned {status}; the session is not reported as successfully closed.");
        if (closed)
        {
            ClosedUtc = _clock.UtcNow;
            _handle.Dispose();
        }

        return ValueTask.FromResult<D2xxStatus?>(status);
    }

    public void RecordCancellation(string reason) =>
        AddEvent(PassiveOperations.Cancellation, error: reason);

    public void RecordDisconnect(string reason, D2xxStatus status) =>
        AddEvent(PassiveOperations.Disconnect, status: status, error: reason);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await CloseAsync();
        _handle?.Dispose();
        _disposed = true;
    }

    private void ValidateSelectedDevice()
    {
        if (!SelectedDevice.IsMyPlasmController)
        {
            throw new InvalidOperationException("The selected FTDI device is not an exact MyPlasm CNC candidate.");
        }

        if (string.IsNullOrWhiteSpace(SelectedDevice.SerialNumber))
        {
            throw new InvalidOperationException("The exact candidate has no serial number.");
        }

        if (SelectedDevice.IsOpen)
        {
            throw new InvalidOperationException("The exact candidate was already open during enumeration.");
        }
    }

    private nint GetHandle()
    {
        if (!IsOpen || _handle is null)
        {
            throw new InvalidOperationException("The passive D2XX session is not open.");
        }

        return _handle.DangerousGetHandle();
    }

    private void AddEvent(
        string operation,
        uint queueDepth = 0,
        uint requested = 0,
        uint returned = 0,
        D2xxStatus status = D2xxStatus.Ok,
        string? error = null)
    {
        DateTimeOffset now = _clock.UtcNow;
        PassiveSessionEvent item = new(
            now,
            operation,
            queueDepth,
            requested,
            returned,
            status,
            now - SessionStartedUtc,
            error);
        lock (_eventGate)
        {
            _events.Add(item);
        }
    }
}

public sealed record PassiveReadResult(
    D2xxStatus Status,
    byte[] Bytes,
    uint ReturnedCount,
    string? ErrorMessage);
