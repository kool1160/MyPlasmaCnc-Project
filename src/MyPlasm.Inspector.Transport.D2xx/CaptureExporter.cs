using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPlasm.Inspector.Core.Transport;

namespace MyPlasm.Inspector.Transport.D2xx;

public sealed record CaptureExportContext(
    string ApplicationName,
    string ApplicationVersion,
    string SourceCommit,
    string ProcessArchitecture,
    string OsVersion,
    string RuntimeVersion,
    string RenderingMode,
    string D2xxDllRelativePath,
    PeInspectionResult? DllInspection,
    string? D2xxLibraryVersion,
    string? StartupLogPath);

public sealed record CaptureExportResult(
    string CaptureDirectory,
    string ZipPath,
    string ZipSha256);

public sealed class CaptureExporter
{
    private static readonly string[] EvidenceFileNames =
    [
        "events.jsonl",
        "report.txt",
        "rx-hex.txt",
        "rx.bin",
        "session.json",
        "source-commit.txt",
        "startup.log"
    ];

    private static readonly JsonSerializerOptions DocumentJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonLineOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ICaptureArchiveWriter _archiveWriter;

    public CaptureExporter()
        : this(new ZipCaptureArchiveWriter())
    {
    }

    internal CaptureExporter(ICaptureArchiveWriter archiveWriter)
    {
        _archiveWriter =
            archiveWriter ?? throw new ArgumentNullException(nameof(archiveWriter));
    }

    public CaptureExportResult Export(
        PassiveCaptureResult capture,
        CaptureExportContext context,
        string destinationRoot)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRoot);

        _ = NormalizeRelativeEvidencePath(context.D2xxDllRelativePath);
        string sourceCommit = NormalizeSourceCommit(context.SourceCommit);
        ValidateEventSequence(capture.Events);
        string root = Path.GetFullPath(destinationRoot);
        Directory.CreateDirectory(root);
        string exportId =
            $"capture-{capture.StartedUtc:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}";
        string folder = Path.Combine(root, exportId);
        string zipPath = folder + ".zip";
        string stagedZipPath =
            Path.Combine(root, $".{exportId}.staging-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(folder);

        try
        {
            // Raw bytes are written first and never reconstructed from formatted
            // output. A later failure leaves this unique evidence directory intact.
            WriteRawBytes(folder, capture.Chunks);
            WriteHex(folder, capture.Chunks);
            WriteText(folder, "source-commit.txt", sourceCommit + "\n");

            CaptureSessionDocument session =
                CreateSessionDocument(capture, context);
            WriteText(
                folder,
                "session.json",
                JsonSerializer.Serialize(session, DocumentJsonOptions));
            WriteEvents(folder, capture.Events);
            WriteText(folder, "report.txt", CreateReport(session));
            CopyStartupLogOrWriteDiagnostic(folder, context.StartupLogPath);
            WriteHashManifest(folder);

            _archiveWriter.CreateFromDirectory(folder, stagedZipPath);
            ValidateArchive(folder, stagedZipPath);
            string zipSha256 = ComputeSha256(stagedZipPath);
            File.Move(stagedZipPath, zipPath);

            return new CaptureExportResult(
                folder,
                zipPath,
                zipSha256);
        }
        catch
        {
            TryDeleteGeneratedStagingArchive(stagedZipPath, root);
            throw;
        }
    }

    private static CaptureSessionDocument CreateSessionDocument(
        PassiveCaptureResult capture,
        CaptureExportContext context)
    {
        FtdiDeviceInfo device = capture.SelectedDevice;
        return new CaptureSessionDocument(
            context.ApplicationName,
            context.ApplicationVersion,
            NormalizeSourceCommit(context.SourceCommit),
            context.ProcessArchitecture,
            context.OsVersion,
            context.RuntimeVersion,
            context.RenderingMode,
            NormalizeRelativeEvidencePath(context.D2xxDllRelativePath),
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
            capture.ElapsedDuration,
            capture.TotalBytes,
            capture.Chunks.Count,
            capture.QueuePollCount,
            capture.CaptureByteLimit,
            capture.CaptureEventLimit,
            capture.CaptureChunkLimit,
            capture.Errors,
            capture.CloseStatus?.ToString() ?? "Not closed",
            capture.CloseFailure,
            0,
            0);
    }

    private static string CreateReport(CaptureSessionDocument session) =>
        $"""
        MYPLASM INSPECTOR PASSIVE RECEIVE EVIDENCE
        ==========================================
        Selected device: {SingleLine(session.Description)}
        Serial: {SingleLine(session.SerialNumber)}
        Source commit: {session.SourceCommit}
        Opened UTC: {session.OpenTimestampUtc:O}
        Capture started UTC: {session.CaptureStartTimestampUtc:O}
        Capture stopped UTC: {session.CaptureStopTimestampUtc:O}
        Closed UTC: {session.CloseTimestampUtc:O}
        Stop reason: {SingleLine(session.StopReason)}
        Duration: {session.Duration}
        Total received bytes: {session.TotalBytes}
        Read chunks: {session.ReadChunkCount}
        Queue polls: {session.QueuePollCount}
        Capture byte limit: {session.CaptureByteLimit}
        Capture event limit: {session.CaptureEventLimit}
        Capture chunk limit: {session.CaptureChunkLimit}
        Last close status: {session.CloseStatus}
        Close failure: {SingleLine(session.CloseFailure ?? "None")}
        Transmit count: 0
        Production allowlist count: 0 (empty)
        """;

    private static void WriteRawBytes(
        string folder,
        IReadOnlyList<PassiveCaptureChunk> chunks)
    {
        using FileStream output = new(
            Path.Combine(folder, "rx.bin"),
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read);
        foreach (PassiveCaptureChunk chunk in chunks)
        {
            output.Write(chunk.Bytes);
        }
    }

    private static void WriteHex(
        string folder,
        IReadOnlyList<PassiveCaptureChunk> chunks)
    {
        using StreamWriter output = new(
            new FileStream(
                Path.Combine(folder, "rx-hex.txt"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read),
            new UTF8Encoding(false));
        long offset = 0;
        byte[] line = new byte[16];
        int lineLength = 0;
        foreach (PassiveCaptureChunk chunk in chunks)
        {
            foreach (byte value in chunk.Bytes)
            {
                line[lineLength++] = value;
                if (lineLength == line.Length)
                {
                    WriteHexLine(output, offset, line.AsSpan(0, lineLength));
                    offset += lineLength;
                    lineLength = 0;
                }
            }
        }

        if (lineLength > 0)
        {
            WriteHexLine(output, offset, line.AsSpan(0, lineLength));
        }
        else if (offset == 0)
        {
            output.WriteLine("ZERO RECEIVED BYTES");
        }
    }

    private static void WriteHexLine(
        TextWriter output,
        long offset,
        ReadOnlySpan<byte> bytes)
    {
        output.Write(offset.ToString("X8"));
        output.Write("  ");
        output.WriteLine(Convert.ToHexString(bytes).ToLowerInvariant());
    }

    private static void WriteEvents(
        string folder,
        IReadOnlyList<PassiveSessionEvent> events)
    {
        using StreamWriter output = new(
            new FileStream(
                Path.Combine(folder, "events.jsonl"),
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read),
            new UTF8Encoding(false));
        foreach (PassiveSessionEvent item in events)
        {
            output.WriteLine(JsonSerializer.Serialize(item, JsonLineOptions));
        }
    }

    private static void CopyStartupLogOrWriteDiagnostic(
        string folder,
        string? startupLogPath)
    {
        string destination = Path.Combine(folder, "startup.log");
        if (string.IsNullOrWhiteSpace(startupLogPath))
        {
            WriteText(
                folder,
                "startup.log",
                "Startup file logging was unavailable for this application session.");
            return;
        }

        try
        {
            using FileStream source = new(
                startupLogPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using FileStream output = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read);
            source.CopyTo(output);
        }
        catch (Exception exception)
            when (exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException or
                ArgumentException)
        {
            WriteText(
                folder,
                "startup.log",
                "Startup log copy unavailable: " +
                $"{exception.GetType().Name}. The source path was not exported.");
        }
    }

    private static void WriteHashManifest(string folder)
    {
        string manifest = string.Join(
            "\n",
            EvidenceFileNames.Select(
                name => $"{ComputeSha256(Path.Combine(folder, name))}  {name}")) +
            "\n";
        WriteText(folder, "hashes.sha256", manifest);
    }

    private static void ValidateArchive(
        string sourceDirectory,
        string zipPath)
    {
        if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
        {
            throw new InvalidDataException(
                "The passive evidence ZIP is missing or empty.");
        }

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        string[] expectedNames = EvidenceFileNames
            .Append("hashes.sha256")
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] actualNames = archive.Entries
            .Select(entry => entry.FullName.Replace('\\', '/'))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (!actualNames.SequenceEqual(expectedNames, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                "The passive evidence ZIP does not contain the exact required file set.");
        }

        foreach (string name in expectedNames)
        {
            ZipArchiveEntry[] matches = archive.Entries
                .Where(entry => string.Equals(
                    entry.FullName.Replace('\\', '/'),
                    name,
                    StringComparison.Ordinal))
                .ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException(
                    $"The passive evidence ZIP contains an ambiguous entry: {name}.");
            }

            string sourceHash = ComputeSha256(Path.Combine(sourceDirectory, name));
            using Stream stream = matches[0].Open();
            string archiveHash = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(sourceHash, archiveHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The passive evidence ZIP hash does not match: {name}.");
            }
        }
    }

    private static string NormalizeRelativeEvidencePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (Path.IsPathRooted(path))
        {
            throw new ArgumentException(
                "D2XX evidence path must be package-relative.",
                nameof(path));
        }

        string normalized = path.Replace('\\', '/');
        if (normalized.Split('/').Any(
            part => part.Length == 0 || part is "." or ".."))
        {
            throw new ArgumentException(
                "D2XX evidence path must be a safe package-relative path.",
                nameof(path));
        }

        return normalized;
    }

    private static string NormalizeSourceCommit(string sourceCommit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCommit);
        if (string.Equals(sourceCommit, "Unknown", StringComparison.Ordinal))
        {
            return sourceCommit;
        }

        if (sourceCommit.Length != 40 ||
            sourceCommit.Any(value => !Uri.IsHexDigit(value)))
        {
            throw new ArgumentException(
                "Source commit must be Unknown or one exact 40-character Git SHA.",
                nameof(sourceCommit));
        }

        return sourceCommit.ToLowerInvariant();
    }

    private static void ValidateEventSequence(
        IReadOnlyList<PassiveSessionEvent> events)
    {
        for (int index = 0; index < events.Count; index++)
        {
            long expected = checked(index + 1L);
            if (events[index].Sequence != expected)
            {
                throw new InvalidDataException(
                    $"Passive event sequence must be contiguous from 1; expected {expected}, found {events[index].Sequence}.");
            }
        }
    }

    private static string SingleLine(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ');

    private static void WriteText(
        string folder,
        string name,
        string contents) =>
        File.WriteAllText(
            Path.Combine(folder, name),
            contents,
            new UTF8Encoding(false));

    private static string ComputeSha256(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void TryDeleteGeneratedStagingArchive(
        string stagedZipPath,
        string expectedRoot)
    {
        try
        {
            string fullStage = Path.GetFullPath(stagedZipPath);
            string fullRoot = Path.GetFullPath(expectedRoot).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (fullStage.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullStage).StartsWith(
                    ".capture-",
                    StringComparison.Ordinal) &&
                File.Exists(fullStage))
            {
                File.Delete(fullStage);
            }
        }
        catch
        {
            // The unique capture directory remains the authoritative evidence.
        }
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
        string SourceCommit,
        string ProcessArchitecture,
        string OsVersion,
        string RuntimeVersion,
        string RenderingMode,
        string D2xxDllRelativePath,
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
        long TotalBytes,
        int ReadChunkCount,
        int QueuePollCount,
        long CaptureByteLimit,
        int CaptureEventLimit,
        int CaptureChunkLimit,
        IReadOnlyList<string> D2xxErrors,
        string CloseStatus,
        string? CloseFailure,
        int TransmitCount,
        int ProductionAllowlistCount);
}

internal interface ICaptureArchiveWriter
{
    void CreateFromDirectory(
        string sourceDirectory,
        string destinationZipPath);
}

internal sealed class ZipCaptureArchiveWriter : ICaptureArchiveWriter
{
    public void CreateFromDirectory(
        string sourceDirectory,
        string destinationZipPath) =>
        ZipFile.CreateFromDirectory(
            sourceDirectory,
            destinationZipPath,
            CompressionLevel.Optimal,
            false);
}
