using MyPlasm.Inspector.Core.Safety;
using MyPlasm.Inspector.Core.Transport;
using MyPlasm.Inspector.Transport.Fake;

namespace MyPlasm.Inspector.Tests;

public sealed class SafeControllerSessionTests
{
    public static TheoryData<CommandIntent> BlockedIntents =>
        new()
        {
            CommandIntent.Motion,
            CommandIntent.HomingOrProbing,
            CommandIntent.OutputControl,
            CommandIntent.PlasmaStart,
            CommandIntent.ConfigurationWrite,
            CommandIntent.FirmwareOperation,
            CommandIntent.FtdiEepromWrite,
            CommandIntent.Unknown
        };

    [Theory]
    [MemberData(nameof(BlockedIntents))]
    public async Task BlockedOperationNeverReachesTransport(CommandIntent intent)
    {
        await using FakeFtdiTransport transport = new();
        await OpenTargetAsync(transport);
        SafeControllerSession session = new(transport, new DenyByDefaultCommandSafetyPolicy());
        ControllerCommand command = new("synthetic-blocked-sentinel", intent, [0xBA, 0xD0]);

        await Assert.ThrowsAsync<CommandRejectedException>(
            async () => await session.SendAsync(command));

        Assert.Empty(transport.Transmissions);
    }

    [Fact]
    public async Task UnknownReadOnlyPayloadNeverReachesTransport()
    {
        await using FakeFtdiTransport transport = new();
        await OpenTargetAsync(transport);
        SafeControllerSession session = new(transport, new DenyByDefaultCommandSafetyPolicy());
        ControllerCommand command = new("unverified-read", CommandIntent.ReadOnlyStatus, [0x01, 0x02]);

        await Assert.ThrowsAsync<CommandRejectedException>(
            async () => await session.SendAsync(command));

        Assert.Empty(transport.Transmissions);
    }

    [Fact]
    public async Task ExactConfirmedTestEntryCanReachFakeTransport()
    {
        byte[] payload = [0xA0, 0x01];
        AllowedCommandDefinition definition = new(
            "synthetic-test-read",
            CommandIntent.ReadOnlyStatus,
            payload,
            EvidenceClassification.Confirmed,
            "test fixture only; not a real controller command");
        DenyByDefaultCommandSafetyPolicy policy = new([definition]);
        await using FakeFtdiTransport transport = new();
        await OpenTargetAsync(transport);
        SafeControllerSession session = new(transport, policy);

        await session.SendAsync(new ControllerCommand(definition.Name, definition.Intent, payload));

        TransmissionRecord write = Assert.Single(transport.Transmissions);
        Assert.Equal(payload, write.Payload);
        Assert.Equal(TimeSpan.Zero, write.TimestampUtc.Offset);
    }

    private static async ValueTask OpenTargetAsync(FakeFtdiTransport transport)
    {
        IReadOnlyList<FtdiDeviceInfo> devices = await transport.EnumerateDevicesAsync();
        FtdiDeviceInfo target = Assert.Single(devices, device => device.IsMyPlasmController);
        await transport.OpenAsync(target.SerialNumber);
    }
}
