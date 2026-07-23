namespace MyPlasm.Inspector.Transport.D2xx;

public sealed class PassiveCaptureService : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumDuration = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly PassiveD2xxSession _session;
    private readonly IPassiveCaptureClock _clock;
    private CancellationTokenSource? _captureCancellation;
    private Task<PassiveCaptureResult>? _activeCapture;
    private PassiveCaptureResult? _lastCapture;
    private bool _disposed;

    public PassiveCaptureService(PassiveD2xxSession session, IPassiveCaptureClock? clock = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _clock = clock ?? new SystemPassiveCaptureClock();
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

    public PassiveCaptureResult? LastCapture => _lastCapture;

    public Task<PassiveCaptureResult> StartAsync(
        TimeSpan? duration = null,
        IProgress<PassiveCaptureProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        TimeSpan captureDuration = duration ?? DefaultDuration;
        if (captureDuration <= TimeSpan.Zero || captureDuration > MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), $"Capture duration must be positive and at most {MaximumDuration}.");
        }

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeCapture is { IsCompleted: false })
            {
                throw new InvalidOperationException("A passive capture is already running.");
            }

            _captureCancellation?.Dispose();
            _captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            CancellationToken token = _captureCancellation.Token;
            _activeCapture = Task.Run(() => CaptureLoopAsync(captureDuration, progress, token), CancellationToken.None);
            return _activeCapture;
        }
    }

    public async Task<PassiveCaptureResult?> StopAsync(string reason = "manual cancellation")
    {
        Task<PassiveCaptureResult>? capture;
        lock (_gate)
        {
            capture = _activeCapture;
            if (capture is { IsCompleted: false })
            {
                _captureCancellation?.Cancel();
            }
        }

        if (capture is null)
        {
            return _lastCapture;
        }

        PassiveCaptureResult result = await capture.ConfigureAwait(false);
        if (result.StopReason == "cancelled")
        {
            result.StopReason = reason;
        }

        return result;
    }

    public async Task<D2xxStatus?> CloseSessionAsync()
    {
        await StopAsync("closed during capture").ConfigureAwait(false);
        D2xxStatus? status = await _session.CloseAsync().ConfigureAwait(false);
        if (_lastCapture is not null)
        {
            _lastCapture.ClosedUtc = _session.ClosedUtc;
            _lastCapture.CloseStatus = status;
            _lastCapture.Events = _session.Events;
        }

        return status;
    }

    public async ValueTask DisposeAsync()
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
        _captureCancellation?.Dispose();
        _captureCancellation = null;
    }

    private async Task<PassiveCaptureResult> CaptureLoopAsync(
        TimeSpan duration,
        IProgress<PassiveCaptureProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!_session.IsOpen)
        {
            throw new InvalidOperationException("Open the exact passive D2XX session before capture.");
        }

        DateTimeOffset started = _clock.UtcNow;
        List<PassiveCaptureChunk> chunks = [];
        string stopReason = "duration elapsed";
        byte[] buffer = new byte[4096];

        try
        {
            while (_clock.UtcNow - started < duration)
            {
                cancellationToken.ThrowIfCancellationRequested();
                D2xxStatus queueStatus = _session.PollQueueStatus(out uint depth);
                if (queueStatus != D2xxStatus.Ok)
                {
                    _session.RecordDisconnect($"Queue polling stopped with {queueStatus}.", queueStatus);
                    stopReason = "queue-status error";
                    break;
                }

                if (depth == 0)
                {
                    progress?.Report(CreateProgress(started, chunks, depth, queueStatus));
                    await _clock.DelayAsync(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                uint request = Math.Min(depth, (uint)buffer.Length);
                PassiveReadResult read = _session.Read(depth, buffer, request);
                if (read.ErrorMessage is not null)
                {
                    if (read.Status != D2xxStatus.Ok)
                    {
                        _session.RecordDisconnect(read.ErrorMessage, read.Status);
                    }

                    stopReason = "read error";
                    break;
                }

                chunks.Add(new PassiveCaptureChunk(_clock.UtcNow, depth, request, read.ReturnedCount, read.Status, read.Bytes));
                progress?.Report(CreateProgress(started, chunks, depth, read.Status));

                // The native calls are already on a worker. A short asynchronous yield
                // also prevents a continuously queued device from becoming a tight loop.
                await _clock.DelayAsync(TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopReason = "cancelled";
            _session.RecordCancellation("Passive capture cancellation requested.");
        }

        PassiveCaptureResult result = new(
            started,
            _clock.UtcNow,
            stopReason,
            chunks,
            _session.Events,
            _session.SelectedDevice,
            _session.DriverVersion)
        {
            OpenedUtc = _session.OpenedUtc
        };
        _lastCapture = result;
        return result;
    }

    private PassiveCaptureProgress CreateProgress(
        DateTimeOffset started,
        IReadOnlyList<PassiveCaptureChunk> chunks,
        uint queueDepth,
        D2xxStatus status) =>
        new(
            _clock.UtcNow - started,
            chunks.Sum(item => item.Bytes.Length),
            chunks.Count,
            queueDepth,
            status);
}

public sealed record PassiveCaptureProgress(
    TimeSpan Elapsed,
    int TotalBytes,
    int ChunkCount,
    uint QueueDepth,
    D2xxStatus LastStatus);
