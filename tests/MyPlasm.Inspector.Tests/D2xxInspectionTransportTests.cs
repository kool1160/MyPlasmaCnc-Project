using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.D2xx;
using System.Runtime.InteropServices;

namespace MyPlasm.Inspector.Tests;

public sealed class D2xxInspectionTransportTests
{
    [Fact]
    public async Task EnumerationMapsMetadataAndFindsOnlyExactDescriptionMatch()
    {
        StubNativeApi nativeApi = new(
            0x00030102,
            [
                new(0, 5, 0x04036001, 0x00001234, "MY001", "MyPlasm CNC"),
                new(1, 8, 0x04036014, 0x00005678, "OTHER", "MyPlasm CNC Clone")
            ]);
        await using D2xxInspectionTransport transport = new(nativeApi);

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.Equal("3.01.02", transport.LibraryVersion);
        Assert.Equal(2, devices.Count);
        FtdiDeviceInfo candidate = Assert.Single(devices, device => device.IsMyPlasmController);
        Assert.Equal((uint)0, candidate.Index);
        Assert.Equal((uint)0x00001234, candidate.LocationId);
        Assert.Equal((uint)0x04036001, candidate.DeviceId);
        Assert.Equal((ushort)0x0403, candidate.VendorId);
        Assert.Equal((ushort)0x6001, candidate.ProductId);
        Assert.Equal("FT_DEVICE_232R (5)", candidate.DeviceType);
        Assert.False(candidate.IsOpen);
        Assert.True(devices[1].IsOpen);
        Assert.Contains(
            transport.Diagnostics,
            diagnostic => diagnostic.Code == "DRIVER_VERSION_NOT_QUERIED");
        Assert.Equal(1, nativeApi.GetLibraryVersionCalls);
        Assert.Equal(1, nativeApi.CreateDeviceInfoListCalls);
        Assert.Equal(1, nativeApi.GetDeviceInfoListCalls);
    }

    [Fact]
    public async Task DuplicateDevicesRemainVisibleWithClearDiagnostics()
    {
        StubNativeApi nativeApi = new(
            0x00030102,
            [
                new(0, 5, 0x04036001, 0x00001000, "DUPLICATE", "MyPlasm CNC"),
                new(0, 5, 0x04036001, 0x00001000, "DUPLICATE", "MyPlasm CNC")
            ]);
        await using D2xxInspectionTransport transport = new(nativeApi);

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.Equal(2, devices.Count);
        Assert.Contains(transport.Diagnostics, diagnostic => diagnostic.Code == "DUPLICATE_SERIAL");
        Assert.Contains(transport.Diagnostics, diagnostic => diagnostic.Code == "DUPLICATE_LOCATION");
    }

    [Fact]
    public async Task ZeroDevicesReportsDriverOrConnectionDiagnostic()
    {
        await using D2xxInspectionTransport transport = new(new StubNativeApi(0x00030102, []));

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.Empty(devices);
        Assert.Contains(transport.Diagnostics, diagnostic => diagnostic.Code == "NO_DEVICES");
    }

    [Theory]
    [InlineData(D2xxStatus.DeviceNotFound)]
    [InlineData(D2xxStatus.IoError)]
    [InlineData(D2xxStatus.OtherError)]
    public async Task NativeListCreationFailureReturnsDiagnostic(D2xxStatus status)
    {
        StubNativeApi nativeApi = new(0x00030102, []) { CreateStatus = status };
        await using D2xxInspectionTransport transport = new(nativeApi);

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.Empty(devices);
        D2xxDiagnostic diagnostic = Assert.Single(
            transport.Diagnostics,
            item => item.Code == "DEVICE_LIST_CREATE_FAILED");
        Assert.Contains(status.ToString(), diagnostic.Message);
    }

    [Fact]
    public async Task NativeListReadFailureReturnsDiagnostic()
    {
        StubNativeApi nativeApi = new(
            0x00030102,
            [new(0, 5, 0x04036001, 0x00001000, "MY001", "MyPlasm CNC")])
        {
            ListStatus = D2xxStatus.IoError
        };
        await using D2xxInspectionTransport transport = new(nativeApi);

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.Empty(devices);
        Assert.Contains(
            transport.Diagnostics,
            diagnostic => diagnostic.Code == "DEVICE_LIST_READ_FAILED");
    }

    [Fact]
    public async Task MissingLocalDllReturnsDiagnosticWithoutNativeLoad()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}", "ftd2xx.dll");
        await using D2xxInspectionTransport transport = new(missingPath);

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.Empty(devices);
        Assert.Contains(transport.Diagnostics, diagnostic => diagnostic.Code == "DLL_NOT_FOUND");
    }

    [Fact]
    public async Task ArchitectureMismatchIsRejectedBeforeNativeLoad()
    {
        string x86PePath = typeof(D2xxInspectionTransport).Assembly.Location;
        await using D2xxInspectionTransport transport = new(
            x86PePath,
            applicationArchitecture: Architecture.X64);

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.Empty(devices);
        Assert.Contains(
            transport.Diagnostics,
            diagnostic => diagnostic.Code == "DLL_ARCHITECTURE_MISMATCH");
    }

    [Fact]
    public async Task EnumerationTransportCannotOpenReadOrWrite()
    {
        await using D2xxInspectionTransport transport = new(new StubNativeApi(0x00030102, []));

        await Assert.ThrowsAsync<NotSupportedException>(
            () => transport.OpenAsync("MY001").AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => transport.ReadAsync(new byte[1]).AsTask());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => transport.WriteAsync(null!).AsTask());
    }

    [Fact]
    public void InjectableNativeSurfaceContainsOnlyEnumerationAndLibraryMetadata()
    {
        string[] methodNames = typeof(ID2xxNativeApi)
            .GetMethods()
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["CreateDeviceInfoList", "GetDeviceInfoList", "GetLibraryVersion"],
            methodNames);
    }

    private sealed class StubNativeApi : ID2xxNativeApi
    {
        private readonly D2xxNativeDeviceInfo[] _devices;
        private readonly uint _libraryVersion;

        public StubNativeApi(uint libraryVersion, D2xxNativeDeviceInfo[] devices)
        {
            _libraryVersion = libraryVersion;
            _devices = devices;
        }

        public D2xxStatus CreateStatus { get; init; } = D2xxStatus.Ok;

        public D2xxStatus ListStatus { get; init; } = D2xxStatus.Ok;

        public D2xxStatus VersionStatus { get; init; } = D2xxStatus.Ok;

        public int GetLibraryVersionCalls { get; private set; }

        public int CreateDeviceInfoListCalls { get; private set; }

        public int GetDeviceInfoListCalls { get; private set; }

        public D2xxStatus GetLibraryVersion(out uint version)
        {
            GetLibraryVersionCalls++;
            version = _libraryVersion;
            return VersionStatus;
        }

        public D2xxStatus CreateDeviceInfoList(out uint deviceCount)
        {
            CreateDeviceInfoListCalls++;
            deviceCount = checked((uint)_devices.Length);
            return CreateStatus;
        }

        public D2xxStatus GetDeviceInfoList(D2xxNativeDeviceInfo[] devices, ref uint deviceCount)
        {
            GetDeviceInfoListCalls++;
            if (ListStatus != D2xxStatus.Ok)
            {
                return ListStatus;
            }

            int count = Math.Min(devices.Length, _devices.Length);
            Array.Copy(_devices, devices, count);
            deviceCount = checked((uint)count);
            return D2xxStatus.Ok;
        }
    }
}
