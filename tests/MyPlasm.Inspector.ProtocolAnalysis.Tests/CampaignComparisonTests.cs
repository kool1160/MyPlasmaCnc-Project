using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using MyPlasm.Inspector.ProtocolAnalysis;
using MyPlasm.ProtocolAnalyzer;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

public sealed class CampaignComparisonTests
{
    private static readonly string[] RequiredOutputs =
    [
        "campaign-summary.json",
        "campaign-report.md",
        "stable-transaction-classes.csv",
        "class-frequency-by-run.csv",
        "run-structure-comparison.csv",
        "hashes.sha256"
    ];

    [Fact]
    public async Task ThreeRunsReportStableTwoRunOneRunAndFrequencyDifferences()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");

        CampaignComparisonResult result =
            await new CampaignComparator().CompareAsync(
                new CampaignComparisonRequest(
                    fixture.AnalysisDirectories,
                    output));

        Assert.Equal(3, result.RunCount);
        Assert.Equal(1, result.StableTransactionClassCount);
        Assert.Equal(
            RequiredOutputs.Order(StringComparer.Ordinal),
            Directory.GetFiles(output)
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal));

        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(output, "campaign-summary.json")));
        JsonElement root = document.RootElement;
        Assert.Equal(3, root.GetProperty("run_count").GetInt32());
        JsonElement[] classes = root
            .GetProperty("transaction_classes")
            .EnumerateArray()
            .ToArray();
        AssertClass(classes, fixture.CommonClassId, 3, "all_three_runs", true);
        AssertClass(classes, fixture.TwoRunClassId, 2, "two_runs", false);
        AssertClass(classes, fixture.OneRunClassId, 1, "one_run", false);

        JsonElement common = classes.Single(item =>
            item.GetProperty("class_id").GetString() ==
            fixture.CommonClassId);
        Assert.Equal(
            [1L, 2L, 3L],
            common.GetProperty("runs")
                .EnumerateArray()
                .Select(item => item.GetProperty("count").GetInt64()));
        Assert.All(
            common.GetProperty("runs").EnumerateArray(),
            run =>
            {
                Assert.NotEmpty(
                    run.GetProperty("counts_by_session")
                        .EnumerateObject());
                Assert.Contains(
                    run.GetProperty("overlapping_phases")
                        .EnumerateArray()
                        .Select(item => item.GetString()),
                    phase => phase == "sustained_exchange_interval");
            });

        string stable = await File.ReadAllTextAsync(
            Path.Combine(output, "stable-transaction-classes.csv"));
        Assert.Contains(fixture.CommonClassId, stable, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.TwoRunClassId, stable, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.OneRunClassId, stable, StringComparison.Ordinal);

        string frequency = await File.ReadAllTextAsync(
            Path.Combine(output, "class-frequency-by-run.csv"));
        Assert.Contains($"{fixture.CommonClassId},all_three_runs", frequency);
        Assert.Contains($"{fixture.TwoRunClassId},two_runs", frequency);
        Assert.Contains($"{fixture.OneRunClassId},one_run", frequency);
    }

    [Fact]
    public async Task AllSixArgumentPermutationsProduceByteIdenticalOutputs()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        int[][] permutations =
        [
            [0, 1, 2],
            [0, 2, 1],
            [1, 0, 2],
            [1, 2, 0],
            [2, 0, 1],
            [2, 1, 0]
        ];
        Dictionary<string, byte[]>? baselineBytes = null;
        IReadOnlyDictionary<string, string>? baselineHashes = null;
        foreach ((int[] permutation, int index) in
                 permutations.Select((value, index) => (value, index)))
        {
            string output = workspace.PathFor($"comparison-{index + 1}");
            CampaignComparisonResult result =
                await new CampaignComparator().CompareAsync(
                    new CampaignComparisonRequest(
                        permutation
                            .Select(item => fixture.AnalysisDirectories[item])
                            .ToArray(),
                        output));
            await CampaignTestAssertions.AssertCompleteVerifiedSetAsync(output);
            await AssertSanitizedAsync(output, workspace.Root);

            using JsonDocument document = JsonDocument.Parse(
                await File.ReadAllTextAsync(
                    Path.Combine(output, "campaign-summary.json")));
            JsonElement[] runs = document.RootElement
                .GetProperty("runs")
                .EnumerateArray()
                .ToArray();
            Assert.Equal(
                ["run-1", "run-2", "run-3"],
                runs.Select(run =>
                    run.GetProperty("run_label").GetString()));
            string?[] orderedFingerprints = runs
                .Select(run =>
                    run.GetProperty("analysis_set_sha256").GetString())
                .ToArray();
            Assert.Equal(
                orderedFingerprints.Order(StringComparer.Ordinal),
                orderedFingerprints);

            Dictionary<string, byte[]> currentBytes =
                CampaignTestAssertions.SnapshotKnownReports(output);
            if (baselineBytes is null)
            {
                baselineBytes = currentBytes;
                baselineHashes = result.OutputSha256;
                continue;
            }

            Assert.Equal(baselineHashes, result.OutputSha256);
            foreach (string name in RequiredOutputs)
            {
                Assert.Equal(baselineBytes[name], currentBytes[name]);
            }
        }
    }

    [Fact]
    public async Task EveryGeneratedHashMatchesAndOutputsRemainSanitized()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        string output = workspace.PathFor("comparison");

        await new CampaignComparator().CompareAsync(
            new CampaignComparisonRequest(
                fixture.AnalysisDirectories,
                output));

        string manifest = await File.ReadAllTextAsync(
            Path.Combine(output, "hashes.sha256"));
        foreach (string line in manifest.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string hash = line[..64];
            string name = line[66..];
            Assert.Equal(
                hash,
                await CaptureAnalyzer.CalculateSha256Async(
                    Path.Combine(output, name)));
        }

        string[] prohibited =
        [
            "A1B2",
            "C3D4",
            "0102",
            "0304",
            "11111111-2222-3333-4444-555555555555",
            "SYNTHETIC-DEVICE",
            "write_hex",
            "read_hex",
            "session_id",
            "selector_pointer",
            "serial_number",
            "0x00002000",
            workspace.Root
        ];
        foreach (string path in Directory.GetFiles(output))
        {
            string contents = await File.ReadAllTextAsync(path);
            Assert.All(
                prohibited,
                value => Assert.DoesNotContain(
                    value,
                    contents,
                    StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ComparisonAssemblyHasNoHardwareNativeOrTransportDependency()
    {
        Assembly assembly = typeof(CampaignComparator).Assembly;
        string[] references = assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();
        Assert.DoesNotContain(
            references,
            name => name.Contains("Transport", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("D2xx", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("PresentationFramework", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("WindowsBase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            assembly.GetTypes().SelectMany(type => type.GetMethods(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static |
                BindingFlags.Instance)),
            method => method.GetCustomAttribute<DllImportAttribute>() is not null);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task ExactlyThreeAnalysisDirectoriesAreRequired(int count)
    {
        using TestWorkspace workspace = new();
        string[] directories = Enumerable.Range(0, count)
            .Select(index =>
            {
                string path = workspace.PathFor($"input-{index}");
                Directory.CreateDirectory(path);
                return path;
            })
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(
            () => new CampaignComparator().CompareAsync(
                new CampaignComparisonRequest(
                    directories,
                    workspace.PathFor("comparison"))));
    }

    [Fact]
    public async Task CompareCliRequiresThreeInputsAndRunsEndToEnd()
    {
        using TestWorkspace workspace = new();
        SyntheticCampaignFixture fixture =
            await SyntheticAnalysisCampaign.CreateAsync(workspace);
        StringWriter output = new();
        StringWriter error = new();

        int usage = await AnalyzerCli.RunAsync(
            [
                "compare",
                "--analysis",
                fixture.AnalysisDirectories[0],
                "--analysis",
                fixture.AnalysisDirectories[1],
                "--output",
                workspace.PathFor("missing-third")
            ],
            output,
            error);
        Assert.Equal(2, usage);
        Assert.Contains("Exactly three", error.ToString(), StringComparison.Ordinal);

        output.GetStringBuilder().Clear();
        error.GetStringBuilder().Clear();
        int success = await AnalyzerCli.RunAsync(
            [
                "compare",
                "--analysis",
                fixture.AnalysisDirectories[2],
                "--analysis",
                fixture.AnalysisDirectories[0],
                "--analysis",
                fixture.AnalysisDirectories[1],
                "--output",
                workspace.PathFor("comparison")
            ],
            output,
            error);
        Assert.Equal(0, success);
        Assert.Empty(error.ToString());
        Assert.Contains("3 verified runs", output.ToString());
        Assert.DoesNotContain(workspace.Root, await File.ReadAllTextAsync(
            workspace.PathFor("comparison/campaign-report.md")));
    }

    private static void AssertClass(
        IEnumerable<JsonElement> classes,
        string classId,
        int presence,
        string presenceLabel,
        bool stable)
    {
        JsonElement item = classes.Single(value =>
            value.GetProperty("class_id").GetString() == classId);
        Assert.Equal(presence, item.GetProperty("present_in_runs").GetInt32());
        Assert.Equal(presenceLabel, item.GetProperty("presence").GetString());
        Assert.Equal(
            stable,
            item.GetProperty("stable_across_all_three_captures").GetBoolean());
    }

    private static async Task AssertSanitizedAsync(
        string output,
        string localPath)
    {
        string[] prohibited =
        [
            "A1B2",
            "C3D4",
            "0102",
            "0304",
            "11111111-2222-3333-4444-555555555555",
            "SYNTHETIC-DEVICE",
            "write_hex",
            "read_hex",
            "session_id",
            "selector_pointer",
            "serial_number",
            "0x00002000",
            localPath
        ];
        foreach (string path in Directory.GetFiles(output))
        {
            string contents = await File.ReadAllTextAsync(path);
            Assert.All(
                prohibited,
                value => Assert.DoesNotContain(
                    value,
                    contents,
                    StringComparison.OrdinalIgnoreCase));
        }
    }
}
