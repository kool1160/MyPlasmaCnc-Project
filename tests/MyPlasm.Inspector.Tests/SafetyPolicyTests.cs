using MyPlasm.Inspector.Core.Safety;

namespace MyPlasm.Inspector.Tests;

public sealed class SafetyPolicyTests
{
    [Fact]
    public void InitialPolicyHasAnEmptyAllowlist()
    {
        DenyByDefaultCommandSafetyPolicy policy = new();

        Assert.Equal(0, policy.AllowedCommandCount);
    }

    [Fact]
    public void PlausibleButUnverifiedReadOnlyCommandIsDenied()
    {
        DenyByDefaultCommandSafetyPolicy policy = new();
        ControllerCommand command = new(
            "synthetic-status-request",
            CommandIntent.ReadOnlyStatus,
            [0x53, 0x54]);

        CommandSafetyDecision decision = policy.Evaluate(command);

        Assert.False(decision.IsAllowed);
        Assert.Contains("No exact, confirmed allowlist entry", decision.Reason);
    }

    [Theory]
    [InlineData(CommandIntent.Motion)]
    [InlineData(CommandIntent.HomingOrProbing)]
    [InlineData(CommandIntent.OutputControl)]
    [InlineData(CommandIntent.PlasmaStart)]
    [InlineData(CommandIntent.ConfigurationWrite)]
    [InlineData(CommandIntent.FirmwareOperation)]
    [InlineData(CommandIntent.FtdiEepromWrite)]
    [InlineData(CommandIntent.Unknown)]
    public void UnsafeAndUnknownIntentsArePermanentlyDenied(CommandIntent intent)
    {
        DenyByDefaultCommandSafetyPolicy policy = new();
        ControllerCommand command = new("synthetic-unsafe-sentinel", intent, [0xDE, 0xAD]);

        CommandSafetyDecision decision = policy.Evaluate(command);

        Assert.False(decision.IsAllowed);
        Assert.Contains("prohibited", decision.Reason);
    }

    [Fact]
    public void HypothesisCannotBeAddedToAllowlist()
    {
        AllowedCommandDefinition definition = new(
            "synthetic-hypothesis",
            CommandIntent.ReadOnlyIdentification,
            [0x01],
            EvidenceClassification.Hypothesis,
            "test-only evidence");

        Assert.Throws<ArgumentException>(() => new DenyByDefaultCommandSafetyPolicy([definition]));
    }

    [Fact]
    public void DestructiveIntentCannotBeAddedToAllowlistEvenWhenMarkedConfirmed()
    {
        AllowedCommandDefinition definition = new(
            "synthetic-motion",
            CommandIntent.Motion,
            [0x02],
            EvidenceClassification.Confirmed,
            "test-only evidence");

        Assert.Throws<ArgumentException>(() => new DenyByDefaultCommandSafetyPolicy([definition]));
    }

    [Fact]
    public void ConfirmedReadOnlyEntryRequiresAnExactNameIntentAndByteMatch()
    {
        AllowedCommandDefinition definition = new(
            "synthetic-identification",
            CommandIntent.ReadOnlyIdentification,
            [0x10, 0x20],
            EvidenceClassification.Confirmed,
            "test-only evidence; not a controller protocol claim");
        DenyByDefaultCommandSafetyPolicy policy = new([definition]);

        Assert.True(policy.Evaluate(new ControllerCommand(
            definition.Name,
            definition.Intent,
            definition.ExactPayload.Span)).IsAllowed);
        Assert.False(policy.Evaluate(new ControllerCommand(
            definition.Name,
            definition.Intent,
            [0x10, 0x21])).IsAllowed);
        Assert.False(policy.Evaluate(new ControllerCommand(
            "different-name",
            definition.Intent,
            definition.ExactPayload.Span)).IsAllowed);
    }
}
