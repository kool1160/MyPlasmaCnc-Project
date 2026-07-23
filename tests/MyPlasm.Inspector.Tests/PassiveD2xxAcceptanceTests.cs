using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.D2xx;

namespace MyPlasm.Inspector.Tests;

public sealed class PassiveD2xxAcceptanceTests
{
    [Fact]
    public async Task EnumerationNeverCallsOpen()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using D2xxInspectionTransport transport = new(native);

        await transport.EnumerateDevicesAsync();

        Assert.Equal(0, native.OpenCalls);
    }

    [Fact]
    public async Task UnrelatedFtdiDeviceIsRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(Device(description: "Unrelated FTDI"));
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();

        Assert.Throws<InvalidOperationException>(
            () => transport.CreatePassiveSession(new FixedProcessDetector(false)));
    }

    [Fact]
    public async Task ZeroExactCandidatesAreRejected()
    {
        ScriptedNativeApi native = NativeWithDevices();
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();

        Assert.Throws<InvalidOperationException>(
            () => transport.CreatePassiveSession(new FixedProcessDetector(false)));
    }

    [Fact]
    public async Task MultipleExactCandidatesAreRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice("ONE", 1), ExactDevice("TWO", 2));
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();

        Assert.Throws<InvalidOperationException>(
            () => transport.CreatePassiveSession(new FixedProcessDetector(false)));
    }

    [Fact]
    public async Task MissingSerialIsRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice("", 1));
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();

        Assert.Throws<InvalidOperationException>(
            () => transport.CreatePassiveSession(new FixedProcessDetector(false)));
    }

    [Fact]
    public async Task DuplicateSerialIsRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(
            ExactDevice("DUP", 1),
            Device("DUP", 2, "Other"));
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();

        Assert.Throws<InvalidOperationException>(
            () => transport.CreatePassiveSession(new FixedProcessDetector(false)));
    }

    [Fact]
    public async Task DuplicateLocationIsRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(
            ExactDevice("ONE", 44),
            Device("TWO", 44, "Other"));
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();

        Assert.Throws<InvalidOperationException>(
            () => transport.CreatePassiveSession(new FixedProcessDetector(false)));
    }

    [Fact]
    public async Task AlreadyOpenDeviceIsRejected()
    {
        D2xxNativeDeviceInfo device = ExactDevice() with { Flags = 1 };
        ScriptedNativeApi native = NativeWithDevices(device);
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();

        Assert.Throws<InvalidOperationException>(
            () => transport.CreatePassiveSession(new FixedProcessDetector(false)));
        Assert.Equal(0, native.OpenCalls);
    }

    [Fact]
    public async Task OriginalMyPlasmProcessBlocksOpen()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();
        PassiveD2xxSession session = transport.CreatePassiveSession(new FixedProcessDetector(true));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync().AsTask());

        Assert.Equal(0, native.OpenCalls);
    }

    [Fact]
    public async Task OnlyExactEnumeratedSerialReachesOpenEx()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice("EXACT-SERIAL", 3));
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();
        PassiveD2xxSession session = transport.CreatePassiveSession(new FixedProcessDetector(false));

        await session.OpenAsync();

        Assert.Equal(["EXACT-SERIAL"], native.OpenSerials);
        Assert.True(transport.IsOpen);
    }

    [Fact]
    public async Task DriverVersionIsQueriedOnlyAfterSuccessfulOpen()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.OpenStatus = D2xxStatus.DeviceNotFound;
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();
        PassiveD2xxSession session = transport.CreatePassiveSession(new FixedProcessDetector(false));

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync().AsTask());

        Assert.Equal(0, native.DriverVersionCalls);
    }

    [Fact]
    public async Task FailedOpenDoesNotRetainHandle()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.OpenStatus = D2xxStatus.IoError;
        PassiveD2xxSession session = Session(native);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync().AsTask());

        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task SecondOpenIsRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using PassiveD2xxSession session = Session(native);
        await session.OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.OpenAsync().AsTask());

        Assert.Equal(1, native.OpenCalls);
    }

    [Fact]
    public async Task IsOpenReflectsActualHandleState()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using PassiveD2xxSession session = Session(native);
        Assert.False(session.IsOpen);

        await session.OpenAsync();
        Assert.True(session.IsOpen);

        Assert.Equal(D2xxStatus.Ok, await session.CloseAsync());
        Assert.False(session.IsOpen);
    }

    [Fact]
    public async Task OpenAndCloseAreRecordedAsEvents()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using PassiveD2xxSession session = Session(native);
        await session.OpenAsync();
        await session.CloseAsync();

        Assert.Contains(session.Events, item => item.Operation == PassiveOperations.Open);
        Assert.Contains(session.Events, item => item.Operation == PassiveOperations.Close);
        Assert.Contains(session.Events, item => item.Operation == PassiveOperations.Metadata);
    }

    [Fact]
    public async Task EnumerationCannotReplaceOpenSessionState()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using D2xxInspectionTransport transport = new(native);
        await transport.EnumerateDevicesAsync();
        PassiveD2xxSession session = transport.CreatePassiveSession(new FixedProcessDetector(false));
        await session.OpenAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => transport.EnumerateDevicesAsync().AsTask());
    }

    [Fact]
    public async Task QueueStatusIsPolled()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.QueueResults.Enqueue((D2xxStatus.Ok, 0));
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        await service.StartAsync(TimeSpan.FromMilliseconds(50));

        Assert.True(native.QueueCalls > 0);
    }

    [Fact]
    public async Task ZeroQueueDepthNeverCallsRead()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        PassiveCaptureResult result = await service.StartAsync(TimeSpan.FromMilliseconds(150));

        Assert.Equal(0, native.ReadCalls);
        Assert.Equal(0, result.TotalBytes);
    }

    [Fact]
    public async Task ZeroQueuePollsArePreservedAsEvents()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        PassiveCaptureResult result = await service.StartAsync(TimeSpan.FromMilliseconds(150));

        Assert.Contains(result.Events, item =>
            item.Operation == PassiveOperations.QueuePoll && item.QueueDepth == 0);
    }

    [Fact]
    public async Task ReadBytesArePreservedExactly()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.QueueResults.Enqueue((D2xxStatus.Ok, 4));
        native.ReadResults.Enqueue((D2xxStatus.Ok, new byte[] { 0, 255, 17, 99 }, null));
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        PassiveCaptureResult result = await service.StartAsync(TimeSpan.FromMilliseconds(1));

        Assert.Equal(new byte[] { 0, 255, 17, 99 }, Assert.Single(result.Chunks).Bytes);
    }

    [Fact]
    public async Task ChunkBoundariesAndOrderingArePreserved()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.QueueResults.Enqueue((D2xxStatus.Ok, 2));
        native.QueueResults.Enqueue((D2xxStatus.Ok, 3));
        native.ReadResults.Enqueue((D2xxStatus.Ok, new byte[] { 1, 2 }, null));
        native.ReadResults.Enqueue((D2xxStatus.Ok, new byte[] { 3, 4, 5 }, null));
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        PassiveCaptureResult result = await service.StartAsync(TimeSpan.FromMilliseconds(2));

        Assert.Equal(2, result.Chunks.Count);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5 }, result.Chunks.SelectMany(item => item.Bytes));
    }

    [Fact]
    public async Task InvalidReturnedCountStopsSafelyAndPreservesEarlierBytes()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.QueueResults.Enqueue((D2xxStatus.Ok, 2));
        native.QueueResults.Enqueue((D2xxStatus.Ok, 2));
        native.ReadResults.Enqueue((D2xxStatus.Ok, new byte[] { 7, 8 }, null));
        native.ReadResults.Enqueue((D2xxStatus.Ok, [], 3));
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        PassiveCaptureResult result = await service.StartAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(new byte[] { 7, 8 }, result.Chunks.SelectMany(item => item.Bytes));
        Assert.Equal("read error", result.StopReason);
        Assert.Contains(result.Events, item =>
            item.Operation == PassiveOperations.Error &&
            item.ErrorMessage!.Contains("invalid count", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RequestedCountGreaterThanBufferIsRejectedBeforeNativeRead()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using PassiveD2xxSession session = Session(native);
        await session.OpenAsync();

        PassiveReadResult result = session.Read(8, new byte[4], 5);

        Assert.NotNull(result.ErrorMessage);
        Assert.Equal(0, native.ReadCalls);
    }

    [Fact]
    public async Task ManualCancellationReturnsValidResult()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        BlockingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);
        Task<PassiveCaptureResult> capture = service.StartAsync(TimeSpan.FromMinutes(1));
        await clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        PassiveCaptureResult? stopped = await service.StopAsync();

        Assert.NotNull(stopped);
        Assert.Equal("manual cancellation", stopped.StopReason);
        Assert.Same(stopped, await capture);
    }

    [Fact]
    public async Task ZeroByteCaptureIsValid()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        PassiveCaptureResult result = await service.StartAsync(TimeSpan.FromMilliseconds(1));

        Assert.Equal(0, result.TotalBytes);
        Assert.Empty(result.Chunks);
    }

    [Fact]
    public async Task MultipleSimultaneousCapturesAreRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        BlockingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);
        _ = service.StartAsync(TimeSpan.FromMinutes(1));
        await clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = service.StartAsync(TimeSpan.FromSeconds(1));
        });
    }

    [Fact]
    public async Task StopCancelsAndAwaitsCapture()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        BlockingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);
        Task<PassiveCaptureResult> active = service.StartAsync(TimeSpan.FromMinutes(1));
        await clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.StopAsync();

        Assert.True(active.IsCompletedSuccessfully);
        Assert.False(service.IsCapturing);
    }

    [Fact]
    public async Task CloseCancelsCaptureBeforeNativeClose()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        BlockingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);
        _ = service.StartAsync(TimeSpan.FromMinutes(1));
        await clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.CloseSessionAsync();

        int cancelEvent = session.Events.ToList().FindIndex(item => item.Operation == PassiveOperations.Cancellation);
        int closeEvent = session.Events.ToList().FindIndex(item => item.Operation == PassiveOperations.Close);
        Assert.InRange(cancelEvent, 0, int.MaxValue);
        Assert.True(closeEvent > cancelEvent);
    }

    [Fact]
    public async Task CloseFailureIsRecordedAndDoesNotClaimClosed()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.CloseStatus = D2xxStatus.IoError;
        await using PassiveD2xxSession session = Session(native);
        await session.OpenAsync();

        D2xxStatus? status = await session.CloseAsync();

        Assert.Equal(D2xxStatus.IoError, status);
        Assert.True(session.IsOpen);
        Assert.Null(session.ClosedUtc);
        Assert.Contains(session.Events, item =>
            item.Operation == PassiveOperations.Close && item.Status == D2xxStatus.IoError);
    }

    [Fact]
    public async Task ReadDisconnectDoesNotCrashCapture()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.QueueResults.Enqueue((D2xxStatus.Ok, 4));
        native.ReadResults.Enqueue((D2xxStatus.IoError, [], 0));
        AdvancingClock clock = new();
        await using PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session, clock);

        PassiveCaptureResult result = await service.StartAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("read error", result.StopReason);
        Assert.Contains(result.Events, item => item.Operation == PassiveOperations.Disconnect);
    }

    [Fact]
    public async Task DisposalClosesExactlyOnce()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        PassiveD2xxSession session = Session(native);
        await session.OpenAsync();

        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task WindowStyleDisposalCancelsCaptureThenClosesExactlyOnce()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        BlockingClock clock = new();
        PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        PassiveCaptureService service = new(session, clock);
        _ = service.StartAsync(TimeSpan.FromMinutes(1));
        await clock.DelayEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.Equal(1, native.CloseCalls);
        int cancelEvent = session.Events.ToList().FindIndex(item => item.Operation == PassiveOperations.Cancellation);
        int closeEvent = session.Events.ToList().FindIndex(item => item.Operation == PassiveOperations.Close);
        Assert.True(closeEvent > cancelEvent);
    }

    [Fact]
    public async Task FailedCloseAndDisposeStillAttemptNativeCloseExactlyOnce()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.CloseStatus = D2xxStatus.IoError;
        PassiveD2xxSession session = Session(native);
        await session.OpenAsync();

        await session.CloseAsync();
        await session.DisposeAsync();

        Assert.Equal(1, native.CloseCalls);
    }

    [Fact]
    public async Task CaptureDurationAboveFiveMinutesIsRejected()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        await using PassiveD2xxSession session = Session(native);
        await session.OpenAsync();
        await using PassiveCaptureService service = new(session);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = service.StartAsync(TimeSpan.FromMinutes(5) + TimeSpan.FromMilliseconds(1));
        });
    }

    [Fact]
    public async Task ZeroByteCaptureExportsEveryRequiredFileAndMetadata()
    {
        using TemporaryDirectory temporary = new();
        PassiveCaptureResult capture = await CreateZeroByteClosedCaptureAsync();
        string startup = Path.Combine(temporary.Path, "source-startup.log");
        await File.WriteAllTextAsync(startup, "startup evidence");
        CaptureExportContext context = ExportContext(startup);

        CaptureExportResult result = new CaptureExporter().Export(capture, context, temporary.Path);

        string[] required =
        [
            "session.json", "events.jsonl", "rx.bin", "rx-hex.txt",
            "report.txt", "hashes.sha256", "startup.log"
        ];
        Assert.All(required, name => Assert.True(File.Exists(Path.Combine(result.CaptureDirectory, name)), name));
        using JsonDocument session = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(result.CaptureDirectory, "session.json")));
        string[] metadata =
        [
            "applicationName", "applicationVersion", "processArchitecture", "osVersion",
            "runtimeVersion", "renderingMode", "d2xxDllPath", "dllPeArchitecture",
            "dllFileVersion", "dllSha256", "d2xxLibraryVersion", "ftdiDriverVersion",
            "selectedDevice", "serialNumber", "description", "deviceType", "vendorId",
            "productId", "locationId", "openTimestampUtc", "captureStartTimestampUtc",
            "captureStopTimestampUtc", "closeTimestampUtc", "stopReason", "duration",
            "totalBytes", "readChunkCount", "queuePollCount", "d2xxErrors",
            "transmitCount", "productionAllowlistCount"
        ];
        Assert.All(metadata, name => Assert.True(session.RootElement.TryGetProperty(name, out _), name));
        Assert.Equal(0, new FileInfo(Path.Combine(result.CaptureDirectory, "rx.bin")).Length);
    }

    [Fact]
    public async Task ExportZipFilesMatchManifestHashes()
    {
        using TemporaryDirectory temporary = new();
        PassiveCaptureResult capture = await CreateZeroByteClosedCaptureAsync();
        string startup = Path.Combine(temporary.Path, "startup.log");
        await File.WriteAllTextAsync(startup, "startup evidence");
        CaptureExportResult result = new CaptureExporter().Export(capture, ExportContext(startup), temporary.Path);
        using ZipArchive archive = ZipFile.OpenRead(result.ZipPath);
        ZipArchiveEntry manifestEntry = Assert.Single(archive.Entries, item => item.FullName == "hashes.sha256");
        using StreamReader reader = new(manifestEntry.Open());
        string[] lines = (await reader.ReadToEndAsync())
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            string expectedHash = line[..64];
            string name = line[66..];
            ZipArchiveEntry entry = Assert.Single(archive.Entries, item => item.FullName == name);
            using Stream stream = entry.Open();
            Assert.Equal(expectedHash, Convert.ToHexString(await SHA256.HashDataAsync(stream)));
        }
    }

    [Fact]
    public async Task ExportFailureRetainsCaptureDirectoryAndRawBytes()
    {
        using TemporaryDirectory temporary = new();
        PassiveCaptureResult capture = await CreateSingleChunkClosedCaptureAsync();
        string startup = Path.Combine(temporary.Path, "startup.log");
        await File.WriteAllTextAsync(startup, "startup evidence");
        CaptureExporter exporter = new(new ThrowingArchiveWriter());

        Assert.Throws<IOException>(
            () => exporter.Export(capture, ExportContext(startup), temporary.Path));

        string captureDirectory = Assert.Single(
            Directory.GetDirectories(temporary.Path, "capture-*", SearchOption.TopDirectoryOnly));
        Assert.Equal(
            new byte[] { 9, 8, 7 },
            await File.ReadAllBytesAsync(Path.Combine(captureDirectory, "rx.bin")));
    }

    private static async Task<PassiveCaptureResult> CreateZeroByteClosedCaptureAsync()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        AdvancingClock clock = new();
        PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        PassiveCaptureService service = new(session, clock);
        PassiveCaptureResult capture = await service.StartAsync(TimeSpan.FromMilliseconds(1));
        await service.CloseSessionAsync();
        await service.DisposeAsync();
        return capture;
    }

    private static async Task<PassiveCaptureResult> CreateSingleChunkClosedCaptureAsync()
    {
        ScriptedNativeApi native = NativeWithDevices(ExactDevice());
        native.QueueResults.Enqueue((D2xxStatus.Ok, 3));
        native.ReadResults.Enqueue((D2xxStatus.Ok, new byte[] { 9, 8, 7 }, null));
        AdvancingClock clock = new();
        PassiveD2xxSession session = Session(native, clock);
        await session.OpenAsync();
        PassiveCaptureService service = new(session, clock);
        PassiveCaptureResult capture = await service.StartAsync(TimeSpan.FromMilliseconds(1));
        await service.CloseSessionAsync();
        await service.DisposeAsync();
        return capture;
    }

    private static CaptureExportContext ExportContext(string startupPath) =>
        new(
            "MyPlasm Inspector",
            "1.0.0",
            "X86",
            "Windows test",
            ".NET 8 test",
            "Software",
            "native/ftd2xx.dll",
            new PeInspectionResult(
                "native/ftd2xx.dll",
                PeArchitecture.X86,
                "3.01.19",
                new string('A', 64),
                System.Runtime.InteropServices.Architecture.X86,
                true),
            "3.01.19",
            startupPath);

    private static PassiveD2xxSession Session(
        ScriptedNativeApi native,
        IPassiveCaptureClock? clock = null) =>
        new(
            native,
            new FtdiDeviceInfo(0, FtdiDeviceInfo.MyPlasmDescription, "EXACT", "FT_DEVICE_232R (5)", 0x0403, 0x6001, false, 7, 0x04036001),
            new FixedProcessDetector(false),
            clock);

    private static ScriptedNativeApi NativeWithDevices(params D2xxNativeDeviceInfo[] devices) =>
        new(devices);

    private static D2xxNativeDeviceInfo ExactDevice(string serial = "EXACT", uint location = 7) =>
        Device(serial, location, FtdiDeviceInfo.MyPlasmDescription);

    private static D2xxNativeDeviceInfo Device(
        string serial = "OTHER",
        uint location = 8,
        string description = "Other FTDI") =>
        new(0, 5, 0x04036001, location, serial, description);

    private sealed class FixedProcessDetector(bool running) : IOriginalMyPlasmProcessDetector
    {
        public bool IsRunning() => running;
    }

    private sealed class AdvancingClock : IPassiveCaptureClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UtcNow += delay;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingClock : IPassiveCaptureClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

        public TaskCompletionSource DelayEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            DelayEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ScriptedNativeApi(D2xxNativeDeviceInfo[] devices) : ID2xxNativeApi
    {
        public Queue<(D2xxStatus Status, uint Depth)> QueueResults { get; } = new();

        public Queue<(D2xxStatus Status, byte[] Bytes, uint? ReturnedOverride)> ReadResults { get; } = new();

        public List<string> OpenSerials { get; } = [];

        public D2xxStatus OpenStatus { get; set; } = D2xxStatus.Ok;

        public D2xxStatus CloseStatus { get; set; } = D2xxStatus.Ok;

        public int OpenCalls { get; private set; }

        public int CloseCalls { get; private set; }

        public int DriverVersionCalls { get; private set; }

        public int QueueCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public D2xxStatus GetLibraryVersion(out uint version)
        {
            version = 0x00030113;
            return D2xxStatus.Ok;
        }

        public D2xxStatus CreateDeviceInfoList(out uint deviceCount)
        {
            deviceCount = checked((uint)devices.Length);
            return D2xxStatus.Ok;
        }

        public D2xxStatus GetDeviceInfoList(D2xxNativeDeviceInfo[] destination, ref uint deviceCount)
        {
            Array.Copy(devices, destination, Math.Min(devices.Length, destination.Length));
            deviceCount = checked((uint)Math.Min(devices.Length, destination.Length));
            return D2xxStatus.Ok;
        }

        public D2xxStatus OpenExBySerialNumber(string serialNumber, out nint handle)
        {
            OpenCalls++;
            OpenSerials.Add(serialNumber);
            handle = OpenStatus == D2xxStatus.Ok ? 123 : 0;
            return OpenStatus;
        }

        public D2xxStatus Close(nint handle)
        {
            CloseCalls++;
            return CloseStatus;
        }

        public D2xxStatus GetDriverVersion(nint handle, out uint version)
        {
            DriverVersionCalls++;
            version = 0x00030113;
            return D2xxStatus.Ok;
        }

        public D2xxStatus GetQueueStatus(nint handle, out uint bytesAvailable)
        {
            QueueCalls++;
            (D2xxStatus status, uint depth) = QueueResults.Count > 0
                ? QueueResults.Dequeue()
                : (D2xxStatus.Ok, 0);
            bytesAvailable = depth;
            return status;
        }

        public D2xxStatus Read(nint handle, byte[] buffer, uint requestedCount, out uint returnedCount)
        {
            ReadCalls++;
            (D2xxStatus status, byte[] bytes, uint? returnedOverride) = ReadResults.Count > 0
                ? ReadResults.Dequeue()
                : (D2xxStatus.Ok, [], 0);
            int copyCount = Math.Min(bytes.Length, Math.Min(buffer.Length, checked((int)requestedCount)));
            bytes.AsSpan(0, copyCount).CopyTo(buffer);
            returnedCount = returnedOverride ?? checked((uint)bytes.Length);
            return status;
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"myplasm-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }

    private sealed class ThrowingArchiveWriter : ICaptureArchiveWriter
    {
        public void CreateFromDirectory(string sourceDirectory, string destinationZipPath) =>
            throw new IOException("Synthetic archive failure.");
    }
}
