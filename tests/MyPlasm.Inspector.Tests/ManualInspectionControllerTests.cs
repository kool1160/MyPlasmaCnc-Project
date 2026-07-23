using MyPlasm.Inspector.Core.Safety;
using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Tests;

public sealed class ManualInspectionControllerTests
{
    [Fact]
    public async Task StartupCreatesNoTransportAndPerformsNoEnumeration()
    {
        int fakeFactoryCalls = 0;
        int d2xxFactoryCalls = 0;

        await using ManualInspectionController controller = new(
            () =>
            {
                fakeFactoryCalls++;
                return new RecordingTransport();
            },
            () =>
            {
                d2xxFactoryCalls++;
                return new RecordingTransport();
            });

        Assert.Null(controller.CurrentTransport);
        Assert.Equal(0, fakeFactoryCalls);
        Assert.Equal(0, d2xxFactoryCalls);
    }

    [Fact]
    public async Task FakeEnumerationIsManualAndCannotReachOpenReadOrWrite()
    {
        RecordingTransport fakeTransport = new();
        int d2xxFactoryCalls = 0;

        await using ManualInspectionController controller = new(
            () => fakeTransport,
            () =>
            {
                d2xxFactoryCalls++;
                return new RecordingTransport();
            });

        IReadOnlyList<FtdiDeviceInfo> devices = await controller.RunFakeEnumerationAsync();

        Assert.Single(devices);
        Assert.Same(fakeTransport, controller.CurrentTransport);
        Assert.Equal(1, fakeTransport.EnumerationCalls);
        Assert.Equal(0, fakeTransport.OpenCalls);
        Assert.Equal(0, fakeTransport.ReadCalls);
        Assert.Equal(0, fakeTransport.WriteCalls);
        Assert.Equal(0, d2xxFactoryCalls);
    }

    [Fact]
    public async Task D2xxFactoryIsNotInvokedUntilOperatorExplicitlyInspectsDevices()
    {
        int d2xxFactoryCalls = 0;
        RecordingTransport d2xxTransport = new();

        await using ManualInspectionController controller = new(
            static () => new RecordingTransport(),
            () =>
            {
                d2xxFactoryCalls++;
                return d2xxTransport;
            });

        Assert.Equal(0, d2xxFactoryCalls);

        await controller.InspectD2xxDevicesAsync();

        Assert.Equal(1, d2xxFactoryCalls);
        Assert.Equal(1, d2xxTransport.EnumerationCalls);
        Assert.Equal(0, d2xxTransport.OpenCalls);
        Assert.Equal(0, d2xxTransport.ReadCalls);
        Assert.Equal(0, d2xxTransport.WriteCalls);
    }

    [Fact]
    public void ProductionAllowlistRemainsEmptyAtStartup()
    {
        Assert.Equal(0, new DenyByDefaultCommandSafetyPolicy().AllowedCommandCount);
    }

    private sealed class RecordingTransport : IControllerTransport
    {
        public int EnumerationCalls { get; private set; }

        public int OpenCalls { get; private set; }

        public int ReadCalls { get; private set; }

        public int WriteCalls { get; private set; }

        public bool IsOpen => false;

        public ValueTask<IReadOnlyList<FtdiDeviceInfo>> EnumerateDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            EnumerationCalls++;
            return ValueTask.FromResult<IReadOnlyList<FtdiDeviceInfo>>(
            [new(0, FtdiDeviceInfo.MyPlasmDescription, "TEST", "Fake", null, null, false)]);
        }

        public ValueTask OpenAsync(string serialNumber, CancellationToken cancellationToken = default)
        {
            OpenCalls++;
            throw new InvalidOperationException("Open must not be reached by manual enumeration.");
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadCalls++;
            throw new InvalidOperationException("Read must not be reached by manual enumeration.");
        }

        public ValueTask WriteAsync(ValidatedCommand command, CancellationToken cancellationToken = default)
        {
            WriteCalls++;
            throw new InvalidOperationException("Write must not be reached by manual enumeration.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
