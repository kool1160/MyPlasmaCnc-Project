using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Core.Safety;

public sealed class SafeControllerSession
{
    private readonly IControllerTransport _transport;
    private readonly ICommandSafetyPolicy _safetyPolicy;

    public SafeControllerSession(IControllerTransport transport, ICommandSafetyPolicy safetyPolicy)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _safetyPolicy = safetyPolicy ?? throw new ArgumentNullException(nameof(safetyPolicy));
    }

    public async ValueTask SendAsync(
        ControllerCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        CommandSafetyDecision decision = _safetyPolicy.Evaluate(command);
        if (!decision.IsAllowed)
        {
            throw new CommandRejectedException(command, decision.Reason);
        }

        await _transport.WriteAsync(new ValidatedCommand(command), cancellationToken);
    }
}
