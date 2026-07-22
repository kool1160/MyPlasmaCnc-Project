namespace MyPlasm.Inspector.Core.Safety;

public sealed class AllowedCommandDefinition
{
    private readonly byte[] _exactPayload;

    public AllowedCommandDefinition(
        string name,
        CommandIntent intent,
        ReadOnlySpan<byte> exactPayload,
        EvidenceClassification classification,
        string evidenceReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceReference);

        Name = name;
        Intent = intent;
        _exactPayload = exactPayload.ToArray();
        Classification = classification;
        EvidenceReference = evidenceReference;
    }

    public string Name { get; }

    public CommandIntent Intent { get; }

    public ReadOnlyMemory<byte> ExactPayload => _exactPayload;

    public EvidenceClassification Classification { get; }

    public string EvidenceReference { get; }
}
