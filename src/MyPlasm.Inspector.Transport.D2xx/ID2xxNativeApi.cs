namespace MyPlasm.Inspector.Transport.D2xx;

public interface ID2xxNativeApi
{
    D2xxStatus GetLibraryVersion(out uint version);

    D2xxStatus CreateDeviceInfoList(out uint deviceCount);

    D2xxStatus GetDeviceInfoList(D2xxNativeDeviceInfo[] devices, ref uint deviceCount);
}
