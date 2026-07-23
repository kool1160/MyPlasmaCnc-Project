using MyPlasm.Inspector.Core.Safety;
using MyPlasm.Inspector.Transport.D2xx;

namespace MyPlasm.Inspector.Tests;

public sealed class ProductionSafetyAuditTests
{
    private static readonly string[] ProhibitedNativeSymbols =
    [
        "FT_Write",
        "FT_EE_",
        "FT_SetBaudRate",
        "FT_SetBitMode",
        "FT_SetDataCharacteristics",
        "FT_SetFlowControl",
        "FT_SetLatencyTimer",
        "FT_ResetDevice",
        "FT_Purge",
        "FT_EraseEE",
        "FT_Program",
        "FT_Firmware"
    ];

    [Fact]
    public void ProductionNativeInterfaceContainsNoTransmitOrConfigurationFunction()
    {
        string[] methods = typeof(ID2xxNativeApi)
            .GetMethods()
            .Select(method => method.Name)
            .ToArray();

        Assert.DoesNotContain(methods, name =>
            name.Contains("Write", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Eeprom", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Baud", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("BitMode", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Firmware", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProductionSourceContainsNoProhibitedNativeSymbol()
    {
        string root = FindRepositoryRoot();
        string[] productionFiles = Directory
            .GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (string file in productionFiles)
        {
            string source = File.ReadAllText(file);
            Assert.All(
                ProhibitedNativeSymbols,
                symbol => Assert.DoesNotContain(symbol, source, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task NoProductionD2xxPathCanTransmitControllerBytes()
    {
        ID2xxNativeApi native = new NoOpNativeApi();
        await using D2xxInspectionTransport transport = new(native);

        await Assert.ThrowsAsync<NotSupportedException>(
            () => transport.WriteAsync(null!).AsTask());
        Assert.Equal(0, new DenyByDefaultCommandSafetyPolicy().AllowedCommandCount);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyPlasm.Inspector.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed class NoOpNativeApi : ID2xxNativeApi
    {
        public D2xxStatus GetLibraryVersion(out uint version)
        {
            version = 0;
            return D2xxStatus.Ok;
        }

        public D2xxStatus CreateDeviceInfoList(out uint deviceCount)
        {
            deviceCount = 0;
            return D2xxStatus.Ok;
        }

        public D2xxStatus GetDeviceInfoList(D2xxNativeDeviceInfo[] devices, ref uint deviceCount) =>
            D2xxStatus.Ok;

        public D2xxStatus OpenExBySerialNumber(string serialNumber, out nint handle)
        {
            handle = 0;
            return D2xxStatus.DeviceNotFound;
        }

        public D2xxStatus Close(nint handle) => D2xxStatus.InvalidHandle;

        public D2xxStatus GetDriverVersion(nint handle, out uint version)
        {
            version = 0;
            return D2xxStatus.InvalidHandle;
        }

        public D2xxStatus GetQueueStatus(nint handle, out uint bytesAvailable)
        {
            bytesAvailable = 0;
            return D2xxStatus.InvalidHandle;
        }

        public D2xxStatus Read(nint handle, byte[] buffer, uint requestedCount, out uint returnedCount)
        {
            returnedCount = 0;
            return D2xxStatus.InvalidHandle;
        }
    }
}
