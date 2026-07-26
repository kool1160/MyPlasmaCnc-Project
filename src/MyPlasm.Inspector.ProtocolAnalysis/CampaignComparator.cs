namespace MyPlasm.Inspector.ProtocolAnalysis;

public sealed class CampaignComparator
{
    public const string ToolVersion = "1.0.0";
    public const int ComparisonSchemaVersion = 1;

    public async Task<CampaignComparisonResult> CompareAsync(
        CampaignComparisonRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CampaignPathSet paths = CampaignPathSafety.Validate(
            request.AnalysisDirectories,
            request.OutputDirectory);

        List<CampaignAnalysisSet> loaded = [];
        foreach (string directory in paths.AnalysisDirectories)
        {
            loaded.Add(
                await CampaignAnalysisReader.ReadAsync(directory, cancellationToken)
                    .ConfigureAwait(false));
        }

        ValidateCompatibility(loaded);
        CampaignAnalysisSet[] sets = loaded
            .OrderBy(set => set.CanonicalFingerprint, StringComparer.Ordinal)
            .ThenBy(set => set.InputCaptureSha256, StringComparer.Ordinal)
            .ThenBy(set => set.Structure.RecordCount)
            .ToArray();
        string[] runLabels = ["run-1", "run-2", "run-3"];

        CampaignRunReport[] runs = sets
            .Select((set, index) => CreateRunReport(set, runLabels[index]))
            .ToArray();
        CampaignTransactionClassReport[] classes =
            BuildClassComparison(sets, runLabels);
        CampaignVariabilityFamilyReport[] variability =
            BuildVariabilityComparison(sets, runLabels);
        CampaignSummaryReport summary = new(
            ToolVersion,
            ComparisonSchemaVersion,
            3,
            "Runs are sorted by SHA-256 of the six verified sanitized reports, then by sanitized capture SHA-256 and record count. Tied report sets are byte-identical.",
            runs,
            classes,
            variability,
            new CampaignClassificationReport(
                "Counts, sanitized fingerprints, report hashes, structural phases, and deterministic cross-run presence from the three verified analyzer report sets.",
                "No hypotheses are generated. Any later hypothesis requires a separate explicit evidence rule and review.",
                "Packet framing, fields, counters, checksums, semantics, command safety, and replay suitability remain unknown."));

        CampaignReportBundle bundle = new(summary, classes, variability);
        IReadOnlyDictionary<string, string> hashes =
            await CampaignReportWriter.WriteAsync(
                    paths.AnalysisDirectories,
                    paths.OutputDirectory,
                    request.Overwrite,
                    bundle,
                    cancellationToken)
                .ConfigureAwait(false);
        return new CampaignComparisonResult(
            3,
            classes.Count(item => item.StableAcrossAllThreeCaptures),
            hashes);
    }

    private static void ValidateCompatibility(
        IReadOnlyList<CampaignAnalysisSet> sets)
    {
        string[] toolVersions = sets
            .Select(set => set.AnalyzerToolVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (toolVersions.Length != 1 ||
            toolVersions[0] != CaptureAnalyzer.ToolVersion)
        {
            throw new CampaignInputValidationException(
                $"All three analysis sets must use supported analyzer version {CaptureAnalyzer.ToolVersion}.");
        }

        int[] schemas = sets
            .Select(set => set.RecorderSchemaVersion)
            .Distinct()
            .ToArray();
        if (schemas.Length != 1 || schemas[0] != 1)
        {
            throw new CampaignInputValidationException(
                "All three analysis sets must use supported recorder schema version 1.");
        }

        foreach (CampaignAnalysisSet set in sets)
        {
            if (set.Structure.FunctionCounts.Values.Sum() !=
                set.Structure.RecordCount)
            {
                throw new CampaignInputValidationException(
                    "Record and function totals disagree in a sanitized report set.");
            }

            foreach ((string function, long count) in
                     set.Structure.FunctionCounts)
            {
                if (!set.Structure.StatusCounts.TryGetValue(
                        function,
                        out IReadOnlyDictionary<string, long>? statuses) ||
                    statuses.Values.Sum() != count)
                {
                    throw new CampaignInputValidationException(
                        "Function and status totals disagree in a sanitized report set.");
                }
            }

            if (set.Structure.StatusCounts.Keys.Any(
                    function => !set.Structure.FunctionCounts.ContainsKey(
                        function)))
            {
                throw new CampaignInputValidationException(
                    "Sanitized status counts reference an unknown function.");
            }

            if (set.TransactionClasses.Values.Sum(item => item.Count) !=
                set.Structure.MatchedPairs)
            {
                throw new CampaignInputValidationException(
                    "Matched-pair and transaction-class totals disagree between sanitized reports.");
            }

            foreach ((string classId, CampaignTransactionClass item) in
                     set.TransactionClasses)
            {
                set.TransactionClassCountsBySession.TryGetValue(
                    classId,
                    out IReadOnlyDictionary<string, long>? sessionCounts);
                long total = sessionCounts?.Values.Sum() ?? 0;
                if (total != item.Count)
                {
                    throw new CampaignInputValidationException(
                        "Transaction class totals disagree between sanitized reports.");
                }
            }

            if (set.TransactionClassCountsBySession.Keys.Any(
                    classId => !set.TransactionClasses.ContainsKey(classId)))
            {
                throw new CampaignInputValidationException(
                    "Sanitized session frequencies reference an unknown transaction class.");
            }
        }
    }

    private static CampaignRunReport CreateRunReport(
        CampaignAnalysisSet set,
        string runLabel)
    {
        CampaignRunStructure structure = set.Structure;
        return new CampaignRunReport(
            runLabel,
            set.CanonicalFingerprint,
            set.AnalyzerToolVersion,
            set.RecorderSchemaVersion,
            set.InputCaptureSha256,
            set.ReportHashes,
            structure.RecordCount,
            structure.FunctionCounts,
            structure.StatusCounts,
            structure.PhaseCounts,
            structure.OpenSessionCount,
            structure.MatchedPairs,
            structure.UnmatchedWrites,
            structure.UnexpectedReads,
            structure.RedundantCloses,
            structure.FailedCloses,
            structure.UnclosedHandles,
            structure.ReconnectTransitions,
            structure.TransactionLatencyMicroseconds,
            structure.TransactionCadenceMicroseconds,
            structure.FunctionCadenceMicroseconds);
    }

    private static CampaignTransactionClassReport[] BuildClassComparison(
        IReadOnlyList<CampaignAnalysisSet> sets,
        IReadOnlyList<string> runLabels)
    {
        string[] classIds = sets
            .SelectMany(set => set.TransactionClasses.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        List<CampaignTransactionClassReport> reports = [];
        foreach (string classId in classIds)
        {
            CampaignTransactionClass template = sets
                .Select(set => set.TransactionClasses.GetValueOrDefault(classId))
                .First(item => item is not null)!;
            CampaignTransactionClass[] present = sets
                .Select(set => set.TransactionClasses.GetValueOrDefault(classId))
                .Where(item => item is not null)
                .Cast<CampaignTransactionClass>()
                .ToArray();
            if (present.Any(item =>
                    item.WriteClassId != template.WriteClassId ||
                    item.ReadClassId != template.ReadClassId ||
                    item.WriteLength != template.WriteLength ||
                    item.ReadLength != template.ReadLength))
            {
                throw new CampaignInputValidationException(
                    "A transaction class fingerprint has incompatible structure across runs.");
            }

            int presence = present.Length;
            CampaignClassRunReport[] runReports = sets
                .Select((set, index) =>
                    CreateClassRunReport(
                        set,
                        classId,
                        runLabels[index]))
                .ToArray();
            reports.Add(new CampaignTransactionClassReport(
                classId,
                template.WriteClassId,
                template.ReadClassId,
                template.WriteLength,
                template.ReadLength,
                presence,
                presence switch
                {
                    3 => "all_three_runs",
                    2 => "two_runs",
                    _ => "one_run"
                },
                "confirmed",
                presence == 3,
                presence == 3
                    ? "Stable across all three captures means the exact same sanitized transaction-class fingerprint is present in every verified run; it does not establish meaning, safety, or replay suitability."
                    : "The sanitized transaction-class fingerprint is absent from one or more verified runs and is not labeled stable.",
                runReports));
        }

        return reports.ToArray();
    }

    private static CampaignClassRunReport CreateClassRunReport(
        CampaignAnalysisSet set,
        string classId,
        string runLabel)
    {
        if (!set.TransactionClasses.TryGetValue(
                classId,
                out CampaignTransactionClass? item))
        {
            return new CampaignClassRunReport(
                runLabel,
                0,
                null,
                null,
                new SortedDictionary<string, long>(StringComparer.Ordinal),
                [],
                null,
                null);
        }

        set.TransactionClassCountsBySession.TryGetValue(
            classId,
            out IReadOnlyDictionary<string, long>? sessionCounts);
        SortedDictionary<string, long> nonzeroSessionCounts =
            new(StringComparer.Ordinal);
        foreach ((string session, long count) in
                 sessionCounts ?? new Dictionary<string, long>())
        {
            if (count > 0)
            {
                nonzeroSessionCounts.Add(session, count);
            }
        }

        string[] overlappingPhases = set.Phases
            .Where(phase =>
                phase.StartSequence <= item.LastReadSequence &&
                phase.EndSequence >= item.FirstWriteSequence)
            .Select(phase => phase.Phase)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return new CampaignClassRunReport(
            runLabel,
            item.Count,
            item.FirstWriteSequence,
            item.LastReadSequence,
            nonzeroSessionCounts,
            overlappingPhases,
            item.LatencyMicroseconds,
            item.QueuePolls);
    }

    private static CampaignVariabilityFamilyReport[]
        BuildVariabilityComparison(
            IReadOnlyList<CampaignAnalysisSet> sets,
            IReadOnlyList<string> runLabels)
    {
        string[] familyIds = sets
            .SelectMany(set => set.VariabilityFamilies.Keys)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return familyIds
            .Select(familyId =>
            {
                CampaignVariabilityFamily template = sets
                    .Select(set => set.VariabilityFamilies.GetValueOrDefault(familyId))
                    .First(item => item is not null)!;
                CampaignVariabilityFamily[] present = sets
                    .Select(set => set.VariabilityFamilies.GetValueOrDefault(familyId))
                    .Where(item => item is not null)
                    .Cast<CampaignVariabilityFamily>()
                    .ToArray();
                if (present.Any(item =>
                        item.Direction != template.Direction ||
                        item.Length != template.Length))
                {
                    throw new CampaignInputValidationException(
                        "A same-length variability family has incompatible structure across runs.");
                }

                CampaignVariabilityRunReport[] runs = sets
                    .Select((set, index) =>
                    {
                        if (!set.VariabilityFamilies.TryGetValue(
                                familyId,
                                out CampaignVariabilityFamily? item))
                        {
                            return new CampaignVariabilityRunReport(
                                runLabels[index],
                                false,
                                0,
                                null,
                                null,
                                []);
                        }

                        return new CampaignVariabilityRunReport(
                            runLabels[index],
                            true,
                            item.SampleCount,
                            item.FixedPrefixLength,
                            item.FixedSuffixLength,
                            item.Positions);
                    })
                    .ToArray();
                return new CampaignVariabilityFamilyReport(
                    familyId,
                    template.Direction,
                    template.Length,
                    present.Length,
                    present.Skip(1).All(item =>
                        VariabilityMetricsEqual(present[0], item)),
                    runs);
            })
            .ToArray();
    }

    private static bool VariabilityMetricsEqual(
        CampaignVariabilityFamily first,
        CampaignVariabilityFamily second) =>
        first.SampleCount == second.SampleCount &&
        first.FixedPrefixLength == second.FixedPrefixLength &&
        first.FixedSuffixLength == second.FixedSuffixLength &&
        first.Positions.SequenceEqual(second.Positions);
}
