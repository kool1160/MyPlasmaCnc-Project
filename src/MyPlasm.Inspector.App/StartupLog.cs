using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;

namespace MyPlasm.Inspector.App;

internal sealed class StartupLog
{
    private const string LogDirectoryName = "MyPlasm Inspector\\Logs";
    private readonly object _gate = new();

    private StartupLog(string directoryPath, string filePath)
    {
        DirectoryPath = directoryPath;
        FilePath = filePath;
    }

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public static StartupLog Create()
    {
        string directoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LogDirectoryName);
        Directory.CreateDirectory(directoryPath);

        string filePath = Path.Combine(
            directoryPath,
            $"startup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.log");
        StartupLog log = new(directoryPath, filePath);
        log.Stage("Startup log created before MainWindow construction.");
        return log;
    }

    public void Stage(string message) => Write("STAGE", message);

    public void Exception(string source, Exception exception) =>
        Write("EXCEPTION", $"{source}:{Environment.NewLine}{exception}");

    public void WriteEnvironment(bool softwareRenderingActive)
    {
        Assembly? entryAssembly = Assembly.GetEntryAssembly();
        string baseDirectory = AppContext.BaseDirectory;
        string packagedDllPath = Path.Combine(baseDirectory, "native", "ftd2xx.dll");
        bool packagedDllPresent = File.Exists(packagedDllPath);

        Write("ENVIRONMENT", $"Timestamp UTC: {DateTimeOffset.UtcNow:O}");
        Write("ENVIRONMENT", $"Application version: {entryAssembly?.GetName().Version?.ToString() ?? "Unknown"}");
        Write("ENVIRONMENT", $"Process architecture: {RuntimeInformation.ProcessArchitecture}; 64-bit process: {Environment.Is64BitProcess}");
        Write("ENVIRONMENT", $"OS version: {RuntimeInformation.OSDescription}; {Environment.OSVersion.VersionString}");
        Write("ENVIRONMENT", $".NET runtime: {RuntimeInformation.FrameworkDescription}");
        Write("ENVIRONMENT", $"Current directory: {Environment.CurrentDirectory}");
        Write("ENVIRONMENT", $"Application base directory: {baseDirectory}");
        Write("ENVIRONMENT", $"Executable path: {Environment.ProcessPath ?? "Unavailable"}");
        Write("ENVIRONMENT", $"Packaged DLL present: {packagedDllPresent}; path: {packagedDllPath}");
        Write("ENVIRONMENT", $"Packaged DLL SHA-256: {(packagedDllPresent ? ComputeSha256(packagedDllPath) : "Unavailable")}");
        Write("ENVIRONMENT", $"WPF rendering tier: {RenderCapability.Tier >> 16}; software rendering active: {softwareRenderingActive}");
    }

    private void Write(string category, string message)
    {
        string entry = $"{DateTimeOffset.UtcNow:O} [{category}] {message}{Environment.NewLine}";
        lock (_gate)
        {
            File.AppendAllText(FilePath, entry, Encoding.UTF8);
        }

        Trace.Write(entry);
    }

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
