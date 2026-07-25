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
    internal const int FallbackEntryCapacity = 256;
    internal const int MaximumFallbackEntryLength = 8192;
    internal const string FileLoggingUnavailableMessage =
        "Startup file logging unavailable. Diagnostics remain available in the application session.";

    private const string LogDirectoryName = "MyPlasm Inspector\\Logs";
    private readonly Queue<string> _fallbackEntries = new();
    private readonly object _gate = new();
    private readonly IStartupLogPlatform _platform;
    private bool _persistentFileLoggingAvailable;
    private string? _persistentLoggingFailure;

    private StartupLog(IStartupLogPlatform platform)
    {
        _platform = platform;
    }

    public string? DirectoryPath { get; private set; }

    public string? FilePath { get; private set; }

    public bool IsPersistentFileLoggingAvailable
    {
        get
        {
            lock (_gate)
            {
                return _persistentFileLoggingAvailable;
            }
        }
    }

    public string? PersistentLoggingFailure
    {
        get
        {
            lock (_gate)
            {
                return _persistentLoggingFailure;
            }
        }
    }

    public string DiagnosticLocation =>
        IsPersistentFileLoggingAvailable && !string.IsNullOrWhiteSpace(FilePath)
            ? FilePath
            : FileLoggingUnavailableMessage;

    public static StartupLog CreateSafe() => CreateSafe(SystemStartupLogPlatform.Instance);

    internal static StartupLog CreateSafe(IStartupLogPlatform platform)
    {
        StartupLog log = new(platform ?? SystemStartupLogPlatform.Instance);
        log.InitializePersistentLogging();
        log.Stage("Startup logger initialized before MainWindow construction.");
        return log;
    }

    public void Stage(string message) => WriteNoThrow("STAGE", message);

    public void Exception(string source, Exception exception)
    {
        string details;
        try
        {
            details = exception?.ToString() ?? "No exception details were available.";
        }
        catch (Exception formattingException)
        {
            details = $"Exception details were unavailable ({DescribeFailure(formattingException)}).";
        }

        WriteNoThrow("EXCEPTION", $"{source}:{Environment.NewLine}{details}");
    }

    public void WriteEnvironment(bool softwareRenderingActive)
    {
        try
        {
            WriteProbe("Timestamp UTC", static () => DateTimeOffset.UtcNow.ToString("O"));
            WriteProbe("Application version", _platform.GetApplicationVersion);
            WriteProbe("Process architecture", _platform.GetProcessDescription);
            WriteProbe("OS version", _platform.GetOperatingSystemDescription);
            WriteProbe(".NET runtime", _platform.GetRuntimeDescription);
            WriteProbe("Current directory", _platform.GetCurrentDirectory);
            WriteProbe("Application base directory", _platform.GetApplicationBaseDirectory);
            WriteProbe("Executable path", _platform.GetExecutablePath);
            WritePackagedDllEvidence();
            WriteProbe(
                "WPF rendering",
                () => $"tier: {_platform.GetRenderingTier()}; software rendering active: {softwareRenderingActive}");
        }
        catch (Exception exception)
        {
            WriteNoThrow(
                "ENVIRONMENT",
                $"Unexpected environment inspection failure: {DescribeFailure(exception)}");
        }
    }

    internal IReadOnlyList<string> GetFallbackEntries()
    {
        lock (_gate)
        {
            return _fallbackEntries.ToArray();
        }
    }

    private void InitializePersistentLogging()
    {
        try
        {
            string localApplicationData = _platform.GetLocalApplicationDataPath();
            if (string.IsNullOrWhiteSpace(localApplicationData))
            {
                throw new InvalidOperationException("The local application-data path is unavailable.");
            }

            string directoryPath = Path.Combine(localApplicationData, LogDirectoryName);
            _platform.CreateDirectory(directoryPath);
            string filePath = Path.Combine(
                directoryPath,
                $"startup-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}.log");

            lock (_gate)
            {
                DirectoryPath = directoryPath;
                FilePath = filePath;
                _persistentFileLoggingAvailable = true;
            }
        }
        catch (Exception exception)
        {
            DisablePersistentLoggingNoThrow(exception);
        }
    }

    private void WritePackagedDllEvidence()
    {
        string? baseDirectory = TryProbe(_platform.GetApplicationBaseDirectory, out string baseFailure);
        if (baseDirectory is null)
        {
            WriteNoThrow("ENVIRONMENT", $"Packaged DLL presence: Unavailable ({baseFailure})");
            WriteNoThrow("ENVIRONMENT", $"Packaged DLL SHA-256: Unavailable ({baseFailure})");
            return;
        }

        string packagedDllPath;
        try
        {
            packagedDllPath = Path.Combine(baseDirectory, "native", "ftd2xx.dll");
        }
        catch (Exception exception)
        {
            string failure = DescribeFailure(exception);
            WriteNoThrow("ENVIRONMENT", $"Packaged DLL presence: Unavailable ({failure})");
            WriteNoThrow("ENVIRONMENT", $"Packaged DLL SHA-256: Unavailable ({failure})");
            return;
        }

        bool packagedDllPresent;
        try
        {
            packagedDllPresent = _platform.FileExists(packagedDllPath);
            WriteNoThrow(
                "ENVIRONMENT",
                $"Packaged DLL present: {packagedDllPresent}; path: {packagedDllPath}");
        }
        catch (Exception exception)
        {
            string failure = DescribeFailure(exception);
            WriteNoThrow("ENVIRONMENT", $"Packaged DLL presence: Unavailable ({failure})");
            WriteNoThrow("ENVIRONMENT", $"Packaged DLL SHA-256: Unavailable ({failure})");
            return;
        }

        if (!packagedDllPresent)
        {
            WriteNoThrow("ENVIRONMENT", "Packaged DLL SHA-256: Unavailable");
            return;
        }

        WriteProbe("Packaged DLL SHA-256", () => _platform.ComputeSha256(packagedDllPath));
    }

    private void WriteProbe(string label, Func<string> probe)
    {
        string? value = TryProbe(probe, out string failure);
        WriteNoThrow(
            "ENVIRONMENT",
            value is null ? $"{label}: Unavailable ({failure})" : $"{label}: {value}");
    }

    private static string? TryProbe(Func<string> probe, out string failure)
    {
        try
        {
            string value = probe();
            if (string.IsNullOrWhiteSpace(value))
            {
                failure = "No value was available.";
                return null;
            }

            failure = string.Empty;
            return value;
        }
        catch (Exception exception)
        {
            failure = DescribeFailure(exception);
            return null;
        }
    }

    private void WriteNoThrow(string category, string? message)
    {
        string entry;
        try
        {
            entry = $"{DateTimeOffset.UtcNow:O} [{category}] {message ?? string.Empty}{Environment.NewLine}";
        }
        catch (Exception exception)
        {
            entry = $"[LOGGER] Entry formatting failed: {DescribeFailure(exception)}{Environment.NewLine}";
        }

        string? failureEntry = null;
        try
        {
            lock (_gate)
            {
                AddFallbackEntryNoThrow(entry);
                if (_persistentFileLoggingAvailable && FilePath is not null)
                {
                    try
                    {
                        _platform.AppendAllText(FilePath, entry);
                    }
                    catch (Exception exception)
                    {
                        failureEntry = DisablePersistentLoggingUnderLockNoThrow(exception);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            failureEntry = $"[LOGGER] Diagnostic buffering failed: {DescribeFailure(exception)}";
        }

        TraceNoThrow(entry);
        if (failureEntry is not null)
        {
            TraceNoThrow(failureEntry);
        }
    }

    private void DisablePersistentLoggingNoThrow(Exception exception)
    {
        string? failureEntry;
        try
        {
            lock (_gate)
            {
                failureEntry = DisablePersistentLoggingUnderLockNoThrow(exception);
            }
        }
        catch
        {
            failureEntry = null;
        }

        if (failureEntry is not null)
        {
            TraceNoThrow(failureEntry);
        }
    }

    private string? DisablePersistentLoggingUnderLockNoThrow(Exception exception)
    {
        if (!_persistentFileLoggingAvailable && _persistentLoggingFailure is not null)
        {
            return null;
        }

        _persistentFileLoggingAvailable = false;
        _persistentLoggingFailure ??= DescribeFailure(exception);
        string failureEntry =
            $"{DateTimeOffset.UtcNow:O} [LOGGER] Persistent startup file logging disabled: " +
            $"{_persistentLoggingFailure}{Environment.NewLine}";
        AddFallbackEntryNoThrow(failureEntry);
        return failureEntry;
    }

    private void AddFallbackEntryNoThrow(string entry)
    {
        try
        {
            string boundedEntry = entry.Length <= MaximumFallbackEntryLength
                ? entry
                : string.Concat(
                    entry.AsSpan(0, MaximumFallbackEntryLength - 32),
                    "... [diagnostic entry truncated]");

            while (_fallbackEntries.Count >= FallbackEntryCapacity)
            {
                _fallbackEntries.Dequeue();
            }

            _fallbackEntries.Enqueue(boundedEntry);
        }
        catch
        {
            // Logging must never become an application failure path.
        }
    }

    private void TraceNoThrow(string entry)
    {
        try
        {
            _platform.WriteTrace(entry);
        }
        catch
        {
            // Trace listeners are diagnostic sinks and may fail independently.
        }
    }

    private static string DescribeFailure(Exception exception)
    {
        try
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
        catch
        {
            return "Unknown logging failure.";
        }
    }
}

internal interface IStartupLogPlatform
{
    string GetLocalApplicationDataPath();

    void CreateDirectory(string path);

    void AppendAllText(string path, string entry);

    void WriteTrace(string entry);

    string GetApplicationVersion();

    string GetProcessDescription();

    string GetOperatingSystemDescription();

    string GetRuntimeDescription();

    string GetCurrentDirectory();

    string GetApplicationBaseDirectory();

    string GetExecutablePath();

    bool FileExists(string path);

    string ComputeSha256(string path);

    int GetRenderingTier();
}

internal sealed class SystemStartupLogPlatform : IStartupLogPlatform
{
    public static SystemStartupLogPlatform Instance { get; } = new();

    private SystemStartupLogPlatform()
    {
    }

    public string GetLocalApplicationDataPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void AppendAllText(string path, string entry) =>
        File.AppendAllText(path, entry, Encoding.UTF8);

    public void WriteTrace(string entry) => Trace.Write(entry);

    public string GetApplicationVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "Unknown";

    public string GetProcessDescription() =>
        $"{RuntimeInformation.ProcessArchitecture}; 64-bit process: {Environment.Is64BitProcess}";

    public string GetOperatingSystemDescription() =>
        $"{RuntimeInformation.OSDescription}; {Environment.OSVersion.VersionString}";

    public string GetRuntimeDescription() => RuntimeInformation.FrameworkDescription;

    public string GetCurrentDirectory() => Environment.CurrentDirectory;

    public string GetApplicationBaseDirectory() => AppContext.BaseDirectory;

    public string GetExecutablePath() => Environment.ProcessPath ?? "Unavailable";

    public bool FileExists(string path) => File.Exists(path);

    public string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public int GetRenderingTier() => RenderCapability.Tier >> 16;
}
