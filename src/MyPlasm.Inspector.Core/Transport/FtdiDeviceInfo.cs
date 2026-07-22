namespace MyPlasm.Inspector.Core.Transport;

public sealed record FtdiDeviceInfo(
    uint Index,
    string Description,
    string SerialNumber,
    string DeviceType,
    ushort? VendorId,
    ushort? ProductId,
    bool IsOpen)
{
    public const string MyPlasmDescription = "MyPlasm CNC";

    public bool IsMyPlasmController =>
        string.Equals(Description, MyPlasmDescription, StringComparison.Ordinal);

    public string VendorIdDisplay => VendorId is ushort value ? $"0x{value:X4}" : "Unknown";

    public string ProductIdDisplay => ProductId is ushort value ? $"0x{value:X4}" : "Unknown";
}
