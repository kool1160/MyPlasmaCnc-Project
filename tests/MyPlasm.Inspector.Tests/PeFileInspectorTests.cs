using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MyPlasm.Inspector.Transport.D2xx;

namespace MyPlasm.Inspector.Tests;

public sealed class PeFileInspectorTests
{
    [Fact]
    public void MissingFileIsReported()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.dll");

        Assert.Throws<FileNotFoundException>(() => new PeFileInspector().Inspect(path));
    }

    [Fact]
    public void InspectionReportsArchitectureVersionHashAndCompatibility()
    {
        string path = typeof(PeFileInspector).Assembly.Location;
        string expectedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        PeInspectionResult result = new PeFileInspector().Inspect(path, Architecture.X86);

        Assert.Equal(PeArchitecture.X86, result.DllArchitecture);
        Assert.False(string.IsNullOrWhiteSpace(result.FileVersion));
        Assert.Equal(expectedHash, result.Sha256);
        Assert.Equal(64, result.Sha256.Length);
        Assert.Equal(Architecture.X86, result.ApplicationArchitecture);
        Assert.True(result.IsLoadCompatible);
    }

    [Theory]
    [InlineData(PeArchitecture.X86, Architecture.X64, false)]
    [InlineData(PeArchitecture.X64, Architecture.X64, true)]
    [InlineData(PeArchitecture.Arm, Architecture.Arm, true)]
    [InlineData(PeArchitecture.Arm64, Architecture.X64, false)]
    [InlineData(PeArchitecture.Unknown, Architecture.X64, false)]
    public void CompatibilityRequiresExactNativeArchitecture(
        PeArchitecture dllArchitecture,
        Architecture applicationArchitecture,
        bool expected)
    {
        Assert.Equal(
            expected,
            PeFileInspector.IsCompatible(dllArchitecture, applicationArchitecture));
    }
}
