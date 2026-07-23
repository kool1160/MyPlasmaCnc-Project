using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Transport.D2xx;

public sealed record CaptureExportContext(
    string ApplicationName,
    string ApplicationVersion,
    string ProcessArchitecture,
    string OsVersion,
    string RuntimeVersion,
    string RenderingMode,
    string D2xxDllPath,
    PeInspectionResult? DllInspection,
    string? D2xxLibraryVersion,
    string StartupLogPath);

public sealed record CaptureExportResult(string CaptureDirectory, string ZipPath);

public sealed class CaptureExporter
{
    private readonly ICaptureArchiveWriter _archiveWriter;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public CaptureExporter(ICaptureArchiveWriter? archiveWriter = null)
    {
        _archiveWriter = archiveWriter ?? new ZipCaptureArchiveWriter();
    }

    public CaptureExportResult Export(
        PassiveCaptureResult capture,
        CaptureExportContext context,
        string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        string folder = Path.Combine(
            Path.GetFullPath(destinationRoot),
            $"capture-{capture.StartedUtc:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);

        // Write raw evidence first. If any later export stage fails, this directory
        // and the bytes already written are deliberately retained.
        byte[] raw = capture.Chunks.SelectMany(chunk => chunk.Bytes).ToArray();
        WriteBytes(folder, "rx.bin", raw);
        WriteText(folder, "rx-hex.txt", FormatHex(raw));

        CaptureSessionDocument session = CreateSessionDocument(capture, context);
        WriteText(folder, "session.json", JsonSerializer.Serialize(session, JsonOptions));
        WriteText(
            folder,
            "events.jsonl",
            string.Join(
                Environment.NewLine,
                capture.Events.Select(item => JsonSerializer.Serialize(item, JsonOptions))) + Environment.NewLine);
        WriteText(folder, "report.txt", CreateReport(session));

        if (File.Exists(context.StartupLogPath))
        {
            File.Copy(context.StartupLogPath, Path.Combine(folder, "startup.log"), true);
        }
        else
        {
            WriteText(folder, "startup.log", $"Startup log unavailable: {context.StartupLogPath}{Environment.NewLine}");
        }

        string[] evidenceFiles = Directory.GetFiles(folder)
            .Where(path => !path.EndsWith("hashes.sha256", StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
        string manifest = string.Join(
            Environment.NewLine,
            evidenceFiles.Select(path => $"{ComputeSha256(path)}  {Path.GetFileName(path)}")) + Environment.NewLine;
        WriteText(folder, "hashes.sha256", manifest);

        string zipPath = folder + ".zip";
        _archiveWriter.CreateFromDirectory(folder, zipPath);
        return new CaptureExportResult(folder, zipPath);
    }

    private static CaptureSessionDocument CreateSessionDocument(
        PassiveCaptureResult capture,
        CaptureExportContext context)
    {
        FtdiDeviceInfo device = capture.SelectedDevice;
        return new CaptureSessionDocument(
            context.ApplicationName,
            context.ApplicationVersion,
            context.ProcessArchitecture,
            context.OsVersion,
            context.RuntimeVersion,
            context.RenderingMode,
            context.D2xxDllPath,
            context.DllInspection?.DllArchitecture.ToString() ?? "Unknown",
            context.DllInspection?.FileVersion ?? "Unknown",
            context.DllInspection?.Sha256 ?? "Unknown",
            context.D2xxLibraryVersion ?? "Unknown",
            capture.DriverVersion ?? "Unknown",
            new SelectedDeviceDocument(
                device.Index,
                device.SerialNumber,
                device.Description,
                device.DeviceType,
                device.VendorId,
                device.ProductId,
                device.DeviceId,
                device.LocationId,
                device.IsOpen),
            device.SerialNumber,
            device.Description,
            device.DeviceType,
            device.VendorId,
            device.ProductId,
            device.LocationId,
            capture.OpenedUtc,
            capture.StartedUtc,
            capture.StoppedUtc,
            capture.ClosedUtc,
            capture.StopReason,
            capture.StoppedUtc - capture.StartedUtc,
            capture.TotalBytes,
            capture.Chunks.Count,
            capture.QueuePollCount,
            capture.Errors,
            capture.CloseStatus?.ToString() ?? "Not closed",
            0,
            0);
    }

    private static string CreateReport(CaptureSessionDocument session) =>
        $"""
        MYPLASM INSPECTOR PASSIVE RECEIVE EVIDENCE
        ==========================================
        Selected device: {session.Description}
        Serial: {session.SerialNumber}
        Opened UTC: {session.OpenTimestampUtc:O}
        Capture started UTC: {session.CaptureStartTimestampUtc:O}
        Capture stopped UTC: {session.CaptureStopTimestampUtc:O}
        Closed UTC: {session.CloseTimestampUtc:O}
        Stop reason: {session.StopReason}
        Duration: {session.Duration}
        Total received bytes: {session.TotalBytes}
        Read chunks: {session.ReadChunkCount}
        Queue polls: {session.QueuePollCount}
        Last close status: {session.CloseStatus}
        Transmit count: 0
        Production allowlist count: 0 (empty)
        """;

    private static string FormatHex(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return "ZERO RECEIVED BYTES" + Environment.NewLine;
        }

        StringBuilder output = new();
        for (int offset = 0; offset < bytes.Length; offset += 16)
        {
            ReadOnlySpan<byte> line = bytes.AsSpan(offset, Math.Min(16, bytes.Length - offset));
            output.Append(offset.ToString("X8"));
            output.Append("  ");
            output.AppendLine(Convert.ToHexString(line).ToLowerInvariant());
        }

        return output.ToString();
    }

    private static void WriteBytes(string folder, string name, byte[] bytes) =>
        File.WriteAllBytes(Path.Combine(folder, name), bytes);

    private static void WriteText(string folder, string name, string contents) =>
        File.WriteAllText(Path.Combine(folder, name), contents, new UTF8Encoding(false));

    private static string ComputeSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private sealed record SelectedDeviceDocument(
        uint Index,
        string SerialNumber,
        string Description,
        string DeviceType,
        ushort? VendorId,
        ushort? ProductId,
        uint? DeviceId,
        uint? LocationId,
        bool WasOpenAtEnumeration);

    private sealed record CaptureSessionDocument(
        string ApplicationName,
        string ApplicationVersion,
        string ProcessArchitecture,
        string OsVersion,
        string RuntimeVersion,
        string RenderingMode,
        string D2xxDllPath,
        string DllPeArchitecture,
        string DllFileVersion,
        string DllSha256,
        string D2xxLibraryVersion,
        string FtdiDriverVersion,
        SelectedDeviceDocument SelectedDevice,
        string SerialNumber,
        string Description,
        string DeviceType,
        ushort? VendorId,
        ushort? ProductId,
        uint? LocationId,
        DateTimeOffset? OpenTimestampUtc,
        DateTimeOffset CaptureStartTimestampUtc,
        DateTimeOffset CaptureStopTimestampUtc,
        DateTimeOffset? CloseTimestampUtc,
        string StopReason,
        TimeSpan Duration,
        int TotalBytes,
        int ReadChunkCount,
        int QueuePollCount,
        IReadOnlyList<string> D2xxErrors,
        string CloseStatus,
        int TransmitCount,
        int ProductionAllowlistCount);
}

public interface ICaptureArchiveWriter
{
    void CreateFromDirectory(string sourceDirectory, string destinationZipPath);
}

public sealed class ZipCaptureArchiveWriter : ICaptureArchiveWriter
{
    public void CreateFromDirectory(string sourceDirectory, string destinationZipPath) =>
        ZipFile.CreateFromDirectory(
            sourceDirectory,
            destinationZipPath,
            CompressionLevel.Optimal,
            false);
}
