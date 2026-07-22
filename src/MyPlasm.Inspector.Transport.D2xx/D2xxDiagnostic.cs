namespace MyPlasm.Inspector.Transport.D2xx;

public enum D2xxDiagnosticSeverity
{
    Information,
    Warning,
    Error
}

public sealed record D2xxDiagnostic(
    string Code,
    D2xxDiagnosticSeverity Severity,
    string Message);
