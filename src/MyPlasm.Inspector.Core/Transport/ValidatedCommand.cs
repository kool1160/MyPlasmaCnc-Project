using MyPlasm.Inspector.Core.Safety;

namespace MyPlasm.Inspector.Core.Transport;

public sealed class ValidatedCommand
{
    private readonly byte[] _payload;

    internal ValidatedCommand(ControllerCommand command)
    {
        Name = command.Name;
        Intent = command.Intent;
        _payload = command.Payload.ToArray();
    }

    public string Name { get; }

    public CommandIntent Intent { get; }

    public ReadOnlyMemory<byte> Payload => _payload;
}
