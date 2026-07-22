namespace MyPlasm.Inspector.Core.Transport;

public sealed record FtdiDeviceInfo(
    uint Index,
    string Description,
    string SerialNumber,
    string DeviceType,
    ushort? VendorId,
    ushort? ProductId,
    bool IsOpen,
    uint? LocationId = null,
    uint? DeviceId = null)
{
    public const string MyPlasmDescription = "MyPlasm CNC";

    public bool IsMyPlasmController =>
        string.Equals(Description, MyPlasmDescription, StringComparison.Ordinal);

    public string VendorIdDisplay => VendorId is ushort value ? $"0x{value:X4}" : "Unknown";

    public string ProductIdDisplay => ProductId is ushort value ? $"0x{value:X4}" : "Unknown";

    public string LocationIdDisplay => LocationId is uint value ? $"0x{value:X8}" : "Unavailable";

    public string DeviceIdDisplay => DeviceId is uint value ? $"0x{value:X8}" : "Unavailable";
}
