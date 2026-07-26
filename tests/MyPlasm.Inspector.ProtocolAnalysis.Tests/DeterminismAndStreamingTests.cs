using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using MyPlasm.Inspector.ProtocolAnalysis;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class DeterminismAndStreamingTests
{
    [Fact]
    public async Task SameInputProducesByteForByteIdenticalOutputs()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = BuildVariableCapture();
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("first")));
        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("second")));

        string[] firstFiles = Directory.GetFiles(workspace.PathFor("first"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        string[] secondFiles = Directory.GetFiles(workspace.PathFor("second"))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        Assert.Equal(firstFiles, secondFiles);
        foreach (string fileName in firstFiles)
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(workspace.PathFor($"first/{fileName}")),
                await File.ReadAllBytesAsync(workspace.PathFor($"second/{fileName}")));
        }
    }

    [Fact]
    public async Task VariabilityAndPercentilesFollowDocumentedRules()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = BuildVariableCapture();
        string input = await capture.WriteAsync(workspace.PathFor("traffic.jsonl"));

        await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis")));

        using JsonDocument variability = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/payload-variability.json")));
        JsonElement writeFamily = variability.RootElement
            .GetProperty("same_length_families")
            .EnumerateArray()
            .Single(item => item.GetProperty("family_id").GetString() == "W-3");
        Assert.Equal(1, writeFamily.GetProperty("fixed_prefix_length").GetInt32());
        Assert.Equal(1, writeFamily.GetProperty("fixed_suffix_length").GetInt32());
        JsonElement middle = writeFamily.GetProperty("positions")[1];
        Assert.Equal(2, middle.GetProperty("unique_value_count").GetInt32());
        Assert.Equal(0.970951, middle.GetProperty("entropy_bits").GetDouble(), precision: 6);

        using JsonDocument summary = JsonDocument.Parse(
            await File.ReadAllTextAsync(workspace.PathFor("analysis/capture-summary.json")));
        JsonElement latency = summary.RootElement
            .GetProperty("transactions")
            .GetProperty("latency_microseconds");
        Assert.Equal(3_000, latency.GetProperty("median").GetInt32());
        Assert.Equal(5_000, latency.GetProperty("p95").GetInt32());
        Assert.Equal(3_000, latency.GetProperty("mean").GetInt32());
        Assert.Equal(
            4L,
            summary.RootElement
                .GetProperty("function_cadence_microseconds")
                .GetProperty("FT_Write")
                .GetProperty("count")
                .GetInt64());
    }

    [Fact]
    public async Task AtLeastOneHundredTwentyThousandRecordsAreStreamedWithProgress()
    {
        using TestWorkspace workspace = new();
        SyntheticCapture capture = new();
        string input = capture.WriteLargeQueueCapture(
            workspace.PathFor("traffic.jsonl"),
            queueRecords: 120_000);
        List<AnalysisProgress> progress = [];

        AnalysisResult result = await new CaptureAnalyzer().AnalyzeAsync(
            new AnalysisRequest(input, workspace.PathFor("analysis")),
            new InlineProgress(progress.Add));

        Assert.Equal(120_002, result.RecordCount);
        Assert.Contains(progress, update => update.RecordsProcessed == 25_000);
        Assert.Contains(progress, update => update.RecordsProcessed == 100_000);
        Assert.Equal(120_002, progress[^1].RecordsProcessed);
    }

    [Fact]
    public void AnalysisAssemblyHasNoTransportUiOrNativeInteropDependency()
    {
        Assembly assembly = typeof(CaptureAnalyzer).Assembly;
        string[] references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();
        Assert.DoesNotContain(
            references,
            name => name.Contains("Transport", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("D2xx", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WindowsBase", StringComparison.OrdinalIgnoreCase));

        IEnumerable<MethodInfo> methods = assembly.GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.Instance));
        Assert.DoesNotContain(
            methods,
            method => method.GetCustomAttribute<DllImportAttribute>() is not null);
    }

    private static SyntheticCapture BuildVariableCapture()
    {
        SyntheticCapture capture = new();
        capture.Open(9);
        string[] writes = ["AA10CC", "AA20CC", "AA20CC", "AA20CC", "AA10CC"];
        for (int index = 0; index < writes.Length; index++)
        {
            capture.Write(9, writes[index]);
            for (int poll = 0; poll < index; poll++)
            {
                capture.Queue(9);
            }

            capture.Read(9, "DD30EE", 3);
        }

        capture.Close(9);
        return capture;
    }

    private sealed class InlineProgress(Action<AnalysisProgress> action)
        : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value) => action(value);
    }
}
