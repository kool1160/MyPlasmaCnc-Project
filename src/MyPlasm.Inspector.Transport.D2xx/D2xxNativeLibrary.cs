using System.Runtime.InteropServices;

namespace MyPlasm.Inspector.Transport.D2xx;

internal sealed class D2xxNativeLibrary : ID2xxNativeApi, IDisposable
{
    private readonly nint _libraryHandle;
    private readonly CreateDeviceInfoListDelegate _createDeviceInfoList;
    private readonly GetDeviceInfoListDelegate _getDeviceInfoList;
    private readonly GetLibraryVersionDelegate _getLibraryVersion;
    private readonly OpenExDelegate _openEx;
    private readonly CloseDelegate _close;
    private readonly GetDriverVersionDelegate _getDriverVersion;
    private readonly GetQueueStatusDelegate _getQueueStatus;
    private readonly ReadDelegate _read;
    private bool _disposed;

    private D2xxNativeLibrary(nint libraryHandle)
    {
        _libraryHandle = libraryHandle;
        _createDeviceInfoList = GetExport<CreateDeviceInfoListDelegate>("FT_CreateDeviceInfoList");
        _getDeviceInfoList = GetExport<GetDeviceInfoListDelegate>("FT_GetDeviceInfoList");
        _getLibraryVersion = GetExport<GetLibraryVersionDelegate>("FT_GetLibraryVersion");
        _openEx = GetExport<OpenExDelegate>("FT_OpenEx");
        _close = GetExport<CloseDelegate>("FT_Close");
        _getDriverVersion = GetExport<GetDriverVersionDelegate>("FT_GetDriverVersion");
        _getQueueStatus = GetExport<GetQueueStatusDelegate>("FT_GetQueueStatus");
        _read = GetExport<ReadDelegate>("FT_Read");
    }

    public static D2xxNativeLibrary Load(string fullPath)
    {
        nint handle = NativeLibrary.Load(fullPath);
        try
        {
            return new D2xxNativeLibrary(handle);
        }
        catch
        {
            NativeLibrary.Free(handle);
            throw;
        }
    }

    public D2xxStatus GetLibraryVersion(out uint version) => _getLibraryVersion(out version);

    public D2xxStatus OpenExBySerialNumber(string serialNumber, out nint handle) =>
        _openEx(serialNumber, 1, out handle);

    public D2xxStatus Close(nint handle) => _close(handle);

    public D2xxStatus GetDriverVersion(nint handle, out uint version) => _getDriverVersion(handle, out version);

    public D2xxStatus GetQueueStatus(nint handle, out uint bytesAvailable) => _getQueueStatus(handle, out bytesAvailable);

    public D2xxStatus Read(nint handle, byte[] buffer, uint requestedCount, out uint returnedCount) =>
        _read(handle, buffer, requestedCount, out returnedCount);

    public D2xxStatus CreateDeviceInfoList(out uint deviceCount) =>
        _createDeviceInfoList(out deviceCount);

    public D2xxStatus GetDeviceInfoList(D2xxNativeDeviceInfo[] devices, ref uint deviceCount)
    {
        ArgumentNullException.ThrowIfNull(devices);

        NativeDeviceInfoNode[] nativeDevices = new NativeDeviceInfoNode[devices.Length];
        D2xxStatus status = _getDeviceInfoList(nativeDevices, ref deviceCount);
        if (status != D2xxStatus.Ok)
        {
            return status;
        }

        int returnedCount = Math.Min(devices.Length, checked((int)deviceCount));
        for (int index = 0; index < returnedCount; index++)
        {
            NativeDeviceInfoNode native = nativeDevices[index];
            devices[index] = new D2xxNativeDeviceInfo(
                native.Flags,
                native.Type,
                native.DeviceId,
                native.LocationId,
                native.SerialNumber ?? string.Empty,
                native.Description ?? string.Empty);
        }

        return status;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        NativeLibrary.Free(_libraryHandle);
        _disposed = true;
    }

    private TDelegate GetExport<TDelegate>(string name)
        where TDelegate : Delegate
    {
        nint address = NativeLibrary.GetExport(_libraryHandle, name);
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(address);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate D2xxStatus CreateDeviceInfoListDelegate(out uint deviceCount);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate D2xxStatus GetDeviceInfoListDelegate(
        [Out] NativeDeviceInfoNode[] devices,
        ref uint deviceCount);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate D2xxStatus GetLibraryVersionDelegate(out uint version);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Ansi)]
    private delegate D2xxStatus OpenExDelegate(string serialNumber, uint flags, out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate D2xxStatus CloseDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate D2xxStatus GetDriverVersionDelegate(nint handle, out uint version);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate D2xxStatus GetQueueStatusDelegate(nint handle, out uint bytesAvailable);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate D2xxStatus ReadDelegate(nint handle, [Out] byte[] buffer, uint requestedCount, out uint returnedCount);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct NativeDeviceInfoNode
    {
        public uint Flags;
        public uint Type;
        public uint DeviceId;
        public uint LocationId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string? SerialNumber;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string? Description;

        public nint Handle;
    }
}
