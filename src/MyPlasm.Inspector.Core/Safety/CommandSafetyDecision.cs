namespace MyPlasm.Inspector.Core.Safety;

public sealed record CommandSafetyDecision(bool IsAllowed, string Reason)
{
    public static CommandSafetyDecision Allow(string reason) => new(true, reason);

    public static CommandSafetyDecision Deny(string reason) => new(false, reason);
}
