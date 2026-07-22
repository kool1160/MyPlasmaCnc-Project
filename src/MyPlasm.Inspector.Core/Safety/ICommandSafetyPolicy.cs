namespace MyPlasm.Inspector.Core.Safety;

public interface ICommandSafetyPolicy
{
    int AllowedCommandCount { get; }

    CommandSafetyDecision Evaluate(ControllerCommand command);
}
