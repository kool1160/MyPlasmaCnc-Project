using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyPlasm.Inspector.ProtocolAnalysis;

internal static class ReportWriter
{
    private static readonly string[] ReportFileNames =
    [
        "capture-summary.json",
        "capture-report.md",
        "phase-timeline.csv",
        "transaction-classes.csv",
        "payload-variability.json"
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
        string outputDirectory,
        bool overwrite,
        ReportBundle reports,
        CancellationToken cancellationToken)
    {
        try
        {
            if (Directory.Exists(outputDirectory))
            {
                if (!overwrite && Directory.EnumerateFileSystemEntries(outputDirectory).Any())
                {
                    throw new AnalysisOutputException(
                        "The output directory is not empty. Use --overwrite to replace analyzer report files explicitly.");
                }
            }
            else
            {
                Directory.CreateDirectory(outputDirectory);
            }

            SortedDictionary<string, string> content = new(StringComparer.Ordinal)
            {
                ["capture-summary.json"] = SerializeJson(reports.Summary),
                ["capture-report.md"] = BuildMarkdown(reports),
                ["phase-timeline.csv"] = BuildPhaseCsv(reports.Phases),
                ["transaction-classes.csv"] =
                    BuildTransactionClassCsv(reports.TransactionClasses),
                ["payload-variability.json"] = SerializeJson(reports.PayloadVariability)
            };

            foreach ((string fileName, string fileContent) in content)
            {
                await WriteAtomicAsync(
                        Path.Combine(outputDirectory, fileName),
                        fileContent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            SortedDictionary<string, string> hashes = new(StringComparer.Ordinal);
            foreach (string fileName in ReportFileNames.Order(StringComparer.Ordinal))
            {
                hashes[fileName] = await CaptureAnalyzer.CalculateSha256Async(
                        Path.Combine(outputDirectory, fileName),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            string manifest = string.Concat(
                hashes.Select(pair => $"{pair.Value}  {pair.Key}\n"));
            await WriteAtomicAsync(
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
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new AnalysisOutputException(
                $"Could not write deterministic reports: {exception.Message}",
                exception);
        }
    }

    private static string SerializeJson<T>(T value) =>
        JsonSerializer.Serialize(value, JsonOptions) + "\n";

    private static async Task WriteAtomicAsync(
        string destination,
        string content,
        CancellationToken cancellationToken)
    {
        string temporary = destination + ".tmp";
        await File.WriteAllTextAsync(temporary, content, Utf8WithoutBom, cancellationToken)
            .ConfigureAwait(false);
        File.Move(temporary, destination, overwrite: true);
    }

    private static string BuildMarkdown(ReportBundle reports)
    {
        CaptureSummaryReport summary = reports.Summary;
        StringBuilder text = new();
        text.AppendLine("# MyPlasm Offline Capture Report");
        text.AppendLine();
        text.AppendLine("Classification: **confirmed structural evidence only**.");
        text.AppendLine();
        text.AppendLine(
            "This report assigns no packet meaning, command safety, framing, checksum, motion, status, firmware, coordinate, or input semantics.");
        text.AppendLine();
        text.AppendLine("## Capture integrity");
        text.AppendLine();
        text.AppendLine($"- Input file: `{EscapeMarkdown(summary.InputFileName)}`");
        text.AppendLine($"- SHA-256: `{summary.InputSha256}`");
        text.AppendLine($"- Recorder schema: `{summary.RecorderSchemaVersion}`");
        text.AppendLine($"- Records: `{summary.RecordCount.ToString(CultureInfo.InvariantCulture)}`");
        text.AppendLine(
            $"- Sequence: `{summary.Sequence.First}` through `{summary.Sequence.Last}`; gaps `{summary.Sequence.GapCount}`; missing values `{summary.Sequence.MissingSequenceCount}`");
        text.AppendLine();
        text.AppendLine("## Function counts");
        text.AppendLine();
        text.AppendLine("| Function | Count |");
        text.AppendLine("| --- | ---: |");
        foreach ((string function, long count) in summary.FunctionCounts)
        {
            text.AppendLine($"| `{function}` | {count.ToString(CultureInfo.InvariantCulture)} |");
        }

        text.AppendLine();
        text.AppendLine("## Deterministic transactions");
        text.AppendLine();
        text.AppendLine(
            $"- Matched write/read pairs: `{summary.Transactions.Matched.ToString(CultureInfo.InvariantCulture)}`");
        text.AppendLine(
            $"- Unmatched writes: `{summary.Transactions.UnmatchedWrites.ToString(CultureInfo.InvariantCulture)}`");
        text.AppendLine(
            $"- Unexpected reads: `{summary.Transactions.UnexpectedReads.ToString(CultureInfo.InvariantCulture)}`");
        text.AppendLine(
            "- Pairing rule: each successful read consumes the oldest pending successful write on the same still-open handle session; successful close ends the boundary.");
        text.AppendLine();
        text.AppendLine("## Handle sessions and anomalies");
        text.AppendLine();
        text.AppendLine($"- Successful open sessions: `{summary.HandleSessions.Count}`");
        text.AppendLine($"- Failed opens: `{summary.Anomalies.FailedOpens}`");
        text.AppendLine($"- Redundant closes: `{summary.Anomalies.RedundantCloses}`");
        text.AppendLine($"- Unclosed handles at capture end: `{summary.Anomalies.UnclosedHandles}`");
        text.AppendLine();
        text.AppendLine("## Classification discipline");
        text.AppendLine();
        text.AppendLine(
            "- `confirmed`: call ordering, return status, counts, timing, exact fingerprints, and deterministic pairing from the recorded evidence.");
        text.AppendLine(
            "- `hypothesis`: none generated by this tool. Future suggestions require an explicit evidence rule and independent review.");
        text.AppendLine(
            "- `unknown`: packet framing, fields, counters, checksums, semantics, and command safety remain unknown.");
        text.AppendLine();
        text.AppendLine(
            "Raw payloads, process handles, controller selectors, local paths, and machine identifiers are intentionally absent.");
        return text.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string BuildPhaseCsv(IEnumerable<PhaseRow> phases)
    {
        StringBuilder csv = new();
        csv.AppendLine(
            "classification,phase,session_label,start_sequence,end_sequence,start_elapsed_us,end_elapsed_us,status,evidence_rule");
        foreach (PhaseRow phase in phases)
        {
            AppendCsvRow(
                csv,
                phase.Classification,
                phase.Phase,
                phase.SessionLabel,
                phase.StartSequence.ToString(CultureInfo.InvariantCulture),
                phase.EndSequence.ToString(CultureInfo.InvariantCulture),
                phase.StartElapsedMicroseconds.ToString(CultureInfo.InvariantCulture),
                phase.EndElapsedMicroseconds.ToString(CultureInfo.InvariantCulture),
                phase.Status?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                phase.EvidenceRule);
        }

        return csv.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string BuildTransactionClassCsv(
        IEnumerable<TransactionClassReport> classes)
    {
        StringBuilder csv = new();
        csv.AppendLine(
            "class_id,write_class_id,read_class_id,count,first_write_sequence,last_read_sequence,latency_min_us,latency_median_us,latency_p95_us,latency_p99_us,latency_mean_us,latency_max_us,queue_polls_min,queue_polls_median,queue_polls_p95,queue_polls_p99,queue_polls_mean,queue_polls_max");
        foreach (TransactionClassReport item in classes)
        {
            AppendCsvRow(
                csv,
                item.ClassId,
                item.WriteClassId,
                item.ReadClassId,
                item.Count.ToString(CultureInfo.InvariantCulture),
                item.FirstWriteSequence.ToString(CultureInfo.InvariantCulture),
                item.LastReadSequence.ToString(CultureInfo.InvariantCulture),
                Number(item.LatencyMicroseconds.Minimum),
                Number(item.LatencyMicroseconds.Median),
                Number(item.LatencyMicroseconds.P95),
                Number(item.LatencyMicroseconds.P99),
                Number(item.LatencyMicroseconds.Mean),
                Number(item.LatencyMicroseconds.Maximum),
                Number(item.QueuePolls.Minimum),
                Number(item.QueuePolls.Median),
                Number(item.QueuePolls.P95),
                Number(item.QueuePolls.P99),
                Number(item.QueuePolls.Mean),
                Number(item.QueuePolls.Maximum));
        }

        return csv.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static string Number(double? value) =>
        value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;

    private static void AppendCsvRow(StringBuilder target, params string[] values)
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

    private static string EscapeMarkdown(string value) =>
        value.Replace("`", "&#96;", StringComparison.Ordinal);
}
