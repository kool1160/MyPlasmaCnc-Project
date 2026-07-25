using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyPlasm.Inspector.ProtocolAnalysis;

public sealed class CaptureAnalyzer
{
    public const string ToolVersion = "1.0.0";

    public async Task<AnalysisResult> AnalyzeAsync(
        AnalysisRequest request,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.InputPath))
        {
            throw new ArgumentException("An explicit input file is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.OutputDirectory))
        {
            throw new ArgumentException("An explicit output directory is required.", nameof(request));
        }

        string inputPath = Path.GetFullPath(request.InputPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The input capture does not exist.", inputPath);
        }

        string inputSha256 = await CalculateSha256Async(inputPath, cancellationToken)
            .ConfigureAwait(false);
        if (request.ExpectedSha256 is { } expected)
        {
            string normalizedExpected = NormalizeSha256(expected);
            if (!string.Equals(normalizedExpected, inputSha256, StringComparison.Ordinal))
            {
                throw new InputHashMismatchException(normalizedExpected, inputSha256);
            }
        }

        AnalysisAccumulator accumulator = new(Path.GetFileName(inputPath), inputSha256);
        CaptureRecordReader reader = new();
        await foreach (CaptureRecord record in reader.ReadAsync(
                           inputPath,
                           progress,
                           cancellationToken))
        {
            accumulator.Accept(record);
        }

        string finalInputSha256 = await CalculateSha256Async(inputPath, cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(inputSha256, finalInputSha256, StringComparison.Ordinal))
        {
            throw new InputHashMismatchException(inputSha256, finalInputSha256);
        }

        ReportBundle reports = accumulator.Complete();
        IReadOnlyDictionary<string, string> outputHashes =
            await ReportWriter.WriteAsync(
                    Path.GetFullPath(request.OutputDirectory),
                    request.Overwrite,
                    reports,
                    cancellationToken)
                .ConfigureAwait(false);

        return new AnalysisResult(
            inputSha256,
            reports.Summary.RecordCount,
            reports.Summary.Transactions.Matched,
            outputHashes);
    }

    public static async Task<string> CalculateSha256Async(
        string path,
        CancellationToken cancellationToken = default)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static string NormalizeSha256(string value)
    {
        string normalized = value.Trim().ToUpperInvariant();
        if (normalized.Length != 64 || normalized.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException(
                "Expected SHA-256 must contain exactly 64 hexadecimal characters.",
                nameof(value));
        }

        return normalized;
    }

    private sealed class AnalysisAccumulator
    {
        private static readonly HashSet<string> ConfigurationFunctions =
        [
            "FT_SetBaudRate",
            "FT_SetDataCharacteristics",
            "FT_SetFlowControl",
            "FT_SetLatencyTimer",
            "FT_SetBitMode"
        ];

        private readonly string inputFileName;
        private readonly string inputSha256;
        private readonly SortedDictionary<string, long> functionCounts =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, SortedDictionary<string, long>> statusCounts =
            new(StringComparer.Ordinal);
        private readonly SortedDictionary<string, List<long>> functionCadences =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> lastFunctionElapsed =
            new(StringComparer.Ordinal);
        private readonly Dictionary<ulong, HandleSessionState> activeHandles = [];
        private readonly List<HandleSessionState> handleSessions = [];
        private readonly List<PhaseRow> phases = [];
        private readonly PayloadAnalysisAccumulator payloads = new();
        private readonly List<TransactionObservation> transactions = [];
        private readonly List<long> writeRequested = [];
        private readonly List<long> writeActual = [];
        private readonly List<long> readRequested = [];
        private readonly List<long> readActual = [];
        private readonly List<long> transactionLatencies = [];
        private readonly List<long> transactionQueuePolls = [];
        private readonly List<long> transactionCadences = [];

        private CaptureRecord? firstRecord;
        private CaptureRecord? lastRecord;
        private CaptureRecord? recordBeforeFirstSuccessfulOpen;
        private CaptureRecord? lastSuccessfulClose;
        private bool hasSuccessfulOpen;
        private long recordCount;
        private long sequenceGapCount;
        private ulong missingSequenceCount;
        private int nextHandleSession;
        private long failedOpens;
        private long failedWrites;
        private long failedReads;
        private long failedCloses;
        private long redundantCloses;
        private long unclosedHandles;
        private long unmatchedWrites;
        private long unexpectedReads;
        private long successfulWritesWithoutOpenHandle;
        private long successfulReadsWithoutOpenHandle;
        private long pairingUncertainties;

        public AnalysisAccumulator(string inputFileName, string inputSha256)
        {
            this.inputFileName = inputFileName;
            this.inputSha256 = inputSha256;
        }

        public void Accept(CaptureRecord record)
        {
            if (lastRecord is not null && record.Sequence > lastRecord.Sequence + 1)
            {
                sequenceGapCount++;
                missingSequenceCount += record.Sequence - lastRecord.Sequence - 1;
            }

            firstRecord ??= record;
            if (!hasSuccessfulOpen)
            {
                recordBeforeFirstSuccessfulOpen = record.Function == "FT_OpenEx" && record.Status == 0
                    ? lastRecord
                    : record;
            }

            recordCount++;
            Increment(functionCounts, record.Function);
            if (lastFunctionElapsed.TryGetValue(record.Function, out long priorElapsed))
            {
                long cadence = record.ElapsedMicroseconds - priorElapsed;
                if (cadence >= 0)
                {
                    if (!functionCadences.TryGetValue(
                            record.Function,
                            out List<long>? cadenceValues))
                    {
                        cadenceValues = [];
                        functionCadences.Add(record.Function, cadenceValues);
                    }

                    cadenceValues.Add(cadence);
                }
            }

            lastFunctionElapsed[record.Function] = record.ElapsedMicroseconds;
            if (!statusCounts.TryGetValue(record.Function, out SortedDictionary<string, long>? statuses))
            {
                statuses = new SortedDictionary<string, long>(StringComparer.Ordinal);
                statusCounts.Add(record.Function, statuses);
            }

            Increment(statuses, record.Status.ToString(CultureInfo.InvariantCulture));

            switch (record.Function)
            {
                case "FT_ListDevices":
                    AddSingleRecordPhase(
                        record,
                        "enumeration_attempt",
                        string.Empty,
                        "confirmed",
                        "An FT_ListDevices record is an enumeration attempt.");
                    break;
                case "FT_OpenEx":
                    AcceptOpen(record);
                    break;
                case "FT_Close":
                    AcceptClose(record);
                    break;
                case "FT_Write":
                    AcceptWrite(record);
                    break;
                case "FT_Read":
                    AcceptRead(record);
                    break;
                case "FT_GetQueueStatus":
                    AcceptQueuePoll(record);
                    break;
                default:
                    if (ConfigurationFunctions.Contains(record.Function))
                    {
                        AcceptConfiguration(record);
                    }

                    break;
            }

            lastRecord = record;
        }

        public ReportBundle Complete()
        {
            if (firstRecord is null || lastRecord is null)
            {
                throw new InvalidOperationException("The validated capture unexpectedly had no records.");
            }

            foreach (HandleSessionState state in handleSessions.Where(
                         state => !state.IsClosed && !state.UnclosedAlreadyCounted))
            {
                unclosedHandles++;
                state.UnclosedAlreadyCounted = true;
                DrainUnmatchedWrites(state);
                phases.Add(new PhaseRow(
                    "confirmed",
                    "unclosed_handle_at_capture_end",
                    state.Label,
                    state.OpenRecord.Sequence,
                    lastRecord.Sequence,
                    state.OpenRecord.ElapsedMicroseconds,
                    lastRecord.ElapsedMicroseconds,
                    null,
                    "A successful open has no later successful close for the same open-handle session."));
            }

            if (recordBeforeFirstSuccessfulOpen is not null)
            {
                phases.Add(new PhaseRow(
                    "confirmed",
                    "process_start_pre_open",
                    string.Empty,
                    firstRecord.Sequence,
                    recordBeforeFirstSuccessfulOpen.Sequence,
                    firstRecord.ElapsedMicroseconds,
                    recordBeforeFirstSuccessfulOpen.ElapsedMicroseconds,
                    null,
                    "Records from capture start through the record immediately before the first successful open."));
            }

            if (lastSuccessfulClose is not null &&
                handleSessions.All(state => state.IsClosed) &&
                lastRecord.Sequence > lastSuccessfulClose.Sequence)
            {
                phases.Add(new PhaseRow(
                    "confirmed",
                    "process_end_tail",
                    string.Empty,
                    lastSuccessfulClose.Sequence,
                    lastRecord.Sequence,
                    lastSuccessfulClose.ElapsedMicroseconds,
                    lastRecord.ElapsedMicroseconds,
                    null,
                    "Records after the last successful close while no handle remains open."));
            }

            foreach (HandleSessionState state in handleSessions.Where(
                         state => state.FirstTransactionWrite is not null))
            {
                phases.Add(new PhaseRow(
                    "confirmed",
                    "sustained_exchange_interval",
                    state.Label,
                    state.FirstTransactionWrite!.Sequence,
                    state.LastTransactionRead!.Sequence,
                    state.FirstTransactionWrite.ElapsedMicroseconds,
                    state.LastTransactionRead.ElapsedMicroseconds,
                    0,
                    "Interval from the first deterministically paired successful write to the last paired successful read in one open-handle session."));
            }

            IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> statusReport =
                statusCounts.ToDictionary(
                    pair => pair.Key,
                    pair => (IReadOnlyDictionary<string, long>)pair.Value,
                    StringComparer.Ordinal);

            HandleSessionReport[] handleSessionReports = handleSessions
                .OrderBy(state => state.Ordinal)
                .Select(state => state.ToReport())
                .ToArray();

            CaptureSummaryReport summary = new(
                ToolVersion,
                firstRecord.SchemaVersion,
                inputFileName,
                inputSha256,
                recordCount,
                1,
                new SequenceReport(
                    firstRecord.Sequence,
                    lastRecord.Sequence,
                    sequenceGapCount,
                    missingSequenceCount),
                firstRecord.TimestampUtc,
                lastRecord.TimestampUtc,
                functionCounts,
                statusReport,
                functionCadences.ToDictionary(
                    pair => pair.Key,
                    pair => Statistics.Summarize(pair.Value),
                    StringComparer.Ordinal),
                new CallLengthReport(
                    Statistics.Summarize(writeRequested),
                    Statistics.Summarize(writeActual),
                    Statistics.Summarize(readRequested),
                    Statistics.Summarize(readActual)),
                new TransactionSummaryReport(
                    transactions.Count,
                    transactions.Count,
                    unmatchedWrites,
                    unexpectedReads,
                    pairingUncertainties,
                    Statistics.Summarize(transactionLatencies),
                    Statistics.Summarize(transactionQueuePolls),
                    Statistics.Summarize(transactionCadences)),
                new AnomalyReport(
                    failedOpens,
                    failedWrites,
                    failedReads,
                    failedCloses,
                    redundantCloses,
                    unclosedHandles,
                    successfulWritesWithoutOpenHandle,
                    successfulReadsWithoutOpenHandle),
                handleSessionReports,
                BuildSessionFrequencyComparison(handleSessionReports));

            return new ReportBundle(
                summary,
                payloads.ToReport(),
                phases
                    .OrderBy(phase => phase.StartSequence)
                    .ThenBy(phase => phase.EndSequence)
                    .ThenBy(phase => phase.Phase, StringComparer.Ordinal)
                    .ThenBy(phase => phase.SessionLabel, StringComparer.Ordinal)
                    .ToArray(),
                BuildTransactionClasses());
        }

        private void AcceptOpen(CaptureRecord record)
        {
            if (record.Status != 0)
            {
                failedOpens++;
                AddSingleRecordPhase(
                    record,
                    "open_failed",
                    string.Empty,
                    "confirmed",
                    "FT_OpenEx returned a nonzero FT status.");
                return;
            }

            hasSuccessfulOpen = true;
            if (record.HandleId != 0 &&
                activeHandles.Remove(record.HandleId, out HandleSessionState? prior))
            {
                unclosedHandles++;
                prior.UnclosedAlreadyCounted = true;
                DrainUnmatchedWrites(prior);
                phases.Add(new PhaseRow(
                    "confirmed",
                    "unclosed_handle_before_reopen",
                    prior.Label,
                    prior.OpenRecord.Sequence,
                    record.Sequence,
                    prior.OpenRecord.ElapsedMicroseconds,
                    record.ElapsedMicroseconds,
                    null,
                    "The same recorder handle identifier was successfully opened again without an intervening successful close."));
            }

            HandleSessionState state = new(++nextHandleSession, record);
            handleSessions.Add(state);
            if (record.HandleId != 0)
            {
                activeHandles.Add(record.HandleId, state);
            }

            AddSingleRecordPhase(
                record,
                "open_success",
                state.Label,
                "confirmed",
                "FT_OpenEx returned status 0; the recorder-assigned handle is represented only by a sanitized session label.");

            if (lastSuccessfulClose is not null)
            {
                phases.Add(new PhaseRow(
                    "confirmed",
                    "reconnect_transition",
                    state.Label,
                    lastSuccessfulClose.Sequence,
                    record.Sequence,
                    lastSuccessfulClose.ElapsedMicroseconds,
                    record.ElapsedMicroseconds,
                    0,
                    "A successful open occurred after an earlier successful close."));
            }
        }

        private void AcceptClose(CaptureRecord record)
        {
            HandleSessionState? state = null;
            bool hasActiveState = record.HandleId != 0 &&
                activeHandles.TryGetValue(record.HandleId, out state);

            if (record.Status == 0 && hasActiveState)
            {
                state!.CloseRecord = record;
                activeHandles.Remove(record.HandleId);
                DrainUnmatchedWrites(state);
                lastSuccessfulClose = record;
                AddSingleRecordPhase(
                    record,
                    "close_success",
                    state.Label,
                    "confirmed",
                    "FT_Close returned status 0 for a currently open handle session.");
                return;
            }

            if (record.Status != 0)
            {
                failedCloses++;
            }

            bool redundant = !hasActiveState;
            if (redundant)
            {
                redundantCloses++;
            }

            AddSingleRecordPhase(
                record,
                redundant ? "redundant_close" : "close_failed",
                hasActiveState ? state!.Label : string.Empty,
                "confirmed",
                redundant
                    ? "FT_Close targeted no currently open recorder handle session."
                    : "FT_Close returned a nonzero FT status and the handle remains open.");
        }

        private void AcceptWrite(CaptureRecord record)
        {
            writeRequested.Add(record.RequestedCount!.Value);
            writeActual.Add(record.ActualCount!.Value);
            string classId = payloads.Add("write", record.Payload!, record.Sequence);

            if (record.Status != 0)
            {
                failedWrites++;
                return;
            }

            if (!TryGetActive(record.HandleId, out HandleSessionState? state))
            {
                successfulWritesWithoutOpenHandle++;
                unmatchedWrites++;
                return;
            }

            Increment(state!.PayloadClassCounts, classId);
            state.PendingWrites.Enqueue(new PendingWrite(
                record,
                classId,
                state.QueuePollCount,
                record.ActualCount != record.RequestedCount));
        }

        private void AcceptRead(CaptureRecord record)
        {
            readRequested.Add(record.RequestedCount!.Value);
            readActual.Add(record.ActualCount!.Value);
            string classId = payloads.Add("read", record.Payload!, record.Sequence);

            if (record.Status != 0)
            {
                failedReads++;
                return;
            }

            if (!TryGetActive(record.HandleId, out HandleSessionState? state))
            {
                successfulReadsWithoutOpenHandle++;
                unexpectedReads++;
                return;
            }

            Increment(state!.PayloadClassCounts, classId);
            if (!state.PendingWrites.TryDequeue(out PendingWrite? pending))
            {
                unexpectedReads++;
                return;
            }

            long latency = record.ElapsedMicroseconds - pending.Record.ElapsedMicroseconds;
            if (latency >= 0)
            {
                transactionLatencies.Add(latency);
            }

            bool uncertain = latency < 0 ||
                pending.IsPartialWrite ||
                record.ActualCount == 0;
            if (uncertain)
            {
                pairingUncertainties++;
            }

            long queuePolls = state.QueuePollCount - pending.QueuePollBaseline;
            transactionQueuePolls.Add(queuePolls);
            if (state.LastTransactionWriteElapsed is { } previousWriteElapsed)
            {
                long cadence = pending.Record.ElapsedMicroseconds - previousWriteElapsed;
                if (cadence >= 0)
                {
                    transactionCadences.Add(cadence);
                }
            }

            state.LastTransactionWriteElapsed = pending.Record.ElapsedMicroseconds;
            string transactionClassId = TransactionClassId(pending.ClassId, classId);
            TransactionObservation transaction = new(
                state.Label,
                pending.Record,
                record,
                pending.ClassId,
                classId,
                transactionClassId,
                latency,
                queuePolls);
            transactions.Add(transaction);
            Increment(state.TransactionClassCounts, transactionClassId);
            state.FirstTransactionWrite ??= pending.Record;
            state.LastTransactionRead = record;
        }

        private void AcceptQueuePoll(CaptureRecord record)
        {
            if (record.Status != 0 ||
                !TryGetActive(record.HandleId, out HandleSessionState? state))
            {
                return;
            }

            state!.QueuePollCount++;
            state.FirstQueuePollElapsed ??= record.ElapsedMicroseconds;
            state.LastQueuePollElapsed = record.ElapsedMicroseconds;
        }

        private void AcceptConfiguration(CaptureRecord record)
        {
            if (record.Status != 0 ||
                !TryGetActive(record.HandleId, out HandleSessionState? state))
            {
                return;
            }

            SortedDictionary<string, long> values = new(StringComparer.Ordinal);
            foreach ((string name, long value) in record.Settings)
            {
                values.Add(name, value);
            }
            state!.Configuration.Add(new ConfigurationReport(
                record.Sequence,
                record.TimestampUtc,
                record.Function,
                values));
            AddSingleRecordPhase(
                record,
                "configuration_call",
                state.Label,
                "confirmed",
                "A successful documented D2XX configuration function was recorded for this open-handle session.");
        }

        private IReadOnlyList<TransactionClassReport> BuildTransactionClasses()
        {
            return transactions
                .GroupBy(transaction => transaction.ClassId, StringComparer.Ordinal)
                .Select(group =>
                {
                    TransactionObservation first = group
                        .OrderBy(transaction => transaction.Write.Sequence)
                        .First();
                    return new TransactionClassReport(
                        group.Key,
                        first.WriteClassId,
                        first.ReadClassId,
                        group.LongCount(),
                        group.Min(transaction => transaction.Write.Sequence),
                        group.Max(transaction => transaction.Read.Sequence),
                        Statistics.Summarize(
                            group.Where(transaction => transaction.LatencyMicroseconds >= 0)
                                .Select(transaction => transaction.LatencyMicroseconds)),
                        Statistics.Summarize(
                            group.Select(transaction => transaction.QueuePollCount)));
                })
                .OrderBy(report => report.ClassId, StringComparer.Ordinal)
                .ToArray();
        }

        private static IReadOnlyList<SessionClassFrequencyReport>
            BuildSessionFrequencyComparison(IReadOnlyList<HandleSessionReport> sessions)
        {
            List<SessionClassFrequencyReport> reports = [];
            AddFrequencyReports(
                reports,
                "payload",
                sessions,
                session => session.PayloadClassCounts);
            AddFrequencyReports(
                reports,
                "transaction",
                sessions,
                session => session.TransactionClassCounts);
            return reports
                .OrderBy(report => report.ClassKind, StringComparer.Ordinal)
                .ThenBy(report => report.ClassId, StringComparer.Ordinal)
                .ToArray();
        }

        private static void AddFrequencyReports(
            ICollection<SessionClassFrequencyReport> destination,
            string classKind,
            IReadOnlyList<HandleSessionReport> sessions,
            Func<HandleSessionReport, IReadOnlyDictionary<string, long>> selectCounts)
        {
            string[] classIds = sessions
                .SelectMany(session => selectCounts(session).Keys)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            foreach (string classId in classIds)
            {
                SortedDictionary<string, long> counts = new(StringComparer.Ordinal);
                foreach (HandleSessionReport session in sessions)
                {
                    selectCounts(session).TryGetValue(classId, out long count);
                    counts.Add(session.SessionLabel, count);
                }

                destination.Add(new SessionClassFrequencyReport(
                    classKind,
                    classId,
                    counts));
            }
        }

        private void DrainUnmatchedWrites(HandleSessionState state)
        {
            unmatchedWrites += state.PendingWrites.Count;
            state.PendingWrites.Clear();
        }

        private bool TryGetActive(ulong handleId, out HandleSessionState? state)
        {
            if (handleId != 0 && activeHandles.TryGetValue(handleId, out state))
            {
                return true;
            }

            state = null;
            return false;
        }

        private void AddSingleRecordPhase(
            CaptureRecord record,
            string phase,
            string sessionLabel,
            string classification,
            string evidenceRule)
        {
            phases.Add(new PhaseRow(
                classification,
                phase,
                sessionLabel,
                record.Sequence,
                record.Sequence,
                record.ElapsedMicroseconds,
                record.ElapsedMicroseconds,
                record.Status,
                evidenceRule));
        }

        private static string TransactionClassId(string writeClassId, string readClassId)
        {
            byte[] material = Encoding.UTF8.GetBytes($"{writeClassId}\n{readClassId}");
            return $"T-{Convert.ToHexString(SHA256.HashData(material))}";
        }

        private static void Increment(IDictionary<string, long> values, string key)
        {
            values.TryGetValue(key, out long current);
            values[key] = current + 1;
        }
    }

    private sealed class HandleSessionState
    {
        public HandleSessionState(int ordinal, CaptureRecord openRecord)
        {
            Ordinal = ordinal;
            OpenRecord = openRecord;
            Label = $"session-{ordinal:D4}";
        }

        public int Ordinal { get; }

        public string Label { get; }

        public CaptureRecord OpenRecord { get; }

        public CaptureRecord? CloseRecord { get; set; }

        public bool IsClosed => CloseRecord is not null;

        public bool UnclosedAlreadyCounted { get; set; }

        public Queue<PendingWrite> PendingWrites { get; } = new();

        public long QueuePollCount { get; set; }

        public long? FirstQueuePollElapsed { get; set; }

        public long? LastQueuePollElapsed { get; set; }

        public long? LastTransactionWriteElapsed { get; set; }

        public CaptureRecord? FirstTransactionWrite { get; set; }

        public CaptureRecord? LastTransactionRead { get; set; }

        public List<ConfigurationReport> Configuration { get; } = [];

        public SortedDictionary<string, long> PayloadClassCounts { get; } =
            new(StringComparer.Ordinal);

        public SortedDictionary<string, long> TransactionClassCounts { get; } =
            new(StringComparer.Ordinal);

        public HandleSessionReport ToReport()
        {
            long? queueDuration = FirstQueuePollElapsed is not null && LastQueuePollElapsed is not null
                ? LastQueuePollElapsed.Value - FirstQueuePollElapsed.Value
                : null;
            return new HandleSessionReport(
                Label,
                OpenRecord.Sequence,
                OpenRecord.TimestampUtc,
                OpenRecord.Status,
                CloseRecord?.Sequence,
                CloseRecord?.TimestampUtc,
                CloseRecord?.Status,
                IsClosed,
                QueuePollCount,
                queueDuration,
                Configuration.OrderBy(item => item.Sequence).ToArray(),
                PayloadClassCounts,
                TransactionClassCounts);
        }
    }

    private sealed record PendingWrite(
        CaptureRecord Record,
        string ClassId,
        long QueuePollBaseline,
        bool IsPartialWrite);

    private sealed record TransactionObservation(
        string SessionLabel,
        CaptureRecord Write,
        CaptureRecord Read,
        string WriteClassId,
        string ReadClassId,
        string ClassId,
        long LatencyMicroseconds,
        long QueuePollCount);

    private sealed class PayloadAnalysisAccumulator
    {
        private readonly Dictionary<string, PayloadClassMutable> classes =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, PayloadFamilyMutable> families =
            new(StringComparer.Ordinal);

        public string Add(string direction, byte[] payload, ulong sequence)
        {
            string fingerprint = Convert.ToHexString(SHA256.HashData(payload));
            string prefix = direction == "write" ? "W" : "R";
            string classId = $"{prefix}-{payload.Length}-{fingerprint}";
            if (!classes.TryGetValue(classId, out PayloadClassMutable? payloadClass))
            {
                payloadClass = new PayloadClassMutable(
                    classId,
                    direction,
                    payload.Length,
                    fingerprint,
                    sequence);
                classes.Add(classId, payloadClass);
            }

            payloadClass.Count++;
            payloadClass.LastSequence = sequence;

            string familyId = $"{prefix}-{payload.Length}";
            if (!families.TryGetValue(familyId, out PayloadFamilyMutable? family))
            {
                family = new PayloadFamilyMutable(familyId, direction, payload.Length);
                families.Add(familyId, family);
            }

            family.Add(payload);
            return classId;
        }

        public PayloadVariabilityReport ToReport() =>
            new(
                "confirmed",
                "Exact classes use direction, byte length, and SHA-256. Same-length families report observed byte-position variability only.",
                classes.Values
                    .OrderBy(item => item.ClassId, StringComparer.Ordinal)
                    .Select(item => item.ToReport())
                    .ToArray(),
                families.Values
                    .OrderBy(item => item.FamilyId, StringComparer.Ordinal)
                    .Select(item => item.ToReport())
                    .ToArray());
    }

    private sealed class PayloadClassMutable
    {
        public PayloadClassMutable(
            string classId,
            string direction,
            int length,
            string sha256,
            ulong firstSequence)
        {
            ClassId = classId;
            Direction = direction;
            Length = length;
            Sha256 = sha256;
            FirstSequence = firstSequence;
            LastSequence = firstSequence;
        }

        public string ClassId { get; }

        public string Direction { get; }

        public int Length { get; }

        public string Sha256 { get; }

        public long Count { get; set; }

        public ulong FirstSequence { get; }

        public ulong LastSequence { get; set; }

        public PayloadClassReport ToReport() =>
            new(
                ClassId,
                Direction,
                Length,
                Sha256,
                Count,
                FirstSequence,
                LastSequence);
    }

    private sealed class PayloadFamilyMutable
    {
        private readonly List<Dictionary<byte, long>> positionCounts;

        public PayloadFamilyMutable(string familyId, string direction, int length)
        {
            FamilyId = familyId;
            Direction = direction;
            Length = length;
            positionCounts = Enumerable.Range(0, length)
                .Select(_ => new Dictionary<byte, long>())
                .ToList();
        }

        public string FamilyId { get; }

        public string Direction { get; }

        public int Length { get; }

        public long SampleCount { get; private set; }

        public void Add(byte[] payload)
        {
            SampleCount++;
            for (int index = 0; index < payload.Length; index++)
            {
                Dictionary<byte, long> counts = positionCounts[index];
                counts.TryGetValue(payload[index], out long current);
                counts[payload[index]] = current + 1;
            }
        }

        public PayloadFamilyReport ToReport()
        {
            int prefix = 0;
            while (prefix < positionCounts.Count && positionCounts[prefix].Count == 1)
            {
                prefix++;
            }

            int suffix = 0;
            while (suffix < positionCounts.Count &&
                   positionCounts[positionCounts.Count - suffix - 1].Count == 1)
            {
                suffix++;
            }

            return new PayloadFamilyReport(
                FamilyId,
                Direction,
                Length,
                SampleCount,
                prefix,
                suffix,
                positionCounts
                    .Select((counts, index) => new BytePositionReport(
                        index,
                        counts.Count,
                        Statistics.Entropy(counts.Values)))
                    .ToArray());
        }
    }
}
