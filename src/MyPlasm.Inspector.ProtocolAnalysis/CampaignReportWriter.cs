using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPlasm.Inspector.ProtocolAnalysis;

internal static class CampaignReportWriter
{
    internal static readonly string[] ReportFileNames =
    [
        "campaign-summary.json",
        "campaign-report.md",
        "stable-transaction-classes.csv",
        "class-frequency-by-run.csv",
        "run-structure-comparison.csv",
        "hashes.sha256"
    ];

    private static readonly UTF8Encoding Utf8WithoutBom =
        new(encoderShouldEmitUTF8Identifier: false);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static async Task<IReadOnlyDictionary<string, string>> WriteAsync(
        IReadOnlyList<string> analysisDirectories,
        string outputDirectory,
        bool overwrite,
        CampaignReportBundle reports,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = CampaignPathSafety.Validate(
                analysisDirectories,
                outputDirectory);
            if (Directory.Exists(outputDirectory))
            {
                if (!overwrite &&
                    Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                {
                    throw new AnalysisOutputException(
                        "The comparison output directory is not empty. Use --overwrite to replace campaign report files explicitly.");
                }
            }
            else
            {
                Directory.CreateDirectory(outputDirectory);
            }

            SortedDictionary<string, string> content = new(StringComparer.Ordinal)
            {
                ["campaign-summary.json"] =
                    JsonSerializer.Serialize(reports.Summary, JsonOptions) + "\n",
                ["campaign-report.md"] = BuildMarkdown(reports),
                ["stable-transaction-classes.csv"] =
                    BuildStableClassCsv(reports.TransactionClasses),
                ["class-frequency-by-run.csv"] =
                    BuildClassFrequencyCsv(reports.TransactionClasses),
                ["run-structure-comparison.csv"] =
                    BuildRunStructureCsv(reports.Summary.Runs)
            };

            foreach ((string fileName, string fileContent) in content)
            {
                await WriteAtomicAsync(
                        analysisDirectories,
                        outputDirectory,
                        Path.Combine(outputDirectory, fileName),
                        fileContent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            SortedDictionary<string, string> hashes = new(StringComparer.Ordinal);
            foreach (string fileName in ReportFileNames
                         .Where(name => name != "hashes.sha256")
                         .Order(StringComparer.Ordinal))
            {
                hashes[fileName] = await CaptureAnalyzer.CalculateSha256Async(
                        Path.Combine(outputDirectory, fileName),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string manifest = string.Concat(
                hashes.Select(pair => $"{pair.Value}  {pair.Key}\n"));
            await WriteAtomicAsync(
                    analysisDirectories,
                    outputDirectory,
                    Path.Combine(outputDirectory, "hashes.sha256"),
                    manifest,
                    cancellationToken)
                .ConfigureAwait(false);
            return hashes;
        }
        catch (AnalysisOutputException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            throw new AnalysisOutputException(
                $"Could not write deterministic campaign reports: {exception.Message}",
                exception);
        }
    }

    private static async Task WriteAtomicAsync(
        IReadOnlyList<string> analysisDirectories,
        string outputDirectory,
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        string temporary = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            byte[] bytes = Utf8WithoutBom.GetBytes(content);
            await using (FileStream stream = new(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 64 * 1024,
                             FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            _ = CampaignPathSafety.Validate(
                analysisDirectories,
                outputDirectory);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string BuildMarkdown(CampaignReportBundle reports)
    {
        CampaignSummaryReport summary = reports.Summary;
        int stable = reports.TransactionClasses.Count(
            item => item.PresentInRuns == 3);
        int twoRuns = reports.TransactionClasses.Count(
            item => item.PresentInRuns == 2);
        int oneRun = reports.TransactionClasses.Count(
            item => item.PresentInRuns == 1);

        StringBuilder text = new();
        text.AppendLine("# MyPlasm Three-Run Differential Capture Comparison");
        text.AppendLine();
        text.AppendLine("Classification: **confirmed structural evidence only**.");
        text.AppendLine();
        text.AppendLine(
            "This comparison consumes only three manifest-verified sanitized analyzer report sets. It assigns no packet meaning, command safety, replay suitability, framing, checksum, motion, status, firmware, coordinate, input, or machine semantics.");
        text.AppendLine();
        text.AppendLine("## Verified canonical runs");
        text.AppendLine();
        text.AppendLine("| Run | Analysis-set SHA-256 | Analyzer | Recorder schema | Records | Pairs | Reconnect transitions |");
        text.AppendLine("| --- | --- | --- | ---: | ---: | ---: | ---: |");
        foreach (CampaignRunReport run in summary.Runs)
        {
            text.AppendLine(
                $"| `{run.RunLabel}` | `{run.AnalysisSetSha256}` | `{run.AnalyzerToolVersion}` | {run.RecorderSchemaVersion} | {Number(run.RecordCount)} | {Number(run.MatchedPairCount)} | {Number(run.ReconnectTransitionCount)} |");
        }

        text.AppendLine();
        text.AppendLine("Canonical ordering uses only verified sanitized report hashes, sanitized capture hashes, and record counts. Local paths and argument order are excluded.");
        text.AppendLine();
        text.AppendLine("## Transaction-class presence");
        text.AppendLine();
        text.AppendLine($"- Exact fingerprints present in all three runs: `{stable}`");
        text.AppendLine($"- Exact fingerprints present in two runs: `{twoRuns}`");
        text.AppendLine($"- Exact fingerprints present in one run: `{oneRun}`");
        text.AppendLine();
        text.AppendLine(
            "A class is labeled **stable across all three captures** only when the same sanitized transaction-class fingerprint appears in all three verified inputs. Stability does not establish meaning, safety, or replay suitability.");
        text.AppendLine();
        text.AppendLine("## Run structure");
        text.AppendLine();
        text.AppendLine("| Run | Open sessions | Unmatched writes | Unexpected reads | Redundant closes | Failed closes | Unclosed handles |");
        text.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (CampaignRunReport run in summary.Runs)
        {
            text.AppendLine(
                $"| `{run.RunLabel}` | {run.OpenSessionCount} | {run.UnmatchedWriteCount} | {run.UnexpectedReadCount} | {run.RedundantCloseCount} | {run.FailedCloseCount} | {run.UnclosedHandleCount} |");
        }

        text.AppendLine();
        text.AppendLine("## Same-length variability families");
        text.AppendLine();
        text.AppendLine("| Family | Direction | Length | Runs present | Metrics equal across present runs |");
        text.AppendLine("| --- | --- | ---: | ---: | --- |");
        foreach (CampaignVariabilityFamilyReport family in
                 reports.VariabilityFamilies)
        {
            text.AppendLine(
                $"| `{family.FamilyId}` | `{family.Direction}` | {family.Length} | {family.PresentInRuns} | `{family.MetricsEqualAcrossPresentRuns}` |");
        }

        text.AppendLine();
        text.AppendLine("## Classification discipline");
        text.AppendLine();
        text.AppendLine(
            $"- `confirmed`: {summary.ClassificationDiscipline.Confirmed}");
        text.AppendLine(
            $"- `hypothesis`: {summary.ClassificationDiscipline.Hypothesis}");
        text.AppendLine(
            $"- `unknown`: {summary.ClassificationDiscipline.Unknown}");
        text.AppendLine();
        text.AppendLine(
            "Raw payloads, recorder session identifiers, process handles, controller selectors, serials, machine identifiers, and local paths are intentionally absent.");
        return NormalizeNewlines(text.ToString());
    }

    private static string BuildStableClassCsv(
        IEnumerable<CampaignTransactionClassReport> classes)
    {
        StringBuilder csv = new();
        csv.AppendLine(
            "class_id,write_class_id,read_class_id,write_length,read_length,presence_count,classification,stable_across_all_three_captures,stability_rule");
        foreach (CampaignTransactionClassReport item in classes
                     .Where(item => item.StableAcrossAllThreeCaptures)
                     .OrderBy(item => item.ClassId, StringComparer.Ordinal))
        {
            AppendCsvRow(
                csv,
                item.ClassId,
                item.WriteClassId,
                item.ReadClassId,
                Number(item.WriteLength),
                Number(item.ReadLength),
                Number(item.PresentInRuns),
                item.Classification,
                "true",
                item.StabilityRule);
        }

        return NormalizeNewlines(csv.ToString());
    }

    private static string BuildClassFrequencyCsv(
        IEnumerable<CampaignTransactionClassReport> classes)
    {
        StringBuilder csv = new();
        csv.AppendLine(
            "class_id,presence,run_label,count,first_write_sequence,last_read_sequence,session_distribution,phase_overlap,latency_min_us,latency_median_us,latency_p95_us,latency_p99_us,latency_mean_us,latency_max_us,queue_polls_min,queue_polls_median,queue_polls_p95,queue_polls_p99,queue_polls_mean,queue_polls_max");
        foreach (CampaignTransactionClassReport item in classes
                     .OrderBy(item => item.ClassId, StringComparer.Ordinal))
        {
            foreach (CampaignClassRunReport run in item.Runs
                         .OrderBy(run => run.RunLabel, StringComparer.Ordinal))
            {
                AppendCsvRow(
                    csv,
                    item.ClassId,
                    item.Presence,
                    run.RunLabel,
                    Number(run.Count),
                    run.FirstWriteSequence?.ToString(
                        CultureInfo.InvariantCulture) ?? string.Empty,
                    run.LastReadSequence?.ToString(
                        CultureInfo.InvariantCulture) ?? string.Empty,
                    string.Join(
                        ';',
                        run.CountsBySession.Select(
                            pair => $"{pair.Key}={Number(pair.Value)}")),
                    string.Join(';', run.OverlappingPhases),
                    DistributionValue(run.LatencyMicroseconds, value => value.Minimum),
                    DistributionValue(run.LatencyMicroseconds, value => value.Median),
                    DistributionValue(run.LatencyMicroseconds, value => value.P95),
                    DistributionValue(run.LatencyMicroseconds, value => value.P99),
                    DistributionValue(run.LatencyMicroseconds, value => value.Mean),
                    DistributionValue(run.LatencyMicroseconds, value => value.Maximum),
                    DistributionValue(run.QueuePolls, value => value.Minimum),
                    DistributionValue(run.QueuePolls, value => value.Median),
                    DistributionValue(run.QueuePolls, value => value.P95),
                    DistributionValue(run.QueuePolls, value => value.P99),
                    DistributionValue(run.QueuePolls, value => value.Mean),
                    DistributionValue(run.QueuePolls, value => value.Maximum));
            }
        }

        return NormalizeNewlines(csv.ToString());
    }

    private static string BuildRunStructureCsv(
        IReadOnlyList<CampaignRunReport> runs)
    {
        StringBuilder csv = new();
        csv.AppendLine("metric,category,run-1,run-2,run-3");
        AppendMetric(csv, runs, "record_count", string.Empty, run => run.RecordCount);
        AppendMetric(csv, runs, "open_session_count", string.Empty, run => run.OpenSessionCount);
        AppendMetric(csv, runs, "matched_pair_count", string.Empty, run => run.MatchedPairCount);
        AppendMetric(csv, runs, "unmatched_write_count", string.Empty, run => run.UnmatchedWriteCount);
        AppendMetric(csv, runs, "unexpected_read_count", string.Empty, run => run.UnexpectedReadCount);
        AppendMetric(csv, runs, "redundant_close_count", string.Empty, run => run.RedundantCloseCount);
        AppendMetric(csv, runs, "failed_close_count", string.Empty, run => run.FailedCloseCount);
        AppendMetric(csv, runs, "unclosed_handle_count", string.Empty, run => run.UnclosedHandleCount);
        AppendMetric(csv, runs, "reconnect_transition_count", string.Empty, run => run.ReconnectTransitionCount);

        foreach (string function in runs
                     .SelectMany(run => run.FunctionCounts.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            AppendMetric(
                csv,
                runs,
                "function_count",
                function,
                run => run.FunctionCounts.GetValueOrDefault(function));
        }

        foreach (string status in runs
                     .SelectMany(run => run.StatusCounts.SelectMany(
                         function => function.Value.Keys.Select(
                             value => $"{function.Key}/{value}")))
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            string[] parts = status.Split('/', 2);
            AppendMetric(
                csv,
                runs,
                "status_count",
                status,
                run => run.StatusCounts.TryGetValue(
                        parts[0],
                        out IReadOnlyDictionary<string, long>? values)
                    ? values.GetValueOrDefault(parts[1])
                    : 0);
        }

        foreach (string phase in runs
                     .SelectMany(run => run.PhaseCounts.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            AppendMetric(
                csv,
                runs,
                "phase_count",
                phase,
                run => run.PhaseCounts.GetValueOrDefault(phase));
        }

        AppendDistributionMetrics(
            csv,
            runs,
            "transaction_latency_us",
            run => run.TransactionLatencyMicroseconds);
        AppendDistributionMetrics(
            csv,
            runs,
            "transaction_cadence_us",
            run => run.TransactionCadenceMicroseconds);
        foreach (string function in runs
                     .SelectMany(run => run.FunctionCadenceMicroseconds.Keys)
                     .Distinct(StringComparer.Ordinal)
                     .Order(StringComparer.Ordinal))
        {
            AppendDistributionMetrics(
                csv,
                runs,
                "function_cadence_us",
                run => run.FunctionCadenceMicroseconds.GetValueOrDefault(
                    function),
                function);
        }

        return NormalizeNewlines(csv.ToString());
    }

    private static void AppendDistributionMetrics(
        StringBuilder csv,
        IReadOnlyList<CampaignRunReport> runs,
        string metric,
        Func<CampaignRunReport, DistributionReport?> select,
        string categoryPrefix = "")
    {
        (string Name, Func<DistributionReport, double?> Select)[] fields =
        [
            ("count", value => value.Count),
            ("minimum", value => value.Minimum),
            ("median", value => value.Median),
            ("p95", value => value.P95),
            ("p99", value => value.P99),
            ("mean", value => value.Mean),
            ("maximum", value => value.Maximum)
        ];
        foreach ((string name, Func<DistributionReport, double?> field) in fields)
        {
            string category = categoryPrefix.Length == 0
                ? name
                : $"{categoryPrefix}/{name}";
            AppendCsvRow(
                csv,
                metric,
                category,
                runs.Select(run =>
                {
                    DistributionReport? distribution = select(run);
                    return distribution is null
                        ? string.Empty
                        : Number(field(distribution));
                }).ToArray());
        }
    }

    private static void AppendMetric(
        StringBuilder csv,
        IReadOnlyList<CampaignRunReport> runs,
        string metric,
        string category,
        Func<CampaignRunReport, long> select) =>
        AppendCsvRow(
            csv,
            metric,
            category,
            runs.Select(run => Number(select(run))).ToArray());

    private static string DistributionValue(
        DistributionReport? distribution,
        Func<DistributionReport, double?> select) =>
        distribution is null ? string.Empty : Number(select(distribution));

    private static string Number(long value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);

    private static string Number(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void AppendCsvRow(
        StringBuilder target,
        string first,
        string second,
        IReadOnlyList<string> remaining)
    {
        List<string> values = [first, second];
        values.AddRange(remaining);
        AppendCsvRow(target, values.ToArray());
    }

    private static void AppendCsvRow(
        StringBuilder target,
        params string[] values)
    {
        target.AppendJoin(',', values.Select(EscapeCsv));
        target.Append('\n');
    }

    private static string EscapeCsv(string value)
    {
        if (!value.AsSpan().ContainsAny([',', '"', '\r', '\n']))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    private static string NormalizeNewlines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
