namespace MyPlasm.Inspector.Core.Safety;

public sealed class DenyByDefaultCommandSafetyPolicy : ICommandSafetyPolicy
{
    private readonly IReadOnlyList<AllowedCommandDefinition> _allowlist;

    public DenyByDefaultCommandSafetyPolicy(IEnumerable<AllowedCommandDefinition>? allowlist = null)
    {
        _allowlist = (allowlist ?? []).ToArray();

        foreach (AllowedCommandDefinition definition in _allowlist)
        {
            ValidateAllowlistEntry(definition);
        }
    }

    public int AllowedCommandCount => _allowlist.Count;

    public CommandSafetyDecision Evaluate(ControllerCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!IsReadOnlyCandidate(command.Intent))
        {
            return CommandSafetyDecision.Deny(
                $"Intent '{command.Intent}' is prohibited by the Version 1 read-only boundary.");
        }

        if (command.Payload.IsEmpty)
        {
            return CommandSafetyDecision.Deny("Empty command payloads are never valid.");
        }

        AllowedCommandDefinition? match = _allowlist.SingleOrDefault(
            candidate => candidate.Intent == command.Intent &&
                         candidate.Name == command.Name &&
                         candidate.ExactPayload.Span.SequenceEqual(command.Payload.Span));

        return match is null
            ? CommandSafetyDecision.Deny(
                "No exact, confirmed allowlist entry matches the command name, intent, and bytes.")
            : CommandSafetyDecision.Allow(
                $"Exact payload is backed by confirmed evidence: {match.EvidenceReference}");
    }

    private static bool IsReadOnlyCandidate(CommandIntent intent) =>
        intent is CommandIntent.ReadOnlyIdentification or CommandIntent.ReadOnlyStatus;

    private static void ValidateAllowlistEntry(AllowedCommandDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (!IsReadOnlyCandidate(definition.Intent))
        {
            throw new ArgumentException(
                $"Intent '{definition.Intent}' can never be allowlisted in Version 1.",
                nameof(definition));
        }

        if (definition.Classification != EvidenceClassification.Confirmed)
        {
            throw new ArgumentException(
                "Only commands supported by confirmed evidence can be allowlisted.",
                nameof(definition));
        }

        if (definition.ExactPayload.IsEmpty)
        {
            throw new ArgumentException("Allowlisted commands must contain exact bytes.", nameof(definition));
        }
    }
}
