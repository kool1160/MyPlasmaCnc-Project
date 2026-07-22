namespace MyPlasm.Inspector.Transport.D2xx;

public sealed record D2xxNativeDeviceInfo(
    uint Flags,
    uint Type,
    uint DeviceId,
    uint LocationId,
    string SerialNumber,
    string Description);
