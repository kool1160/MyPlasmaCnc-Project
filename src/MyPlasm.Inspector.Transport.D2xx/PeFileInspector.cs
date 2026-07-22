using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace MyPlasm.Inspector.Transport.D2xx;

public sealed class PeFileInspector
{
    public PeInspectionResult Inspect(
        string filePath,
        Architecture? applicationArchitecture = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The vendor D2XX DLL was not found.", fullPath);
        }

        Architecture selectedArchitecture = applicationArchitecture ?? RuntimeInformation.ProcessArchitecture;

        PeArchitecture dllArchitecture;
        using (FileStream stream = File.OpenRead(fullPath))
        using (PEReader reader = new(stream, PEStreamOptions.LeaveOpen))
        {
            if (!reader.HasMetadata && reader.PEHeaders.PEHeader is null)
            {
                throw new BadImageFormatException("The file does not contain a valid PE header.", fullPath);
            }

            dllArchitecture = MapArchitecture(reader.PEHeaders.CoffHeader.Machine);
        }

        string sha256;
        using (FileStream stream = File.OpenRead(fullPath))
        {
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
        }

        string? fileVersion = FileVersionInfo.GetVersionInfo(fullPath).FileVersion;

        return new PeInspectionResult(
            fullPath,
            dllArchitecture,
            string.IsNullOrWhiteSpace(fileVersion) ? null : fileVersion,
            sha256,
            selectedArchitecture,
            IsCompatible(dllArchitecture, selectedArchitecture));
    }

    public static bool IsCompatible(PeArchitecture dllArchitecture, Architecture applicationArchitecture) =>
        (dllArchitecture, applicationArchitecture) switch
        {
            (PeArchitecture.X86, Architecture.X86) => true,
            (PeArchitecture.X64, Architecture.X64) => true,
            (PeArchitecture.Arm, Architecture.Arm) => true,
            (PeArchitecture.Arm64, Architecture.Arm64) => true,
            _ => false
        };

    internal static PeArchitecture MapArchitecture(System.Reflection.PortableExecutable.Machine machine) =>
        machine switch
        {
            System.Reflection.PortableExecutable.Machine.I386 => PeArchitecture.X86,
            System.Reflection.PortableExecutable.Machine.Amd64 => PeArchitecture.X64,
            System.Reflection.PortableExecutable.Machine.Arm or
                System.Reflection.PortableExecutable.Machine.ArmThumb2 => PeArchitecture.Arm,
            System.Reflection.PortableExecutable.Machine.Arm64 => PeArchitecture.Arm64,
            _ => PeArchitecture.Unknown
        };
}
