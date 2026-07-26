namespace MyPlasm.Inspector.Transport.D2xx;

public sealed class PassiveCaptureService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);
    public const long MaximumCaptureBytes = 64L * 1024L * 1024L;
    public const int MaximumCaptureEvents = 100_000;
    public const int MaximumCaptureChunks = 16_384;
    public const int ReceiveBufferSize = 4096;

    private readonly object _gate = new();
    private readonly SemaphoreSlim _disposeGate = new(1, 1);
    private readonly PassiveD2xxSession _session;
    private readonly IPassiveCaptureClock _clock;
    private readonly long _maximumCaptureBytes;
    private readonly int _maximumCaptureEvents;
    private readonly int _maximumCaptureChunks;
    private readonly int _receiveBufferSize;
    private CancellationTokenSource? _captureCancellation;
    private Task<PassiveCaptureResult>? _activeCapture;
    private PassiveCaptureResult? _lastCapture;
    private string? _requestedStopReason;
    private bool _disposed;

    public PassiveCaptureService(PassiveD2xxSession session)
        : this(session, null)
    {
    }

    internal PassiveCaptureService(
        PassiveD2xxSession session,
        IPassiveCaptureClock? clock,
        long maximumCaptureBytes = MaximumCaptureBytes,
        int maximumCaptureEvents = MaximumCaptureEvents,
        int maximumCaptureChunks = MaximumCaptureChunks,
        int receiveBufferSize = ReceiveBufferSize)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? new SystemPassiveCaptureClock();
        if (maximumCaptureBytes <= 0 ||
            maximumCaptureEvents < 4 ||
            maximumCaptureChunks <= 0 ||
            receiveBufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCaptureBytes),
                "Passive capture safety limits must be positive.");
        }

        _maximumCaptureBytes = maximumCaptureBytes;
        _maximumCaptureEvents = maximumCaptureEvents;
        _maximumCaptureChunks = maximumCaptureChunks;
        _receiveBufferSize = receiveBufferSize;
    }

    public bool IsCapturing
    {
        get
        {
            lock (_gate)
            {
                return _activeCapture is { IsCompleted: false };
            }
        }
    }

    public PassiveCaptureResult? LastCapture
    {
        get
        {
            lock (_gate)
            {
                return _lastCapture;
            }
        }
    }

    public Task<PassiveCaptureResult> StartAsync(
        TimeSpan? duration = null,
        IProgress<PassiveCaptureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan captureDuration = duration ?? DefaultDuration;
        if (captureDuration <= TimeSpan.Zero ||
            captureDuration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                $"Capture duration must be positive and at most {MaximumDuration}.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_session.IsOpen)
            {
                throw new InvalidOperationException(
                    "Open the exact passive D2XX session before capture.");
            }

            if (_activeCapture is { IsCompleted: false })
            {
                throw new InvalidOperationException(
                    "A passive capture is already running.");
            }

            _captureCancellation?.Dispose();
            _captureCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _requestedStopReason = null;
            CancellationToken token = _captureCancellation.Token;
            _activeCapture = Task.Run(
                () => CaptureLoopAsync(captureDuration, progress, token),
                CancellationToken.None);
            return _activeCapture;
        }
    }

    public async Task<PassiveCaptureResult?> StopAsync(
        string reason = "manual cancellation")
    {
        Task<PassiveCaptureResult>? capture;
        lock (_gate)
        {
            capture = _activeCapture;
            if (capture is { IsCompleted: false })
            {
                _requestedStopReason ??= reason;
                _captureCancellation?.Cancel();
            }
        }

        return capture is null
            ? LastCapture
            : await capture.ConfigureAwait(false);
    }

    public async Task<D2xxStatus?> CloseSessionAsync()
    {
        await StopAsync("closed during capture").ConfigureAwait(false);
        D2xxStatus? status = await _session.CloseAsync().ConfigureAwait(false);
        lock (_gate)
        {
            if (_lastCapture is not null)
            {
                _lastCapture.ClosedUtc = _session.ClosedUtc;
                _lastCapture.CloseStatus = status;
                _lastCapture.CloseFailure = _session.CloseFailure;
                _lastCapture.Events = _session.Events;
            }
        }

        return status;
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            await StopAsync("window disposal").ConfigureAwait(false);
            await CloseSessionAsync().ConfigureAwait(false);
            await _session.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _captureCancellation?.Dispose();
                _captureCancellation = null;
            }
        }
        finally
        {
            _disposeGate.Release();
        }
    }

    private async Task<PassiveCaptureResult> CaptureLoopAsync(
        TimeSpan duration,
        IProgress<PassiveCaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        DateTimeOffset startedUtc = _clock.UtcNow;
        TimeSpan startedElapsed = _clock.Elapsed;
        int initialEventCount = _session.EventCount;
        List<PassiveCaptureChunk> chunks = [];
        long totalBytes = 0;
        string stopReason = "duration elapsed";
        byte[] buffer = new byte[_receiveBufferSize];
        bool progressAvailable = progress is not null;

        try
        {
            while (_clock.Elapsed - startedElapsed < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_session.EventCount - initialEventCount >=
                    _maximumCaptureEvents - 2)
                {
                    stopReason = "event safety limit reached";
                    _session.RecordLimit(
                        $"Capture stopped at the event limit of {_maximumCaptureEvents}.");
                    break;
                }

                D2xxStatus queueStatus =
                    _session.PollQueueStatus(out uint depth);
                if (queueStatus != D2xxStatus.Ok)
                {
                    _session.RecordDisconnect(
                        $"Queue polling stopped with {queueStatus}.",
                        queueStatus);
                    stopReason = "queue-status error";
                    break;
                }

                if (depth == 0)
                {
                    progressAvailable = ReportProgressNoThrow(
                        progressAvailable ? progress : null,
                        CreateProgress(
                            startedElapsed,
                            totalBytes,
                            chunks.Count,
                            depth,
                            queueStatus));
                    await _clock.DelayAsync(
                        TimeSpan.FromMilliseconds(100),
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (chunks.Count >= _maximumCaptureChunks)
                {
                    stopReason = "chunk safety limit reached";
                    _session.RecordLimit(
                        $"Capture stopped at the chunk limit of {_maximumCaptureChunks}.");
                    break;
                }

                long remainingBytes = _maximumCaptureBytes - totalBytes;
                if (remainingBytes <= 0)
                {
                    stopReason = "byte safety limit reached";
                    _session.RecordLimit(
                        $"Capture stopped at the byte limit of {_maximumCaptureBytes}.");
                    break;
                }

                uint request = Math.Min(
                    Math.Min(depth, (uint)buffer.Length),
                    checked((uint)Math.Min(remainingBytes, uint.MaxValue)));
                PassiveReadResult read =
                    _session.Read(depth, buffer, request);
                if (read.ErrorMessage is not null)
                {
                    if (read.Status != D2xxStatus.Ok)
                    {
                        _session.RecordDisconnect(
                            read.ErrorMessage,
                            read.Status);
                    }

                    stopReason = "read error";
                    break;
                }

                if (read.Bytes.Length > 0)
                {
                    chunks.Add(
                        new PassiveCaptureChunk(
                            _clock.UtcNow,
                            depth,
                            request,
                            read.ReturnedCount,
                            read.Status,
                            read.Bytes));
                    totalBytes += read.Bytes.Length;
                }

                progressAvailable = ReportProgressNoThrow(
                    progressAvailable ? progress : null,
                    CreateProgress(
                        startedElapsed,
                        totalBytes,
                        chunks.Count,
                        depth,
                        read.Status));

                if (totalBytes >= _maximumCaptureBytes)
                {
                    stopReason = "byte safety limit reached";
                    _session.RecordLimit(
                        $"Capture stopped at the byte limit of {_maximumCaptureBytes}.");
                    break;
                }

                if (chunks.Count >= _maximumCaptureChunks)
                {
                    stopReason = "chunk safety limit reached";
                    _session.RecordLimit(
                        $"Capture stopped at the chunk limit of {_maximumCaptureChunks}.");
                    break;
                }

                await _clock.DelayAsync(
                    TimeSpan.FromMilliseconds(1),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
            {
                stopReason = _requestedStopReason ?? "cancelled";
            }

            _session.RecordCancellation(
                $"Passive capture stopped: {stopReason}.");
        }
        catch (Exception exception)
        {
            stopReason = "capture error";
            _session.RecordError(
                $"Passive capture stopped safely after {exception.GetType().Name}: " +
                exception.Message);
        }

        PassiveCaptureResult result = new(
            startedUtc,
            _clock.UtcNow,
            _clock.Elapsed - startedElapsed,
            stopReason,
            chunks,
            _session.Events,
            _session.SelectedDevice,
            _session.DriverVersion,
            _maximumCaptureBytes,
            _maximumCaptureEvents,
            _maximumCaptureChunks)
        {
            OpenedUtc = _session.OpenedUtc
        };
        lock (_gate)
        {
            _lastCapture = result;
        }

        return result;
    }

    private bool ReportProgressNoThrow(
        IProgress<PassiveCaptureProgress>? progress,
        PassiveCaptureProgress item)
    {
        if (progress is null)
        {
            return false;
        }

        try
        {
            progress.Report(item);
            return true;
        }
        catch (Exception exception)
        {
            _session.RecordError(
                $"Progress reporting was disabled after {exception.GetType().Name}: " +
                exception.Message);
            return false;
        }
    }

    private PassiveCaptureProgress CreateProgress(
        TimeSpan startedElapsed,
        long totalBytes,
        int chunkCount,
        uint queueDepth,
        D2xxStatus status) =>
        new(
            _clock.Elapsed - startedElapsed,
            totalBytes,
            chunkCount,
            queueDepth,
            status);
}

public sealed record PassiveCaptureProgress(
    TimeSpan Elapsed,
    long TotalBytes,
    int ChunkCount,
    uint QueueDepth,
    D2xxStatus LastStatus);
