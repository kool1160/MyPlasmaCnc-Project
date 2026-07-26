using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

internal sealed record SyntheticCampaignFixture(
    string[] AnalysisDirectories,
    string CommonClassId,
    string TwoRunClassId,
    string OneRunClassId);

internal static class SyntheticAnalysisCampaign
{
    public static async Task<SyntheticCampaignFixture> CreateAsync(
        TestWorkspace workspace)
    {
        string[] directories = new string[3];
        string? commonClass = null;
        string? twoRunClass = null;
        string? oneRunClass = null;
        for (int run = 0; run < 3; run++)
        {
            SyntheticCapture capture = new();
            capture.ListDevices();
            capture.Open(100UL + (ulong)run);

            for (int occurrence = 0; occurrence <= run; occurrence++)
            {
                capture.Write(100UL + (ulong)run, "A1B2");
                capture.Queue(100UL + (ulong)run, (uint)(occurrence + 1));
                capture.Read(100UL + (ulong)run, "C3D4", 2);
            }

            if (run < 2)
            {
                capture.Write(100UL + (ulong)run, "0102");
                capture.Read(100UL + (ulong)run, "0304", 2);
            }

            if (run == 0)
            {
                capture.Write(100UL + (ulong)run, "1112");
                capture.Read(100UL + (ulong)run, "1314", 2);
            }

            capture.Close(100UL + (ulong)run);
            string input = await capture.WriteAsync(
                workspace.PathFor($"synthetic-input-{run + 1}.jsonl"));
            string directory = workspace.PathFor($"analysis-{run + 1}");
            await new CaptureAnalyzer().AnalyzeAsync(
                new AnalysisRequest(input, directory));
            directories[run] = directory;

            string[] classIds = (await File.ReadAllLinesAsync(
                    Path.Combine(directory, "transaction-classes.csv")))
                .Skip(1)
                .Where(line => line.Length > 0)
                .Select(line => line.Split(',')[0])
                .ToArray();
            commonClass ??= classIds.Single(id =>
                id == TransactionClassId("A1B2", "C3D4"));
            if (run < 2)
            {
                twoRunClass ??= classIds.Single(id =>
                    id == TransactionClassId("0102", "0304"));
            }

            if (run == 0)
            {
                oneRunClass = classIds.Single(id =>
                    id == TransactionClassId("1112", "1314"));
            }
        }

        return new SyntheticCampaignFixture(
            directories,
            commonClass!,
            twoRunClass!,
            oneRunClass!);
    }

    public static async Task RewriteJsonAndManifestAsync(
        string analysisDirectory,
        string fileName,
        Action<JsonObject> update)
    {
        string path = Path.Combine(analysisDirectory, fileName);
        JsonObject root = JsonNode.Parse(await File.ReadAllTextAsync(path))!
            .AsObject();
        update(root);
        await File.WriteAllTextAsync(
            path,
            root.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true
            }) + "\n",
            new UTF8Encoding(false));
        await RefreshManifestEntryAsync(analysisDirectory, fileName);
    }

    public static async Task RefreshManifestEntryAsync(
        string analysisDirectory,
        string fileName)
    {
        string hash = await CaptureAnalyzer.CalculateSha256Async(
            Path.Combine(analysisDirectory, fileName));
        string manifestPath = Path.Combine(
            analysisDirectory,
            "hashes.sha256");
        string[] lines = await File.ReadAllLinesAsync(manifestPath);
        bool replaced = false;
        for (int index = 0; index < lines.Length; index++)
        {
            if (!lines[index].EndsWith(
                    $"  {fileName}",
                    StringComparison.Ordinal))
            {
                continue;
            }

            lines[index] = $"{hash}  {fileName}";
            replaced = true;
        }

        if (!replaced)
        {
            throw new InvalidOperationException(
                $"Synthetic manifest entry was not found: {fileName}");
        }

        await File.WriteAllTextAsync(
            manifestPath,
            string.Join('\n', lines) + "\n",
            new UTF8Encoding(false));
    }

    private static string TransactionClassId(
        string writeHex,
        string readHex)
    {
        string writeClass =
            $"W-{writeHex.Length / 2}-{Sha256(Convert.FromHexString(writeHex))}";
        string readClass =
            $"R-{readHex.Length / 2}-{Sha256(Convert.FromHexString(readHex))}";
        return "T-" + Sha256(
            Encoding.UTF8.GetBytes($"{writeClass}\n{readClass}"));
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
}
