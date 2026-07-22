namespace MyPlasm.Inspector.Core.Safety;

public sealed class ControllerCommand
{
    private readonly byte[] _payload;

    public ControllerCommand(string name, CommandIntent intent, ReadOnlySpan<byte> payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Name = name;
        Intent = intent;
        _payload = payload.ToArray();
    }

    public string Name { get; }

    public CommandIntent Intent { get; }

    public ReadOnlyMemory<byte> Payload => _payload;
}
