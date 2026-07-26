using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Transport.D2xx;

public sealed class PassiveD2xxSession : IAsyncDisposable
{
    internal const int MaximumRetainedEvents = 100_128;

    private readonly object _eventGate = new();
    private readonly object _nativeGate = new();
    private readonly ID2xxNativeApi _nativeApi;
    private readonly IOriginalMyPlasmProcessDetector _processDetector;
    private readonly IPassiveCaptureClock _clock;
    private readonly List<PassiveSessionEvent> _events = [];
    private readonly TimeSpan _sessionStartedElapsed;
    private D2xxSafeHandle? _handle;
    private D2xxStatus? _closeStatus;
    private string? _closeFailure;
    private bool _disposed;

    internal PassiveD2xxSession(
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
        _sessionStartedElapsed = _clock.Elapsed;
    }

    public FtdiDeviceInfo SelectedDevice { get; }

    public DateTimeOffset SessionStartedUtc { get; }

    public DateTimeOffset? OpenedUtc { get; private set; }

    public DateTimeOffset? ClosedUtc { get; private set; }

    public string? DriverVersion { get; private set; }

    public D2xxStatus? CloseStatus => _closeStatus;

    public string? CloseFailure => _closeFailure;

    public bool HasUnresolvedCloseFailure =>
        _closeStatus is not null && _closeStatus != D2xxStatus.Ok;

    public bool IsOpen
    {
        get
        {
            lock (_nativeGate)
            {
                return _handle is { IsInvalid: false, IsClosed: false } &&
                    !_disposed;
            }
        }
    }

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

    internal int EventCount
    {
        get
        {
            lock (_eventGate)
            {
                return _events.Count;
            }
        }
    }

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_nativeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsOpen)
            {
                throw new InvalidOperationException(
                    "The passive D2XX session is already open.");
            }

            if (HasUnresolvedCloseFailure)
            {
                throw new InvalidOperationException(
                    "The prior native close failed; reopening is refused until the process exits.");
            }

            ValidateSelectedDevice();
            if (_processDetector.IsRunning())
            {
                throw new InvalidOperationException(
                    "Close the original MyPlasm software before opening the device.");
            }

            D2xxStatus openStatus = _nativeApi.OpenExBySerialNumber(
                SelectedDevice.SerialNumber,
                out nint rawHandle);
            AddEvent(
                PassiveOperations.Open,
                status: openStatus,
                error: openStatus == D2xxStatus.Ok
                    ? null
                    : $"FT_OpenEx returned {openStatus}.");
            if (openStatus != D2xxStatus.Ok)
            {
                return ValueTask.FromException(
                    new InvalidOperationException($"FT_OpenEx failed: {openStatus}."));
            }

            if (rawHandle == 0 || rawHandle == -1)
            {
                AddEvent(
                    PassiveOperations.Error,
                    status: D2xxStatus.InvalidHandle,
                    error: "FT_OpenEx returned an invalid handle.");
                return ValueTask.FromException(
                    new InvalidOperationException("FT_OpenEx returned an invalid handle."));
            }

            _handle = new D2xxSafeHandle(_nativeApi, rawHandle);
            OpenedUtc = _clock.UtcNow;
            QueryDriverVersionNoThrow(rawHandle);
            return ValueTask.CompletedTask;
        }
    }

    internal D2xxStatus PollQueueStatus(out uint queueDepth)
    {
        lock (_nativeGate)
        {
            nint handle = GetHandleUnderLock();
            D2xxStatus status;
            try
            {
                status = _nativeApi.GetQueueStatus(handle, out queueDepth);
            }
            catch (Exception exception)
            {
                queueDepth = 0;
                status = D2xxStatus.OtherError;
                AddEvent(
                    PassiveOperations.QueuePoll,
                    status: status,
                    error: $"FT_GetQueueStatus failed: {Describe(exception)}.");
                return status;
            }

            AddEvent(
                PassiveOperations.QueuePoll,
                queueDepth,
                status: status,
                error: status == D2xxStatus.Ok
                    ? null
                    : $"FT_GetQueueStatus returned {status}.");
            return status;
        }
    }

    internal PassiveReadResult Read(
        uint queueDepth,
        byte[] buffer,
        uint requestedCount)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (requestedCount > buffer.Length)
        {
            const string message =
                "Requested byte count exceeds the receive buffer length.";
            AddEvent(
                PassiveOperations.Error,
                queueDepth,
                requestedCount,
                status: D2xxStatus.InvalidParameter,
                error: message);
            return new PassiveReadResult(
                D2xxStatus.InvalidParameter,
                [],
                0,
                message);
        }

        lock (_nativeGate)
        {
            nint handle = GetHandleUnderLock();
            D2xxStatus status;
            uint returnedCount;
            try
            {
                status = _nativeApi.Read(
                    handle,
                    buffer,
                    requestedCount,
                    out returnedCount);
            }
            catch (Exception exception)
            {
                string failure = $"FT_Read failed: {Describe(exception)}.";
                AddEvent(
                    PassiveOperations.Read,
                    queueDepth,
                    requestedCount,
                    status: D2xxStatus.OtherError,
                    error: failure);
                return new PassiveReadResult(
                    D2xxStatus.OtherError,
                    [],
                    0,
                    failure);
            }

            if (returnedCount > requestedCount || returnedCount > buffer.Length)
            {
                string message =
                    $"FT_Read returned invalid count {returnedCount}; " +
                    $"requested {requestedCount}, buffer {buffer.Length}.";
                AddEvent(
                    PassiveOperations.Error,
                    queueDepth,
                    requestedCount,
                    returnedCount,
                    status,
                    message);
                return new PassiveReadResult(status, [], returnedCount, message);
            }

            byte[] bytes = status == D2xxStatus.Ok
                ? buffer.AsSpan(0, checked((int)returnedCount)).ToArray()
                : [];
            string? error = status == D2xxStatus.Ok
                ? null
                : $"FT_Read returned {status}.";
            AddEvent(
                PassiveOperations.Read,
                queueDepth,
                requestedCount,
                returnedCount,
                status,
                error);
            return new PassiveReadResult(status, bytes, returnedCount, error);
        }
    }

    public ValueTask<D2xxStatus?> CloseAsync(
        CancellationToken cancellationToken = default)
    {
        // A close attempt is a safety cleanup and is deliberately not cancelled.
        _ = cancellationToken;
        lock (_nativeGate)
        {
            if (_handle is null)
            {
                return ValueTask.FromResult(_closeStatus);
            }

            bool closed = _handle.TryClose(out D2xxStatus status);
            _closeStatus = status;
            _closeFailure = _handle.CloseFailure;
            string? error = closed
                ? null
                : _closeFailure is null
                    ? $"FT_Close returned {status}; native closure is unconfirmed."
                    : $"FT_Close failed with {status}: {_closeFailure}; native closure is unconfirmed.";
            AddEvent(PassiveOperations.Close, status: status, error: error);
            if (closed)
            {
                ClosedUtc = _clock.UtcNow;
                _handle.Dispose();
                _handle = null;
            }

            return ValueTask.FromResult<D2xxStatus?>(status);
        }
    }

    internal void RecordCancellation(string reason) =>
        AddEvent(PassiveOperations.Cancellation, error: reason);

    internal void RecordDisconnect(string reason, D2xxStatus status) =>
        AddEvent(PassiveOperations.Disconnect, status: status, error: reason);

    internal void RecordLimit(string reason) =>
        AddEvent(PassiveOperations.Limit, error: reason);

    internal void RecordError(string reason) =>
        AddEvent(
            PassiveOperations.Error,
            status: D2xxStatus.OtherError,
            error: reason);

    public async ValueTask DisposeAsync()
    {
        lock (_nativeGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        await CloseAsync();
        lock (_nativeGate)
        {
            _handle?.Dispose();
            _handle = null;
            _disposed = true;
        }
    }

    private void QueryDriverVersionNoThrow(nint rawHandle)
    {
        try
        {
            D2xxStatus driverStatus = _nativeApi.GetDriverVersion(
                rawHandle,
                out uint driverVersion);
            DriverVersion = driverStatus == D2xxStatus.Ok
                ? D2xxVersion.Format(driverVersion)
                : null;
            AddEvent(
                PassiveOperations.Metadata,
                status: driverStatus,
                error: driverStatus == D2xxStatus.Ok
                    ? null
                    : $"FT_GetDriverVersion returned {driverStatus}.");
        }
        catch (Exception exception)
        {
            DriverVersion = null;
            AddEvent(
                PassiveOperations.Metadata,
                status: D2xxStatus.OtherError,
                error: $"FT_GetDriverVersion failed: {Describe(exception)}.");
        }
    }

    private void ValidateSelectedDevice()
    {
        if (!SelectedDevice.IsMyPlasmController)
        {
            throw new InvalidOperationException(
                "The selected FTDI device is not an exact MyPlasm CNC candidate.");
        }

        if (string.IsNullOrWhiteSpace(SelectedDevice.SerialNumber))
        {
            throw new InvalidOperationException(
                "The exact candidate has no serial number.");
        }

        if (SelectedDevice.IsOpen)
        {
            throw new InvalidOperationException(
                "The exact candidate was already open during enumeration.");
        }
    }

    private nint GetHandleUnderLock()
    {
        if (_disposed ||
            _handle is null ||
            _handle.IsInvalid ||
            _handle.IsClosed ||
            HasUnresolvedCloseFailure)
        {
            throw new InvalidOperationException(
                "The passive D2XX session is not safely open.");
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
        PassiveSessionEvent item = new(
            _clock.UtcNow,
            operation,
            queueDepth,
            requested,
            returned,
            status,
            _clock.Elapsed - _sessionStartedElapsed,
            error);
        lock (_eventGate)
        {
            if (_events.Count >= MaximumRetainedEvents)
            {
                throw new InvalidOperationException(
                    "The passive session event safety limit was exceeded.");
            }

            _events.Add(item);
        }
    }

    private static string Describe(Exception exception) =>
        $"{exception.GetType().Name}: {exception.Message}";
}

public sealed record PassiveReadResult(
    D2xxStatus Status,
    byte[] Bytes,
    uint ReturnedCount,
    string? ErrorMessage);
