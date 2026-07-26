using System.Diagnostics;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

internal static class CampaignTestAssertions
{
    public static readonly string[] ReportNames =
    [
        "campaign-summary.json",
        "campaign-report.md",
        "stable-transaction-classes.csv",
        "class-frequency-by-run.csv",
        "run-structure-comparison.csv",
        "hashes.sha256"
    ];

    public static Dictionary<string, byte[]> SnapshotKnownReports(
        string directory) =>
        ReportNames
            .Where(name => File.Exists(Path.Combine(directory, name)))
            .ToDictionary(
                name => name,
                name => File.ReadAllBytes(Path.Combine(directory, name)),
                StringComparer.Ordinal);

    public static void AssertKnownReportsEqual(
        string directory,
        IReadOnlyDictionary<string, byte[]> expected)
    {
        Assert.Equal(
            expected.Keys.Order(StringComparer.Ordinal),
            ReportNames
                .Where(name => File.Exists(Path.Combine(directory, name)))
                .Order(StringComparer.Ordinal));
        foreach ((string name, byte[] bytes) in expected)
        {
            Assert.Equal(
                bytes,
                File.ReadAllBytes(Path.Combine(directory, name)));
        }
    }

    public static async Task AssertCompleteVerifiedSetAsync(string directory)
    {
        Assert.Equal(
            ReportNames.Order(StringComparer.Ordinal),
            Directory.GetFiles(directory)
                .Select(Path.GetFileName)
                .Where(name => ReportNames.Contains(name, StringComparer.Ordinal))
                .Order(StringComparer.Ordinal));
        string[] lines = await File.ReadAllLinesAsync(
            Path.Combine(directory, "hashes.sha256"));
        string[] expectedNames = ReportNames
            .Where(name => name != "hashes.sha256")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expectedNames.Length, lines.Length);
        for (int index = 0; index < lines.Length; index++)
        {
            Assert.Equal(expectedNames[index], lines[index][66..]);
            Assert.Equal(
                lines[index][..64],
                await CaptureAnalyzer.CalculateSha256Async(
                    Path.Combine(directory, expectedNames[index])));
        }
    }

    public static void AssertNoTransactionArtifacts(
        string workspaceRoot,
        string outputName)
    {
        Assert.Empty(
            Directory.EnumerateDirectories(
                workspaceRoot,
                $".{outputName}.campaign-*",
                SearchOption.TopDirectoryOnly));
    }

    public static string CopyAnalysisDirectory(
        TestWorkspace workspace,
        string source,
        string relativeDestination)
    {
        string destination = workspace.PathFor(relativeDestination);
        Directory.CreateDirectory(destination);
        foreach (string sourcePath in Directory.GetFiles(source))
        {
            File.Copy(
                sourcePath,
                Path.Combine(destination, Path.GetFileName(sourcePath)));
        }

        return destination;
    }

    public static bool TryCreateHardLink(string linkPath, string targetPath)
    {
        ProcessStartInfo startInfo;
        if (OperatingSystem.IsWindows())
        {
            startInfo = new ProcessStartInfo(
                "cmd.exe",
                $"/d /c mklink /H \"{linkPath}\" \"{targetPath}\"");
        }
        else
        {
            startInfo = new ProcessStartInfo(
                "ln",
                $"\"{targetPath}\" \"{linkPath}\"");
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using Process? process = Process.Start(startInfo);
        process?.WaitForExit();
        return process?.ExitCode == 0 && File.Exists(linkPath);
    }

    public static bool TryCreateDirectoryLink(
        string linkPath,
        string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return Directory.Exists(linkPath);
        }

        ProcessStartInfo startInfo = new(
            "cmd.exe",
            $"/d /c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using Process? process = Process.Start(startInfo);
        process?.WaitForExit();
        return process?.ExitCode == 0 && Directory.Exists(linkPath);
    }
}
