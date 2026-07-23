namespace MyPlasm.Inspector.Transport.D2xx;

public interface ID2xxNativeApi
{
    D2xxStatus GetLibraryVersion(out uint version);

    D2xxStatus CreateDeviceInfoList(out uint deviceCount);

    D2xxStatus GetDeviceInfoList(D2xxNativeDeviceInfo[] devices, ref uint deviceCount);

    D2xxStatus OpenExBySerialNumber(string serialNumber, out nint handle);

    D2xxStatus Close(nint handle);

    D2xxStatus GetDriverVersion(nint handle, out uint version);

    D2xxStatus GetQueueStatus(nint handle, out uint bytesAvailable);

    D2xxStatus Read(nint handle, byte[] buffer, uint requestedCount, out uint returnedCount);
}
