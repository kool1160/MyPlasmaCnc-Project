using System.IO.Compression;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using MyPlasm.Inspector.Transport.D2xx;

namespace MyPlasm.Inspector.App;

internal static class CaptureExporter
{
    public static string Export(PassiveCaptureResult capture, StartupLog startupLog)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MyPlasm Inspector", "Captures");
        string folder = Path.Combine(root, $"capture-{capture.StartedUtc:yyyyMMdd-HHmmss-fff}");
        Directory.CreateDirectory(folder);
        byte[] raw = capture.Chunks.SelectMany(chunk => chunk.Bytes).ToArray();
        File.WriteAllBytes(Path.Combine(folder, "rx.bin"), raw);
        File.WriteAllText(Path.Combine(folder, "rx-hex.txt"), Convert.ToHexString(raw));
        File.WriteAllText(Path.Combine(folder, "session.json"), JsonSerializer.Serialize(new { capture.StartedUtc, capture.StoppedUtc, TotalBytes = raw.Length, ChunkCount = capture.Chunks.Count, TransmitCount = 0, ProductionAllowlistCount = 0 }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllLines(Path.Combine(folder, "events.jsonl"), capture.Chunks.Select(chunk => JsonSerializer.Serialize(chunk)));
        File.WriteAllText(Path.Combine(folder, "report.txt"), $"PASSIVE RECEIVE ONLY\nBytes: {raw.Length}\nChunks: {capture.Chunks.Count}\nTransmit count: 0\nProduction allowlist: empty\n");
        File.Copy(startupLog.FilePath, Path.Combine(folder, "startup.log"), true);
        string[] files = Directory.GetFiles(folder).Where(path => !path.EndsWith("hashes.sha256", StringComparison.OrdinalIgnoreCase)).ToArray();
        File.WriteAllLines(Path.Combine(folder, "hashes.sha256"), files.Select(path => $"{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))}  {Path.GetFileName(path)}"));
        string zip = folder + ".zip";
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, false);
        return zip;
    }
}
