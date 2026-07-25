using System.Runtime.InteropServices;
using MyPlasm.Inspector.Transport.D2xx;

string path = args.Length > 0 && !args[0].StartsWith("--", StringComparison.Ordinal)
    ? args[0]
    : Path.Combine("native", "local", "ftd2xx.dll");

Architecture? selectedArchitecture = null;
int architectureOption = Array.IndexOf(args, "--architecture");
if (architectureOption >= 0)
{
    if (architectureOption + 1 >= args.Length ||
        !TryParseArchitecture(args[architectureOption + 1], out Architecture parsedArchitecture))
    {
        Console.Error.WriteLine("Use --architecture x86, x64, arm, or arm64.");
        return 1;
    }

    selectedArchitecture = parsedArchitecture;
}

try
{
    PeInspectionResult result = new PeFileInspector().Inspect(path, selectedArchitecture);
    Console.WriteLine($"DLL: {result.FilePath}");
    Console.WriteLine($"Architecture: {result.DllArchitecture}");
    Console.WriteLine($"File version: {result.FileVersion ?? "Unavailable"}");
    Console.WriteLine($"SHA-256: {result.Sha256}");
    Console.WriteLine($"Selected application architecture: {result.ApplicationArchitecture}");
    Console.WriteLine($"Load compatible: {(result.IsLoadCompatible ? "Yes" : "No")}");
    return result.IsLoadCompatible ? 0 : 3;
}
catch (FileNotFoundException exception)
{
    Console.Error.WriteLine(exception.Message);
    Console.Error.WriteLine("Place the vendor DLL under native/local/; it is intentionally ignored by Git.");
    return 2;
}
catch (Exception exception) when (exception is BadImageFormatException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Inspection failed: {exception.Message}");
    return 4;
}

static bool TryParseArchitecture(string value, out Architecture architecture)
{
    switch (value.ToUpperInvariant())
    {
        case "X86":
            architecture = Architecture.X86;
            return true;
        case "X64":
            architecture = Architecture.X64;
            return true;
        case "ARM":
            architecture = Architecture.Arm;
            return true;
        case "ARM64":
            architecture = Architecture.Arm64;
            return true;
        default:
            architecture = default;
            return false;
    }
}
