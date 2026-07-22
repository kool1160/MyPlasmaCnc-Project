namespace MyPlasm.Inspector.Core.Safety;

public sealed class CommandRejectedException : InvalidOperationException
{
    public CommandRejectedException(ControllerCommand command, string reason)
        : base($"Command '{command.Name}' was rejected before transport: {reason}")
    {
        CommandName = command.Name;
        Intent = command.Intent;
        Reason = reason;
    }

    public string CommandName { get; }

    public CommandIntent Intent { get; }

    public string Reason { get; }
}
