using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyPlasm.Inspector.ProtocolAnalysis;

internal static class CampaignAnalysisReader
{
    internal static readonly string[] RequiredReportFileNames =
    [
        "capture-summary.json",
        "capture-report.md",
        "phase-timeline.csv",
        "transaction-classes.csv",
        "payload-variability.json",
        "hashes.sha256"
    ];

    private static readonly string[] ManifestReportFileNames =
        RequiredReportFileNames
            .Where(name => name != "hashes.sha256")
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static readonly string[] ForbiddenSanitizedFieldNames =
    [
        "handle_id",
        "process_id",
        "read_hex",
        "returned_handle",
        "selector",
        "selector_pointer",
        "serial_number",
        "session_id",
        "thread_id",
        "write_hex"
    ];

    private static readonly string[] TransactionHeader =
    [
        "class_id",
        "write_class_id",
        "read_class_id",
        "count",
        "first_write_sequence",
        "last_read_sequence",
        "latency_min_us",
        "latency_median_us",
        "latency_p95_us",
        "latency_p99_us",
        "latency_mean_us",
        "latency_max_us",
        "queue_polls_min",
        "queue_polls_median",
        "queue_polls_p95",
        "queue_polls_p99",
        "queue_polls_mean",
        "queue_polls_max"
    ];

    private static readonly string[] PhaseHeader =
    [
        "classification",
        "phase",
        "session_label",
        "start_sequence",
        "end_sequence",
        "start_elapsed_us",
        "end_elapsed_us",
        "status",
        "evidence_rule"
    ];

    public static async Task<CampaignAnalysisSet> ReadAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCoreAsync(directory, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (CampaignInputValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                NotSupportedException)
        {
            throw new CampaignInputValidationException(
                "A sanitized analysis report set could not be read safely.",
                exception);
        }
    }

    private static async Task<CampaignAnalysisSet> ReadCoreAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        ValidateExactFileSet(directory);
        Dictionary<string, string> paths = RequiredReportFileNames.ToDictionary(
            name => name,
            name => Path.Combine(directory, name),
            StringComparer.Ordinal);
        ValidateUnambiguousFiles(paths);

        IReadOnlyDictionary<string, string> manifest =
            ParseManifest(paths["hashes.sha256"]);
        SortedDictionary<string, string> actualHashes =
            new(StringComparer.Ordinal);
        foreach (string name in RequiredReportFileNames.Order(StringComparer.Ordinal))
        {
            actualHashes[name] = await CaptureAnalyzer.CalculateSha256Async(
                    paths[name],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (string name in ManifestReportFileNames)
        {
            if (!string.Equals(
                    manifest[name],
                    actualHashes[name],
                    StringComparison.Ordinal))
            {
                throw new CampaignInputValidationException(
                    $"The SHA-256 recorded for {name} does not match its analysis report.");
            }
        }

        ValidateMarkdown(paths["capture-report.md"]);
        ParsedSummary summary = ParseSummary(paths["capture-summary.json"]);
        IReadOnlyList<CampaignPhase> phases =
            ParsePhases(paths["phase-timeline.csv"]);
        IReadOnlyDictionary<string, CampaignTransactionClass> classes =
            ParseTransactionClasses(paths["transaction-classes.csv"]);
        IReadOnlyDictionary<string, CampaignVariabilityFamily> families =
            ParseVariability(paths["payload-variability.json"]);

        SortedDictionary<string, long> phaseCounts = new(StringComparer.Ordinal);
        foreach (CampaignPhase phase in phases)
        {
            Increment(phaseCounts, phase.Phase);
        }

        long reconnectTransitions =
            phaseCounts.GetValueOrDefault("reconnect_transition");
        CampaignRunStructure structure = new(
            summary.RecordCount,
            summary.FunctionCounts,
            summary.StatusCounts,
            phaseCounts,
            summary.OpenSessionCount,
            summary.MatchedPairs,
            summary.UnmatchedWrites,
            summary.UnexpectedReads,
            summary.RedundantCloses,
            summary.FailedCloses,
            summary.UnclosedHandles,
            reconnectTransitions,
            summary.TransactionLatency,
            summary.TransactionCadence,
            summary.FunctionCadence);

        string canonicalFingerprint = FingerprintReportSet(actualHashes);
        return new CampaignAnalysisSet(
            canonicalFingerprint,
            summary.ToolVersion,
            summary.RecorderSchemaVersion,
            summary.InputCaptureSha256,
            actualHashes,
            structure,
            classes,
            families,
            phases,
            summary.TransactionClassCountsBySession);
    }

    private static void ValidateExactFileSet(string directory)
    {
        string[] actual = Directory
            .EnumerateFileSystemEntries(directory)
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray()!;
        string[] expected = RequiredReportFileNames
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new CampaignInputValidationException(
                "Each analysis directory must contain exactly the six sanitized analyzer outputs and no other files or directories.");
        }
    }

    private static void ValidateUnambiguousFiles(
        IReadOnlyDictionary<string, string> paths)
    {
        HashSet<string> resolved = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string path) in paths)
        {
            if (!File.Exists(path))
            {
                throw new CampaignInputValidationException(
                    $"Required sanitized report {name} is missing.");
            }

            string identity = CampaignPathSafety.ResolveExistingLinks(path);
            if (!resolved.Add(identity))
            {
                throw new CampaignInputValidationException(
                    "Two required analysis reports resolve to the same file.");
            }
        }
    }

    private static IReadOnlyDictionary<string, string> ParseManifest(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(
                path,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException)
        {
            throw new CampaignInputValidationException(
                "hashes.sha256 could not be read as UTF-8.",
                exception);
        }

        SortedDictionary<string, string> entries = new(StringComparer.Ordinal);
        List<string> listedNames = [];
        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.Length < 67 ||
                line[64..66] != "  " ||
                line[..64].Any(character => !char.IsAsciiHexDigit(character)))
            {
                throw new CampaignInputValidationException(
                    "hashes.sha256 contains a malformed entry.");
            }

            string name = line[66..];
            if (!ManifestReportFileNames.Contains(name, StringComparer.Ordinal) ||
                !entries.TryAdd(name, line[..64].ToUpperInvariant()))
            {
                throw new CampaignInputValidationException(
                    "hashes.sha256 contains an unknown or duplicate report entry.");
            }

            listedNames.Add(name);
        }

        if (!listedNames.SequenceEqual(
                ManifestReportFileNames,
                StringComparer.Ordinal))
        {
            throw new CampaignInputValidationException(
                "hashes.sha256 does not list exactly the five hashed analyzer reports.");
        }

        return entries;
    }

    private static void ValidateMarkdown(string path)
    {
        string text = ReadSanitizedText(path, "capture-report.md");
        ScanForbiddenText(text, "capture-report.md");
        if (!text.StartsWith(
                "# MyPlasm Offline Capture Report\n",
                StringComparison.Ordinal) ||
            !text.Contains(
                "Classification: **confirmed structural evidence only**.",
                StringComparison.Ordinal) ||
            !text.Contains(
                "- `unknown`: packet framing, fields, counters, checksums, semantics, and command safety remain unknown.",
                StringComparison.Ordinal))
        {
            throw new CampaignInputValidationException(
                "capture-report.md has an incompatible sanitized report structure.");
        }
    }

    private static ParsedSummary ParseSummary(string path)
    {
        using JsonDocument document =
            ParseJson(path, "capture-summary.json");
        JsonElement root = RequireObject(document.RootElement, "capture-summary.json");
        ScanForbiddenProperties(root, "capture-summary.json");

        string toolVersion = RequiredString(root, "tool_version", "capture-summary.json");
        int schema = RequiredInt32(
            root,
            "recorder_schema_version",
            "capture-summary.json");
        string inputFileName = RequiredString(
            root,
            "input_file_name",
            "capture-summary.json");
        if (!string.Equals(
                inputFileName,
                Path.GetFileName(inputFileName),
                StringComparison.Ordinal) ||
            inputFileName.Contains(':', StringComparison.Ordinal) ||
            inputFileName.Contains('\\', StringComparison.Ordinal) ||
            inputFileName.Contains('/', StringComparison.Ordinal))
        {
            throw new CampaignInputValidationException(
                "capture-summary.json input_file_name must be a basename.");
        }

        string inputSha256 = RequiredSha256(
            root,
            "input_sha256",
            "capture-summary.json");
        long recordCount = RequiredNonnegativeInt64(
            root,
            "record_count",
            "capture-summary.json");
        IReadOnlyDictionary<string, long> functions =
            ReadCountDictionary(
                RequiredObject(root, "function_counts", "capture-summary.json"),
                "capture-summary.json function_counts");
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> statuses =
            ReadNestedCountDictionary(
                RequiredObject(root, "status_counts", "capture-summary.json"),
                "capture-summary.json status_counts");
        IReadOnlyDictionary<string, DistributionReport> functionCadence =
            ReadDistributionDictionary(
                RequiredObject(
                    root,
                    "function_cadence_microseconds",
                    "capture-summary.json"),
                "capture-summary.json function_cadence_microseconds");

        JsonElement transactions = RequiredObject(
            root,
            "transactions",
            "capture-summary.json");
        long matched = RequiredNonnegativeInt64(
            transactions,
            "matched",
            "capture-summary.json transactions");
        long unmatched = RequiredNonnegativeInt64(
            transactions,
            "unmatched_writes",
            "capture-summary.json transactions");
        long unexpected = RequiredNonnegativeInt64(
            transactions,
            "unexpected_reads",
            "capture-summary.json transactions");
        DistributionReport transactionLatency = ReadDistribution(
            RequiredObject(
                transactions,
                "latency_microseconds",
                "capture-summary.json transactions"),
            "capture-summary.json transaction latency");
        DistributionReport transactionCadence = ReadDistribution(
            RequiredObject(
                transactions,
                "cadence_microseconds",
                "capture-summary.json transactions"),
            "capture-summary.json transaction cadence");

        JsonElement anomalies = RequiredObject(
            root,
            "anomalies",
            "capture-summary.json");
        long redundantCloses = RequiredNonnegativeInt64(
            anomalies,
            "redundant_closes",
            "capture-summary.json anomalies");
        long failedCloses = RequiredNonnegativeInt64(
            anomalies,
            "failed_closes",
            "capture-summary.json anomalies");
        long unclosedHandles = RequiredNonnegativeInt64(
            anomalies,
            "unclosed_handles",
            "capture-summary.json anomalies");

        JsonElement handleSessions = RequiredArray(
            root,
            "handle_sessions",
            "capture-summary.json");
        int openSessionCount = handleSessions.GetArrayLength();
        foreach (JsonElement session in handleSessions.EnumerateArray())
        {
            _ = RequiredString(
                RequireObject(session, "capture-summary.json handle session"),
                "session_label",
                "capture-summary.json handle session");
        }

        Dictionary<string, IReadOnlyDictionary<string, long>>
            transactionClassCountsBySession = new(StringComparer.Ordinal);
        JsonElement frequencies = RequiredArray(
            root,
            "session_class_frequency_comparison",
            "capture-summary.json");
        foreach (JsonElement itemElement in frequencies.EnumerateArray())
        {
            JsonElement item = RequireObject(
                itemElement,
                "capture-summary.json session frequency");
            string kind = RequiredString(
                item,
                "class_kind",
                "capture-summary.json session frequency");
            if (kind != "transaction")
            {
                continue;
            }

            string classId = RequiredString(
                item,
                "class_id",
                "capture-summary.json session frequency");
            IReadOnlyDictionary<string, long> counts = ReadCountDictionary(
                RequiredObject(
                    item,
                    "counts_by_session",
                    "capture-summary.json session frequency"),
                "capture-summary.json counts_by_session");
            if (!transactionClassCountsBySession.TryAdd(classId, counts))
            {
                throw new CampaignInputValidationException(
                    "capture-summary.json contains duplicate transaction class session counts.");
            }
        }

        return new ParsedSummary(
            toolVersion,
            schema,
            inputSha256,
            recordCount,
            functions,
            statuses,
            functionCadence,
            openSessionCount,
            matched,
            unmatched,
            unexpected,
            redundantCloses,
            failedCloses,
            unclosedHandles,
            transactionLatency,
            transactionCadence,
            transactionClassCountsBySession);
    }

    private static IReadOnlyList<CampaignPhase> ParsePhases(string path)
    {
        IReadOnlyList<string[]> rows =
            SanitizedCsvReader.Read(path, PhaseHeader, "phase-timeline.csv");
        List<CampaignPhase> phases = [];
        foreach (string[] row in rows)
        {
            if (row[0] != "confirmed")
            {
                throw new CampaignInputValidationException(
                    "phase-timeline.csv contains a non-confirmed structural classification.");
            }

            ulong start = ParseUInt64(row[3], "phase start sequence");
            ulong end = ParseUInt64(row[4], "phase end sequence");
            if (start == 0 || end < start)
            {
                throw new CampaignInputValidationException(
                    "phase-timeline.csv contains an invalid sequence range.");
            }

            _ = ParseInt64(row[5], "phase start elapsed time");
            _ = ParseInt64(row[6], "phase end elapsed time");
            if (row[7].Length > 0)
            {
                _ = ParseUInt64(row[7], "phase status");
            }

            phases.Add(new CampaignPhase(row[1], row[2], start, end));
        }

        return phases
            .OrderBy(item => item.StartSequence)
            .ThenBy(item => item.EndSequence)
            .ThenBy(item => item.Phase, StringComparer.Ordinal)
            .ThenBy(item => item.SessionLabel, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, CampaignTransactionClass>
        ParseTransactionClasses(string path)
    {
        IReadOnlyList<string[]> rows = SanitizedCsvReader.Read(
            path,
            TransactionHeader,
            "transaction-classes.csv");
        SortedDictionary<string, CampaignTransactionClass> classes =
            new(StringComparer.Ordinal);
        foreach (string[] row in rows)
        {
            ValidateTransactionClassIdentity(row[0], row[1], row[2]);
            long count = ParseNonnegativeInt64(
                row[3],
                "transaction class count");
            CampaignTransactionClass item = new(
                row[0],
                row[1],
                row[2],
                ParsePayloadClassLength(row[1], "write"),
                ParsePayloadClassLength(row[2], "read"),
                count,
                ParsePositiveUInt64(row[4], "first write sequence"),
                ParsePositiveUInt64(row[5], "last read sequence"),
                DistributionFromCsv(
                    row,
                    6,
                    count,
                    "transaction latency"),
                DistributionFromCsv(
                    row,
                    12,
                    count,
                    "transaction queue polls"));
            if (item.Count <= 0 ||
                item.LastReadSequence < item.FirstWriteSequence ||
                !classes.TryAdd(item.ClassId, item))
            {
                throw new CampaignInputValidationException(
                    "transaction-classes.csv contains a duplicate or invalid transaction class.");
            }
        }

        return classes;
    }

    private static IReadOnlyDictionary<string, CampaignVariabilityFamily>
        ParseVariability(string path)
    {
        using JsonDocument document =
            ParseJson(path, "payload-variability.json");
        JsonElement root = RequireObject(
            document.RootElement,
            "payload-variability.json");
        ScanForbiddenProperties(root, "payload-variability.json");
        if (RequiredString(
                root,
                "classification",
                "payload-variability.json") != "confirmed")
        {
            throw new CampaignInputValidationException(
                "payload-variability.json classification must be confirmed.");
        }

        _ = RequiredArray(
            root,
            "exact_payload_classes",
            "payload-variability.json");
        JsonElement familyArray = RequiredArray(
            root,
            "same_length_families",
            "payload-variability.json");
        SortedDictionary<string, CampaignVariabilityFamily> families =
            new(StringComparer.Ordinal);
        foreach (JsonElement familyElement in familyArray.EnumerateArray())
        {
            JsonElement family = RequireObject(
                familyElement,
                "payload-variability.json family");
            string familyId = RequiredString(
                family,
                "family_id",
                "payload-variability.json family");
            string direction = RequiredString(
                family,
                "direction",
                "payload-variability.json family");
            int length = RequiredNonnegativeInt32(
                family,
                "length",
                "payload-variability.json family");
            if ((direction is not ("write" or "read")) ||
                familyId != $"{(direction == "write" ? "W" : "R")}-{length}")
            {
                throw new CampaignInputValidationException(
                    "payload-variability.json contains an invalid family identity.");
            }

            JsonElement positionsElement = RequiredArray(
                family,
                "positions",
                "payload-variability.json family");
            List<BytePositionReport> positions = [];
            foreach (JsonElement positionElement in
                     positionsElement.EnumerateArray())
            {
                JsonElement position = RequireObject(
                    positionElement,
                    "payload-variability.json position");
                positions.Add(new BytePositionReport(
                    RequiredNonnegativeInt32(
                        position,
                        "index",
                        "payload-variability.json position"),
                    RequiredNonnegativeInt32(
                        position,
                        "unique_value_count",
                        "payload-variability.json position"),
                    RequiredNonnegativeDouble(
                        position,
                        "entropy_bits",
                        "payload-variability.json position")));
            }

            if (positions.Count != length ||
                positions.Where((position, index) =>
                    position.Index != index ||
                    position.UniqueValueCount <= 0).Any())
            {
                throw new CampaignInputValidationException(
                    "payload-variability.json positions do not match the family length.");
            }

            CampaignVariabilityFamily item = new(
                familyId,
                direction,
                length,
                RequiredNonnegativeInt64(
                    family,
                    "sample_count",
                    "payload-variability.json family"),
                RequiredNonnegativeInt32(
                    family,
                    "fixed_prefix_length",
                    "payload-variability.json family"),
                RequiredNonnegativeInt32(
                    family,
                    "fixed_suffix_length",
                    "payload-variability.json family"),
                positions);
            if (item.SampleCount <= 0 ||
                item.FixedPrefixLength > length ||
                item.FixedSuffixLength > length ||
                !families.TryAdd(familyId, item))
            {
                throw new CampaignInputValidationException(
                    "payload-variability.json contains a duplicate or invalid family.");
            }
        }

        return families;
    }

    private static DistributionReport DistributionFromCsv(
        string[] row,
        int offset,
        long count,
        string description)
    {
        double? minimum = ParseOptionalDouble(row[offset], description);
        double? median = ParseOptionalDouble(row[offset + 1], description);
        double? p95 = ParseOptionalDouble(row[offset + 2], description);
        double? p99 = ParseOptionalDouble(row[offset + 3], description);
        double? mean = ParseOptionalDouble(row[offset + 4], description);
        double? maximum = ParseOptionalDouble(row[offset + 5], description);
        bool any = new[] { minimum, median, p95, p99, mean, maximum }
            .Any(value => value is not null);
        bool all = new[] { minimum, median, p95, p99, mean, maximum }
            .All(value => value is not null);
        if (any != all || (count > 0 && !all))
        {
            throw new CampaignInputValidationException(
                $"{description} is not fully populated.");
        }

        return new DistributionReport(
            all ? count : 0,
            minimum,
            median,
            p95,
            p99,
            mean,
            maximum);
    }

    private static IReadOnlyDictionary<string, DistributionReport>
        ReadDistributionDictionary(JsonElement element, string description)
    {
        SortedDictionary<string, DistributionReport> values =
            new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!values.TryAdd(
                    property.Name,
                    ReadDistribution(
                        RequireObject(property.Value, description),
                        $"{description}.{property.Name}")))
            {
                throw new CampaignInputValidationException(
                    $"{description} contains a duplicate property.");
            }
        }

        return values;
    }

    private static DistributionReport ReadDistribution(
        JsonElement element,
        string description)
    {
        long count = RequiredNonnegativeInt64(element, "count", description);
        double? minimum = OptionalDouble(element, "minimum", description);
        double? median = OptionalDouble(element, "median", description);
        double? p95 = OptionalDouble(element, "p95", description);
        double? p99 = OptionalDouble(element, "p99", description);
        double? mean = OptionalDouble(element, "mean", description);
        double? maximum = OptionalDouble(element, "maximum", description);
        bool any = new[] { minimum, median, p95, p99, mean, maximum }
            .Any(value => value is not null);
        bool all = new[] { minimum, median, p95, p99, mean, maximum }
            .All(value => value is not null);
        if ((count == 0 && any) || (count > 0 && !all))
        {
            throw new CampaignInputValidationException(
                $"{description} has inconsistent distribution values.");
        }

        return new DistributionReport(
            count,
            minimum,
            median,
            p95,
            p99,
            mean,
            maximum);
    }

    private static IReadOnlyDictionary<string, long> ReadCountDictionary(
        JsonElement element,
        string description)
    {
        SortedDictionary<string, long> values = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetInt64(out long value) ||
                value < 0 ||
                !values.TryAdd(property.Name, value))
            {
                throw new CampaignInputValidationException(
                    $"{description} contains an invalid count.");
            }
        }

        return values;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>
        ReadNestedCountDictionary(JsonElement element, string description)
    {
        SortedDictionary<string, IReadOnlyDictionary<string, long>> values =
            new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!values.TryAdd(
                    property.Name,
                    ReadCountDictionary(
                        RequireObject(property.Value, description),
                        $"{description}.{property.Name}")))
            {
                throw new CampaignInputValidationException(
                    $"{description} contains a duplicate property.");
            }
        }

        return values;
    }

    private static string FingerprintReportSet(
        IReadOnlyDictionary<string, string> reportHashes)
    {
        string material = string.Concat(
            reportHashes
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => $"{pair.Key}:{pair.Value}\n"));
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    private static void ValidateTransactionClassIdentity(
        string classId,
        string writeClassId,
        string readClassId)
    {
        _ = ParsePayloadClassLength(writeClassId, "write");
        _ = ParsePayloadClassLength(readClassId, "read");
        if (!classId.StartsWith("T-", StringComparison.Ordinal) ||
            classId.Length != 66 ||
            classId[2..].Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new CampaignInputValidationException(
                "transaction-classes.csv contains an invalid class_id.");
        }

        string material = $"{writeClassId}\n{readClassId}";
        string expected = "T-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)));
        if (!string.Equals(classId, expected, StringComparison.Ordinal))
        {
            throw new CampaignInputValidationException(
                "transaction-classes.csv class_id does not match its sanitized write/read fingerprints.");
        }
    }

    private static int ParsePayloadClassLength(
        string classId,
        string expectedDirection)
    {
        string prefix = expectedDirection == "write" ? "W-" : "R-";
        if (!classId.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new CampaignInputValidationException(
                $"A transaction class has an invalid {expectedDirection} fingerprint.");
        }

        string[] parts = classId.Split('-');
        if (parts.Length != 3 ||
            !int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int length) ||
            length < 0 ||
            parts[2].Length != 64 ||
            parts[2].Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new CampaignInputValidationException(
                $"A transaction class has an invalid {expectedDirection} fingerprint.");
        }

        return length;
    }

    private static JsonDocument ParseJson(string path, string reportName)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            return JsonDocument.Parse(bytes);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                JsonException)
        {
            throw new CampaignInputValidationException(
                $"{reportName} is not valid JSON.",
                exception);
        }
    }

    private static string ReadSanitizedText(string path, string reportName)
    {
        try
        {
            return File.ReadAllText(
                path,
                new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true))
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                DecoderFallbackException)
        {
            throw new CampaignInputValidationException(
                $"{reportName} could not be read as UTF-8.",
                exception);
        }
    }

    private static void ScanForbiddenText(string text, string reportName)
    {
        foreach (string field in ForbiddenSanitizedFieldNames)
        {
            if (text.Contains($"\"{field}\"", StringComparison.Ordinal))
            {
                throw new CampaignInputValidationException(
                    $"{reportName} contains a prohibited raw-evidence field.");
            }
        }
    }

    private static void ScanForbiddenProperties(
        JsonElement element,
        string reportName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new CampaignInputValidationException(
                        $"{reportName} contains duplicate JSON property '{property.Name}'.");
                }

                if (ForbiddenSanitizedFieldNames.Contains(
                        property.Name,
                        StringComparer.Ordinal))
                {
                    throw new CampaignInputValidationException(
                        $"{reportName} contains prohibited raw-evidence field '{property.Name}'.");
                }

                ScanForbiddenProperties(property.Value, reportName);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                ScanForbiddenProperties(item, reportName);
            }
        }
    }

    private static JsonElement RequireObject(JsonElement value, string description)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new CampaignInputValidationException(
                $"{description} must be a JSON object.");
        }

        return value;
    }

    private static JsonElement RequiredObject(
        JsonElement parent,
        string name,
        string description) =>
        RequireObject(RequiredProperty(parent, name, description), $"{description}.{name}");

    private static JsonElement RequiredArray(
        JsonElement parent,
        string name,
        string description)
    {
        JsonElement value = RequiredProperty(parent, name, description);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be an array.");
        }

        return value;
    }

    private static JsonElement RequiredProperty(
        JsonElement parent,
        string name,
        string description)
    {
        if (!parent.TryGetProperty(name, out JsonElement value))
        {
            throw new CampaignInputValidationException(
                $"{description} is missing required field '{name}'.");
        }

        return value;
    }

    private static string RequiredString(
        JsonElement parent,
        string name,
        string description)
    {
        JsonElement value = RequiredProperty(parent, name, description);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrEmpty(value.GetString()))
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be a nonempty string.");
        }

        return value.GetString()!;
    }

    private static string RequiredSha256(
        JsonElement parent,
        string name,
        string description)
    {
        string value = RequiredString(parent, name, description);
        if (value.Length != 64 ||
            value.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be a SHA-256 value.");
        }

        return value.ToUpperInvariant();
    }

    private static int RequiredInt32(
        JsonElement parent,
        string name,
        string description)
    {
        JsonElement value = RequiredProperty(parent, name, description);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be an integer.");
        }

        return result;
    }

    private static int RequiredNonnegativeInt32(
        JsonElement parent,
        string name,
        string description)
    {
        int value = RequiredInt32(parent, name, description);
        if (value < 0)
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be nonnegative.");
        }

        return value;
    }

    private static long RequiredNonnegativeInt64(
        JsonElement parent,
        string name,
        string description)
    {
        JsonElement value = RequiredProperty(parent, name, description);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result) ||
            result < 0)
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be a nonnegative integer.");
        }

        return result;
    }

    private static double RequiredNonnegativeDouble(
        JsonElement parent,
        string name,
        string description)
    {
        JsonElement value = RequiredProperty(parent, name, description);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) ||
            result < 0)
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be a nonnegative finite number.");
        }

        return result;
    }

    private static double? OptionalDouble(
        JsonElement parent,
        string name,
        string description)
    {
        JsonElement value = RequiredProperty(parent, name, description);
        if (value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result) ||
            result < 0)
        {
            throw new CampaignInputValidationException(
                $"{description}.{name} must be null or a nonnegative finite number.");
        }

        return result;
    }

    private static ulong ParsePositiveUInt64(string value, string description)
    {
        ulong result = ParseUInt64(value, description);
        if (result == 0)
        {
            throw new CampaignInputValidationException(
                $"{description} must be positive.");
        }

        return result;
    }

    private static ulong ParseUInt64(string value, string description)
    {
        if (!ulong.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out ulong result))
        {
            throw new CampaignInputValidationException(
                $"{description} is not a nonnegative integer.");
        }

        return result;
    }

    private static long ParseNonnegativeInt64(string value, string description)
    {
        long result = ParseInt64(value, description);
        if (result < 0)
        {
            throw new CampaignInputValidationException(
                $"{description} must be nonnegative.");
        }

        return result;
    }

    private static long ParseInt64(string value, string description)
    {
        if (!long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long result))
        {
            throw new CampaignInputValidationException(
                $"{description} is not an integer.");
        }

        return result;
    }

    private static double? ParseOptionalDouble(
        string value,
        string description)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (!double.TryParse(
                value,
                NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out double result) ||
            !double.IsFinite(result) ||
            result < 0)
        {
            throw new CampaignInputValidationException(
                $"{description} is not a nonnegative finite number.");
        }

        return result;
    }

    private static void Increment(IDictionary<string, long> values, string key)
    {
        values.TryGetValue(key, out long current);
        values[key] = current + 1;
    }

    private sealed record ParsedSummary(
        string ToolVersion,
        int RecorderSchemaVersion,
        string InputCaptureSha256,
        long RecordCount,
        IReadOnlyDictionary<string, long> FunctionCounts,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> StatusCounts,
        IReadOnlyDictionary<string, DistributionReport> FunctionCadence,
        int OpenSessionCount,
        long MatchedPairs,
        long UnmatchedWrites,
        long UnexpectedReads,
        long RedundantCloses,
        long FailedCloses,
        long UnclosedHandles,
        DistributionReport TransactionLatency,
        DistributionReport TransactionCadence,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>>
            TransactionClassCountsBySession);
}
