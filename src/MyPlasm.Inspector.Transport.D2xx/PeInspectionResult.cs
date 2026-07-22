using System.Runtime.InteropServices;

namespace MyPlasm.Inspector.Transport.D2xx;

public enum PeArchitecture
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64
}

public sealed record PeInspectionResult(
    string FilePath,
    PeArchitecture DllArchitecture,
    string? FileVersion,
    string Sha256,
    Architecture ApplicationArchitecture,
    bool IsLoadCompatible);
