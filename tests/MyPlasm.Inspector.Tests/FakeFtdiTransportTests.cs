using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.Fake;

namespace MyPlasm.Inspector.Tests;

public sealed class FakeFtdiTransportTests
{
    [Fact]
    public async Task EnumerationIdentifiesTargetWithoutOpeningAnyDevice()
    {
        await using FakeFtdiTransport transport = new();

        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();

        Assert.False(transport.IsOpen);
        Assert.Single(devices, device => device.IsMyPlasmController);
        Assert.All(devices, device => Assert.False(device.IsOpen));
    }

    [Fact]
    public async Task FakeTransportPreservesQueuedReceiveBytesExactly()
    {
        await using FakeFtdiTransport transport = new();
        FtdiDeviceInfo target = Assert.Single(
            await transport.EnumerateDevicesAsync(),
            device => device.IsMyPlasmController);
        await transport.OpenAsync(target.SerialNumber);
        byte[] expected = [0x00, 0x7F, 0x80, 0xFF];
        transport.QueueReceiveBytes(expected);
        byte[] actual = new byte[expected.Length];

        int count = await transport.ReadAsync(actual);

        Assert.Equal(expected.Length, count);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task OpenAndCloseCanBeRepeatedWithoutTransmitting()
    {
        await using FakeFtdiTransport transport = new();
        FtdiDeviceInfo target = Assert.Single(
            await transport.EnumerateDevicesAsync(),
            device => device.IsMyPlasmController);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            await transport.OpenAsync(target.SerialNumber);
            Assert.True(transport.IsOpen);
            await transport.CloseAsync();
            Assert.False(transport.IsOpen);
        }

        Assert.Empty(transport.Transmissions);
    }
}
