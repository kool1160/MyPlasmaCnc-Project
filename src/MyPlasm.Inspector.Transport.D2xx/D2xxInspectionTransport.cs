using System.Runtime.InteropServices;
using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Transport.D2xx;

public sealed class D2xxInspectionTransport : IControllerTransport
{
    public const string DefaultRelativeLibraryPath = "native/ftd2xx.dll";
    private const uint MaximumDeviceCount = 256;

    private readonly List<D2xxDiagnostic> _diagnostics = [];
    private readonly Architecture? _applicationArchitecture;
    private readonly PeFileInspector _peInspector;
    private readonly string? _libraryPath;
    private ID2xxNativeApi? _nativeApi;
    private bool _ownsNativeApi;
    private IReadOnlyList<FtdiDeviceInfo> _lastDevices = [];
    private bool _enumerationSucceeded;
    private nint _deviceHandle;

    public D2xxInspectionTransport(ID2xxNativeApi nativeApi)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
        _peInspector = new PeFileInspector();
    }

    public D2xxInspectionTransport(
        string libraryPath,
        PeFileInspector? peInspector = null,
        Architecture? applicationArchitecture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        _libraryPath = Path.GetFullPath(libraryPath);
        _peInspector = peInspector ?? new PeFileInspector();
        _applicationArchitecture = applicationArchitecture;
    }

    public bool IsOpen => false;

    public string? LibraryVersion { get; private set; }

    public PeInspectionResult? LibraryInspection { get; private set; }

    public IReadOnlyList<D2xxDiagnostic> Diagnostics => _diagnostics.ToArray();

    public bool HasOpenPassiveSession => _deviceHandle != 0;

    public string? DriverVersion { get; private set; }

    public static D2xxInspectionTransport CreateDefault() =>
        new(Path.Combine(AppContext.BaseDirectory, DefaultRelativeLibraryPath));

    public ValueTask<IReadOnlyList<FtdiDeviceInfo>> EnumerateDevicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _diagnostics.Clear();
        _enumerationSucceeded = false;
        _lastDevices = [];
        LibraryVersion = null;

        if (!TryEnsureNativeApi())
        {
            return ValueTask.FromResult<IReadOnlyList<FtdiDeviceInfo>>([]);
        }

        D2xxStatus versionStatus = _nativeApi!.GetLibraryVersion(out uint rawLibraryVersion);
        if (versionStatus == D2xxStatus.Ok)
        {
            LibraryVersion = D2xxVersion.Format(rawLibraryVersion);
        }
        else
        {
            AddStatusError("LIBRARY_VERSION_FAILED", "FT_GetLibraryVersion", versionStatus);
        }

        D2xxStatus countStatus = _nativeApi.CreateDeviceInfoList(out uint deviceCount);
        if (countStatus != D2xxStatus.Ok)
        {
            AddStatusError("DEVICE_LIST_CREATE_FAILED", "FT_CreateDeviceInfoList", countStatus);
            return ValueTask.FromResult<IReadOnlyList<FtdiDeviceInfo>>([]);
        }

        if (deviceCount == 0)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "NO_DEVICES",
                D2xxDiagnosticSeverity.Warning,
                "D2XX reported no devices. The driver may be absent, or no supported FTDI device is connected."));
            AddDriverVersionDiagnostic();
            return ValueTask.FromResult<IReadOnlyList<FtdiDeviceInfo>>([]);
        }

        if (deviceCount > MaximumDeviceCount)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DEVICE_COUNT_INVALID",
                D2xxDiagnosticSeverity.Error,
                $"D2XX reported {deviceCount} devices, above the safety limit of {MaximumDeviceCount}."));
            return ValueTask.FromResult<IReadOnlyList<FtdiDeviceInfo>>([]);
        }

        D2xxNativeDeviceInfo[] nativeDevices = new D2xxNativeDeviceInfo[checked((int)deviceCount)];
        uint returnedCount = deviceCount;
        D2xxStatus listStatus = _nativeApi.GetDeviceInfoList(nativeDevices, ref returnedCount);
        if (listStatus != D2xxStatus.Ok)
        {
            AddStatusError("DEVICE_LIST_READ_FAILED", "FT_GetDeviceInfoList", listStatus);
            return ValueTask.FromResult<IReadOnlyList<FtdiDeviceInfo>>([]);
        }

        int mappedCount = Math.Min(nativeDevices.Length, checked((int)returnedCount));
        List<FtdiDeviceInfo> devices = new(mappedCount);
        for (int index = 0; index < mappedCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            D2xxNativeDeviceInfo native = nativeDevices[index];
            devices.Add(MapDevice(checked((uint)index), native));
        }

        AddDuplicateDiagnostics(devices);
        AddDriverVersionDiagnostic();
        _lastDevices = devices;
        _enumerationSucceeded = true;
        return ValueTask.FromResult<IReadOnlyList<FtdiDeviceInfo>>(devices);
    }

    public ValueTask OpenExactPassiveSessionAsync(Func<bool> originalApplicationRunning, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(originalApplicationRunning);
        if (_deviceHandle != 0) throw new InvalidOperationException("An Inspector passive session is already open.");
        if (!_enumerationSucceeded) throw new InvalidOperationException("Enumerate devices successfully before opening.");
        if (originalApplicationRunning()) throw new InvalidOperationException("Close the original MyPlasm software before opening the device.");
        FtdiDeviceInfo[] candidates = _lastDevices.Where(device => device.IsMyPlasmController).ToArray();
        if (candidates.Length != 1) throw new InvalidOperationException("Exactly one MyPlasm CNC candidate is required.");
        FtdiDeviceInfo candidate = candidates[0];
        if (string.IsNullOrWhiteSpace(candidate.SerialNumber)) throw new InvalidOperationException("The exact candidate has no serial number.");
        if (candidate.IsOpen) throw new InvalidOperationException("The exact candidate is already open.");
        if (_lastDevices.GroupBy(device => device.SerialNumber, StringComparer.Ordinal).Any(group => !string.IsNullOrWhiteSpace(group.Key) && group.Count() > 1) ||
            _lastDevices.Where(device => device.LocationId.HasValue).GroupBy(device => device.LocationId).Any(group => group.Count() > 1))
            throw new InvalidOperationException("Duplicate serial number or location ambiguity prevents opening.");
        D2xxStatus status = _nativeApi!.OpenExBySerialNumber(candidate.SerialNumber, out _deviceHandle);
        if (status != D2xxStatus.Ok) { _deviceHandle = 0; throw new InvalidOperationException($"FT_OpenEx failed: {status}."); }
        status = _nativeApi.GetDriverVersion(_deviceHandle, out uint version);
        DriverVersion = status == D2xxStatus.Ok ? D2xxVersion.Format(version) : null;
        if (status != D2xxStatus.Ok) _diagnostics.Add(new D2xxDiagnostic("DRIVER_VERSION_FAILED", D2xxDiagnosticSeverity.Warning, $"FT_GetDriverVersion returned {status}."));
        return ValueTask.CompletedTask;
    }

    public async ValueTask<PassiveCaptureResult> CapturePassiveReceiveAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (_deviceHandle == 0) throw new InvalidOperationException("Open an exact passive session before capture.");
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(duration));
        DateTimeOffset started = DateTimeOffset.UtcNow;
        List<PassiveCaptureChunk> chunks = [];
        byte[] buffer = new byte[4096];
        while (DateTimeOffset.UtcNow - started < duration && !cancellationToken.IsCancellationRequested)
        {
            D2xxStatus queueStatus = _nativeApi!.GetQueueStatus(_deviceHandle, out uint depth);
            if (queueStatus != D2xxStatus.Ok) { _diagnostics.Add(new D2xxDiagnostic("QUEUE_STATUS_FAILED", D2xxDiagnosticSeverity.Error, $"FT_GetQueueStatus returned {queueStatus}.")); break; }
            if (depth == 0) { await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken); continue; }
            uint request = Math.Min(depth, (uint)buffer.Length);
            D2xxStatus readStatus = _nativeApi.Read(_deviceHandle, buffer, request, out uint returned);
            byte[] bytes = readStatus == D2xxStatus.Ok ? buffer.AsSpan(0, checked((int)returned)).ToArray() : [];
            chunks.Add(new PassiveCaptureChunk(DateTimeOffset.UtcNow, depth, request, returned, readStatus, bytes));
            if (readStatus != D2xxStatus.Ok) { _diagnostics.Add(new D2xxDiagnostic("READ_FAILED", D2xxDiagnosticSeverity.Error, $"FT_Read returned {readStatus}.")); break; }
        }
        return new PassiveCaptureResult(started, DateTimeOffset.UtcNow, chunks, Diagnostics);
    }

    public ValueTask ClosePassiveSessionAsync()
    {
        if (_deviceHandle != 0) { _nativeApi!.Close(_deviceHandle); _deviceHandle = 0; }
        return ValueTask.CompletedTask;
    }

    public ValueTask OpenAsync(string serialNumber, CancellationToken cancellationToken = default) =>
        throw EnumerationOnlyException();

    public ValueTask CloseAsync(CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;

    public ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default) =>
        throw EnumerationOnlyException();

    public ValueTask WriteAsync(
        ValidatedCommand command,
        CancellationToken cancellationToken = default) =>
        throw EnumerationOnlyException();

    public ValueTask DisposeAsync()
    {
        ClosePassiveSessionAsync();
        if (_ownsNativeApi && _nativeApi is IDisposable disposable)
        {
            disposable.Dispose();
        }

        _nativeApi = null;
        _ownsNativeApi = false;
        return ValueTask.CompletedTask;
    }

    private bool TryEnsureNativeApi()
    {
        if (_nativeApi is not null)
        {
            return true;
        }

        if (_libraryPath is null)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DLL_PATH_MISSING",
                D2xxDiagnosticSeverity.Error,
                "No local D2XX library path was configured."));
            return false;
        }

        try
        {
            LibraryInspection = _peInspector.Inspect(_libraryPath, _applicationArchitecture);
        }
        catch (FileNotFoundException)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DLL_NOT_FOUND",
                D2xxDiagnosticSeverity.Error,
                $"Vendor DLL not found. Place it at '{_libraryPath}' using the documented local-only setup."));
            return false;
        }
        catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DLL_INSPECTION_FAILED",
                D2xxDiagnosticSeverity.Error,
                $"The vendor DLL could not be inspected: {exception.Message}"));
            return false;
        }

        if (!LibraryInspection.IsLoadCompatible)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DLL_ARCHITECTURE_MISMATCH",
                D2xxDiagnosticSeverity.Error,
                $"DLL architecture {LibraryInspection.DllArchitecture} cannot load in the " +
                $"{LibraryInspection.ApplicationArchitecture} application process."));
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "WINDOWS_REQUIRED",
                D2xxDiagnosticSeverity.Error,
                "The production D2XX DLL can only be loaded by this transport on Windows."));
            return false;
        }

        try
        {
            _nativeApi = D2xxNativeLibrary.Load(LibraryInspection.FilePath);
            _ownsNativeApi = true;
            return true;
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DLL_LOAD_FAILED",
                D2xxDiagnosticSeverity.Error,
                $"The D2XX DLL or one of its required exports could not be loaded: {exception.Message}"));
            return false;
        }
    }

    private static FtdiDeviceInfo MapDevice(uint index, D2xxNativeDeviceInfo native)
    {
        ushort? vendorId = native.DeviceId == 0 ? null : checked((ushort)(native.DeviceId >> 16));
        ushort? productId = native.DeviceId == 0 ? null : checked((ushort)(native.DeviceId & 0xFFFF));

        return new FtdiDeviceInfo(
            index,
            native.Description,
            native.SerialNumber,
            FormatDeviceType(native.Type),
            vendorId,
            productId,
            (native.Flags & 0x1) != 0,
            native.LocationId == 0 ? null : native.LocationId,
            native.DeviceId == 0 ? null : native.DeviceId);
    }

    private static string FormatDeviceType(uint type) =>
        type switch
        {
            0 => "FT_DEVICE_232BM (0)",
            1 => "FT_DEVICE_232AM (1)",
            2 => "FT_DEVICE_100AX (2)",
            3 => "FT_DEVICE_UNKNOWN (3)",
            4 => "FT_DEVICE_2232C (4)",
            5 => "FT_DEVICE_232R (5)",
            6 => "FT_DEVICE_2232H (6)",
            7 => "FT_DEVICE_4232H (7)",
            8 => "FT_DEVICE_232H (8)",
            9 => "FT_DEVICE_X_SERIES (9)",
            _ => $"Unrecognized FT_DEVICE value ({type})"
        };

    private void AddStatusError(string code, string function, D2xxStatus status)
    {
        _diagnostics.Add(new D2xxDiagnostic(
            code,
            D2xxDiagnosticSeverity.Error,
            $"{function} returned {status} ({(uint)status}). The D2XX driver may be unavailable or incompatible."));
    }

    private void AddDriverVersionDiagnostic()
    {
        _diagnostics.Add(new D2xxDiagnostic(
            "DRIVER_VERSION_NOT_QUERIED",
            D2xxDiagnosticSeverity.Information,
            "Driver version was not queried because FT_GetDriverVersion requires an open device handle; " +
            "this task never opens a device."));
    }

    private void AddDuplicateDiagnostics(IReadOnlyList<FtdiDeviceInfo> devices)
    {
        IEnumerable<string> duplicateSerials = devices
            .Where(device => !string.IsNullOrWhiteSpace(device.SerialNumber))
            .GroupBy(device => device.SerialNumber, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (string serial in duplicateSerials)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DUPLICATE_SERIAL",
                D2xxDiagnosticSeverity.Warning,
                $"Multiple FTDI entries reported serial number '{serial}'. All entries remain visible."));
        }

        IEnumerable<uint> duplicateLocations = devices
            .Where(device => device.LocationId.HasValue)
            .GroupBy(device => device.LocationId!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (uint location in duplicateLocations)
        {
            _diagnostics.Add(new D2xxDiagnostic(
                "DUPLICATE_LOCATION",
                D2xxDiagnosticSeverity.Warning,
                $"Multiple FTDI entries reported location 0x{location:X8}. All entries remain visible."));
        }
    }

    private static NotSupportedException EnumerationOnlyException() =>
        new("The D2XX inspection transport supports device enumeration only and cannot open, read, or write.");
}
