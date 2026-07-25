using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace MyPlasm.Inspector.ProtocolAnalysis;

internal sealed class CaptureRecordReader
{
    private static readonly HashSet<string> SupportedFunctions =
    [
        "FT_ListDevices",
        "FT_OpenEx",
        "FT_Close",
        "FT_Read",
        "FT_Write",
        "FT_SetBaudRate",
        "FT_SetDataCharacteristics",
        "FT_SetFlowControl",
        "FT_GetQueueStatus",
        "FT_SetLatencyTimer",
        "FT_SetBitMode"
    ];

    private static readonly HashSet<string> FlushTriggers =
    [
        "none",
        "byte_threshold",
        "time_threshold",
        "close"
    ];

    public async IAsyncEnumerable<CaptureRecord> ReadAsync(
        string path,
        IProgress<AnalysisProgress>? progress,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        long lineNumber = 0;
        long recordCount = 0;
        ulong previousSequence = 0;
        Guid? expectedSession = null;
        uint? expectedProcess = null;
        byte[] readBuffer = new byte[64 * 1024];
        ArrayBufferWriter<byte> lineBuffer = new();

        CaptureRecord? ParseValidatedLine(byte[] bytes)
        {
            string line;
            try
            {
                int offset = lineNumber == 1 &&
                    bytes.Length >= 3 &&
                    bytes[0] == 0xEF &&
                    bytes[1] == 0xBB &&
                    bytes[2] == 0xBF
                        ? 3
                        : 0;
                int count = bytes.Length - offset;
                if (count > 0 && bytes[^1] == (byte)'\r')
                {
                    count--;
                }

                line = new UTF8Encoding(
                    encoderShouldEmitUTF8Identifier: false,
                    throwOnInvalidBytes: true).GetString(bytes, offset, count);
            }
            catch (DecoderFallbackException exception)
            {
                throw new CaptureValidationException(
                    lineNumber,
                    $"invalid UTF-8 ({exception.Message}).");
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                return null;
            }

            CaptureRecord record = ParseRecord(line, lineNumber);
            if (recordCount > 0 && record.Sequence <= previousSequence)
            {
                throw new CaptureValidationException(
                    lineNumber,
                    $"sequence {record.Sequence} is not unique and monotonically increasing after {previousSequence}.");
            }

            expectedSession ??= record.SessionId;
            if (record.SessionId != expectedSession)
            {
                throw new CaptureValidationException(
                    lineNumber,
                    "session_id changed within one capture.");
            }

            expectedProcess ??= record.ProcessId;
            if (record.ProcessId != expectedProcess)
            {
                throw new CaptureValidationException(
                    lineNumber,
                    "process_id changed within one recorder session.");
            }

            previousSequence = record.Sequence;
            recordCount++;
            if (recordCount % 25_000 == 0)
            {
                progress?.Report(new AnalysisProgress(recordCount, lineNumber));
            }

            return record;
        }

        while (true)
        {
            int bytesRead = await stream.ReadAsync(readBuffer, cancellationToken)
                .ConfigureAwait(false);
            if (bytesRead == 0)
            {
                break;
            }

            int segmentStart = 0;
            for (int index = 0; index < bytesRead; index++)
            {
                if (readBuffer[index] != (byte)'\n')
                {
                    continue;
                }

                lineBuffer.Write(readBuffer.AsSpan(segmentStart, index - segmentStart));
                lineNumber++;
                byte[] lineBytes = lineBuffer.WrittenMemory.ToArray();
                lineBuffer.Clear();
                segmentStart = index + 1;
                CaptureRecord? record = ParseValidatedLine(lineBytes);
                if (record is not null)
                {
                    yield return record;
                }
            }

            if (segmentStart < bytesRead)
            {
                lineBuffer.Write(readBuffer.AsSpan(segmentStart, bytesRead - segmentStart));
            }
        }

        if (lineBuffer.WrittenCount > 0)
        {
            lineNumber++;
            CaptureRecord? finalRecord = ParseValidatedLine(
                lineBuffer.WrittenMemory.ToArray());
            if (finalRecord is not null)
            {
                yield return finalRecord;
            }
        }

        if (recordCount == 0)
        {
            throw new CaptureValidationException(
                Math.Max(1, lineNumber),
                "capture contains no nonempty JSON records.");
        }

        progress?.Report(new AnalysisProgress(recordCount, lineNumber));
    }

    private static CaptureRecord ParseRecord(string line, long lineNumber)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            RequireKind(root, JsonValueKind.Object, "record", lineNumber);

            int schemaVersion = GetRequiredInt32(root, "schema_version", lineNumber);
            if (schemaVersion != 1)
            {
                throw Error(lineNumber, $"unsupported schema_version {schemaVersion}; supported version is 1.");
            }

            string sessionText = GetRequiredString(root, "session_id", lineNumber);
            if (!Guid.TryParseExact(sessionText, "D", out Guid sessionId))
            {
                throw Error(lineNumber, "session_id must be a canonical GUID.");
            }

            string timestampText = GetRequiredString(root, "utc_timestamp", lineNumber);
            if (!DateTimeOffset.TryParse(
                    timestampText,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out DateTimeOffset timestamp) ||
                timestamp.Offset != TimeSpan.Zero)
            {
                throw Error(lineNumber, "utc_timestamp must be a valid UTC ISO-8601 timestamp.");
            }

            long elapsed = GetRequiredInt64(root, "elapsed_us", lineNumber);
            if (elapsed < 0)
            {
                throw Error(lineNumber, "elapsed_us must be nonnegative.");
            }

            uint processId = GetRequiredUInt32(root, "process_id", lineNumber);
            uint threadId = GetRequiredUInt32(root, "thread_id", lineNumber);
            if (processId == 0 || threadId == 0)
            {
                throw Error(lineNumber, "process_id and thread_id must be positive.");
            }

            string function = GetRequiredString(root, "function", lineNumber);
            if (!SupportedFunctions.Contains(function))
            {
                throw Error(lineNumber, $"unsupported function '{function}'.");
            }

            ulong sequence = GetRequiredUInt64(root, "sequence", lineNumber);
            if (sequence == 0)
            {
                throw Error(lineNumber, "sequence must be positive.");
            }

            ulong handleId = GetRequiredUInt64(root, "handle_id", lineNumber);
            uint status = GetRequiredUInt32(root, "status", lineNumber);
            string flushTrigger = GetRequiredString(root, "flush_trigger", lineNumber);
            if (!FlushTriggers.Contains(flushTrigger))
            {
                throw Error(lineNumber, $"flush_trigger '{flushTrigger}' is not supported.");
            }

            JsonElement arguments = GetRequiredObject(root, "arguments", lineNumber);
            uint? requestedCount = null;
            uint? actualCount = null;
            byte[]? payload = null;
            uint? queueCount = null;
            uint? deviceCount = null;
            Dictionary<string, long> settings = new(StringComparer.Ordinal);

            switch (function)
            {
                case "FT_ListDevices":
                    _ = GetRequiredString(arguments, "argument1", lineNumber);
                    _ = GetRequiredString(arguments, "argument2", lineNumber);
                    _ = GetRequiredUInt32(arguments, "flags", lineNumber);
                    deviceCount = GetRequiredUInt32(root, "device_count", lineNumber);
                    break;
                case "FT_OpenEx":
                    _ = GetRequiredString(arguments, "selector_pointer", lineNumber);
                    _ = GetRequiredString(arguments, "selector", lineNumber);
                    _ = GetRequiredUInt32(arguments, "flags", lineNumber);
                    _ = GetRequiredString(root, "returned_handle", lineNumber);
                    break;
                case "FT_Close":
                    _ = GetRequiredString(arguments, "handle", lineNumber);
                    break;
                case "FT_Read":
                    _ = GetRequiredString(arguments, "buffer", lineNumber);
                    requestedCount = GetRequiredUInt32(root, "requested_count", lineNumber);
                    actualCount = GetRequiredUInt32(root, "actual_count", lineNumber);
                    if (actualCount > requestedCount)
                    {
                        throw Error(lineNumber, "FT_Read actual_count exceeds requested_count.");
                    }

                    payload = ParseHex(
                        GetRequiredString(root, "read_hex", lineNumber),
                        actualCount.Value,
                        "read_hex",
                        lineNumber);
                    break;
                case "FT_Write":
                    _ = GetRequiredString(arguments, "buffer", lineNumber);
                    requestedCount = GetRequiredUInt32(root, "requested_count", lineNumber);
                    actualCount = GetRequiredUInt32(root, "actual_count", lineNumber);
                    if (actualCount > requestedCount)
                    {
                        throw Error(lineNumber, "FT_Write actual_count exceeds requested_count.");
                    }

                    payload = ParseHex(
                        GetRequiredString(root, "write_hex", lineNumber),
                        requestedCount.Value,
                        "write_hex",
                        lineNumber);
                    break;
                case "FT_SetBaudRate":
                    AddMatchingUInt32Setting(root, arguments, settings, "baud_rate", lineNumber);
                    break;
                case "FT_SetDataCharacteristics":
                    JsonElement characteristics =
                        GetRequiredObject(root, "data_characteristics", lineNumber);
                    AddMatchingByteSetting(
                        characteristics,
                        arguments,
                        settings,
                        "word_length",
                        lineNumber);
                    AddMatchingByteSetting(
                        characteristics,
                        arguments,
                        settings,
                        "stop_bits",
                        lineNumber);
                    AddMatchingByteSetting(
                        characteristics,
                        arguments,
                        settings,
                        "parity",
                        lineNumber);
                    break;
                case "FT_SetFlowControl":
                    JsonElement flow = GetRequiredObject(root, "flow_control", lineNumber);
                    AddRenamedMatchingUInt16Setting(
                        flow,
                        "mode",
                        arguments,
                        "flow_control",
                        settings,
                        lineNumber);
                    AddMatchingByteSetting(flow, arguments, settings, "xon", lineNumber);
                    AddMatchingByteSetting(flow, arguments, settings, "xoff", lineNumber);
                    break;
                case "FT_GetQueueStatus":
                    queueCount = GetRequiredUInt32(root, "queue_count", lineNumber);
                    break;
                case "FT_SetLatencyTimer":
                    AddMatchingByteSetting(root, arguments, settings, "latency_timer", lineNumber);
                    break;
                case "FT_SetBitMode":
                    JsonElement bitMode = GetRequiredObject(root, "bit_mode", lineNumber);
                    AddMatchingByteSetting(bitMode, arguments, settings, "mask", lineNumber);
                    AddMatchingByteSetting(bitMode, arguments, settings, "mode", lineNumber);
                    break;
            }

            return new CaptureRecord(
                schemaVersion,
                sessionId,
                timestamp.ToUniversalTime(),
                elapsed,
                processId,
                threadId,
                function,
                sequence,
                handleId,
                status,
                flushTrigger,
                requestedCount,
                actualCount,
                payload,
                queueCount,
                deviceCount,
                settings);
        }
        catch (CaptureValidationException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw Error(lineNumber, $"malformed JSON ({exception.Message}).");
        }
        catch (Exception exception) when (
            exception is FormatException or OverflowException or InvalidOperationException)
        {
            throw Error(lineNumber, exception.Message);
        }
    }

    private static byte[] ParseHex(
        string text,
        uint expectedByteCount,
        string fieldName,
        long lineNumber)
    {
        if (text.Length % 2 != 0)
        {
            throw Error(lineNumber, $"{fieldName} must have an even hexadecimal length.");
        }

        if (text.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw Error(lineNumber, $"{fieldName} contains a non-hexadecimal character.");
        }

        if ((ulong)text.Length / 2 != expectedByteCount)
        {
            throw Error(
                lineNumber,
                $"{fieldName} byte length does not match its recorded byte count.");
        }

        return Convert.FromHexString(text);
    }

    private static void AddMatchingUInt32Setting(
        JsonElement root,
        JsonElement arguments,
        IDictionary<string, long> settings,
        string name,
        long lineNumber)
    {
        uint first = GetRequiredUInt32(root, name, lineNumber);
        uint second = GetRequiredUInt32(arguments, name, lineNumber);
        EnsureEqual(first, second, name, lineNumber);
        settings.Add(name, first);
    }

    private static void AddMatchingByteSetting(
        JsonElement root,
        JsonElement arguments,
        IDictionary<string, long> settings,
        string name,
        long lineNumber)
    {
        byte first = GetRequiredByte(root, name, lineNumber);
        byte second = GetRequiredByte(arguments, name, lineNumber);
        EnsureEqual(first, second, name, lineNumber);
        settings.Add(name, first);
    }

    private static void AddRenamedMatchingUInt16Setting(
        JsonElement root,
        string rootName,
        JsonElement arguments,
        string argumentName,
        IDictionary<string, long> settings,
        long lineNumber)
    {
        ushort first = GetRequiredUInt16(root, rootName, lineNumber);
        ushort second = GetRequiredUInt16(arguments, argumentName, lineNumber);
        EnsureEqual(first, second, argumentName, lineNumber);
        settings.Add(argumentName, first);
    }

    private static void EnsureEqual<T>(T first, T second, string name, long lineNumber)
        where T : IEquatable<T>
    {
        if (!first.Equals(second))
        {
            throw Error(lineNumber, $"{name} disagrees between arguments and its top-level record.");
        }
    }

    private static JsonElement GetRequiredObject(
        JsonElement element,
        string name,
        long lineNumber)
    {
        JsonElement value = GetRequiredProperty(element, name, lineNumber);
        RequireKind(value, JsonValueKind.Object, name, lineNumber);
        return value;
    }

    private static string GetRequiredString(
        JsonElement element,
        string name,
        long lineNumber)
    {
        JsonElement value = GetRequiredProperty(element, name, lineNumber);
        RequireKind(value, JsonValueKind.String, name, lineNumber);
        return value.GetString()!;
    }

    private static int GetRequiredInt32(JsonElement element, string name, long lineNumber)
    {
        JsonElement value = GetRequiredProperty(element, name, lineNumber);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
        {
            throw Error(lineNumber, $"{name} must be a 32-bit integer.");
        }

        return result;
    }

    private static long GetRequiredInt64(JsonElement element, string name, long lineNumber)
    {
        JsonElement value = GetRequiredProperty(element, name, lineNumber);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
        {
            throw Error(lineNumber, $"{name} must be a 64-bit integer.");
        }

        return result;
    }

    private static ulong GetRequiredUInt64(JsonElement element, string name, long lineNumber)
    {
        JsonElement value = GetRequiredProperty(element, name, lineNumber);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out ulong result))
        {
            throw Error(lineNumber, $"{name} must be a nonnegative 64-bit integer.");
        }

        return result;
    }

    private static uint GetRequiredUInt32(JsonElement element, string name, long lineNumber)
    {
        JsonElement value = GetRequiredProperty(element, name, lineNumber);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out uint result))
        {
            throw Error(lineNumber, $"{name} must be a nonnegative 32-bit integer.");
        }

        return result;
    }

    private static ushort GetRequiredUInt16(JsonElement element, string name, long lineNumber)
    {
        uint result = GetRequiredUInt32(element, name, lineNumber);
        if (result > ushort.MaxValue)
        {
            throw Error(lineNumber, $"{name} must fit in an unsigned 16-bit integer.");
        }

        return (ushort)result;
    }

    private static byte GetRequiredByte(JsonElement element, string name, long lineNumber)
    {
        uint result = GetRequiredUInt32(element, name, lineNumber);
        if (result > byte.MaxValue)
        {
            throw Error(lineNumber, $"{name} must fit in an unsigned byte.");
        }

        return (byte)result;
    }

    private static JsonElement GetRequiredProperty(
        JsonElement element,
        string name,
        long lineNumber)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
        {
            throw Error(lineNumber, $"required field '{name}' is missing.");
        }

        return value;
    }

    private static void RequireKind(
        JsonElement element,
        JsonValueKind expected,
        string name,
        long lineNumber)
    {
        if (element.ValueKind != expected)
        {
            throw Error(
                lineNumber,
                $"{name} must be {expected.ToString().ToLowerInvariant()}, not {element.ValueKind.ToString().ToLowerInvariant()}.");
        }
    }

    private static CaptureValidationException Error(long lineNumber, string message) =>
        new(lineNumber, message);
}
