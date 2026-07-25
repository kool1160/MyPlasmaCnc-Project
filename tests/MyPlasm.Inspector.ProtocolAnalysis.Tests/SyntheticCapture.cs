using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPlasm.Inspector.ProtocolAnalysis.Tests;

internal sealed class SyntheticCapture
{
    private static readonly DateTimeOffset Epoch =
        new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly List<JsonObject> records = [];
    private ulong sequence;

    public IReadOnlyList<JsonObject> Records => records;

    public JsonObject ListDevices(uint status = 0, uint count = 1) =>
        Add("FT_ListDevices", 0, status, new JsonObject
        {
            ["argument1"] = "0x00000000",
            ["argument2"] = "0x00000000",
            ["flags"] = 0x80000000U
        }, root => root["device_count"] = count);

    public JsonObject Open(ulong handleId, uint status = 0) =>
        Add("FT_OpenEx", handleId, status, new JsonObject
        {
            ["selector_pointer"] = "0x00001000",
            ["selector"] = "SYNTHETIC-DEVICE",
            ["flags"] = 2
        }, root => root["returned_handle"] = status == 0 ? "0x00002000" : "0x00000000");

    public JsonObject Close(ulong handleId, uint status = 0) =>
        Add("FT_Close", handleId, status, new JsonObject
        {
            ["handle"] = "0x00002000"
        });

    public JsonObject Write(
        ulong handleId,
        string hex,
        uint status = 0,
        uint? actualCount = null)
    {
        uint requested = checked((uint)hex.Length / 2);
        return Add("FT_Write", handleId, status, new JsonObject
        {
            ["buffer"] = "0x00003000"
        }, root =>
        {
            root["requested_count"] = requested;
            root["actual_count"] = actualCount ?? (status == 0 ? requested : 0);
            root["write_hex"] = hex;
        });
    }

    public JsonObject Read(
        ulong handleId,
        string hex,
        uint requestedCount,
        uint status = 0)
    {
        uint actual = checked((uint)hex.Length / 2);
        return Add("FT_Read", handleId, status, new JsonObject
        {
            ["buffer"] = "0x00004000"
        }, root =>
        {
            root["requested_count"] = requestedCount;
            root["actual_count"] = status == 0 ? actual : 0;
            root["read_hex"] = status == 0 ? hex : string.Empty;
        });
    }

    public JsonObject Queue(ulong handleId, uint count = 0, uint status = 0) =>
        Add("FT_GetQueueStatus", handleId, status, new JsonObject(), root =>
        {
            root["queue_count"] = count;
        });

    public JsonObject Baud(ulong handleId, uint value = 115200) =>
        Add("FT_SetBaudRate", handleId, 0, new JsonObject
        {
            ["baud_rate"] = value
        }, root => root["baud_rate"] = value);

    public JsonObject Data(ulong handleId, byte word = 8, byte stop = 0, byte parity = 0) =>
        Add("FT_SetDataCharacteristics", handleId, 0, new JsonObject
        {
            ["word_length"] = word,
            ["stop_bits"] = stop,
            ["parity"] = parity
        }, root => root["data_characteristics"] = new JsonObject
        {
            ["word_length"] = word,
            ["stop_bits"] = stop,
            ["parity"] = parity
        });

    public JsonObject Flow(ulong handleId, ushort mode = 0, byte xon = 17, byte xoff = 19) =>
        Add("FT_SetFlowControl", handleId, 0, new JsonObject
        {
            ["flow_control"] = mode,
            ["xon"] = xon,
            ["xoff"] = xoff
        }, root => root["flow_control"] = new JsonObject
        {
            ["mode"] = mode,
            ["xon"] = xon,
            ["xoff"] = xoff
        });

    public JsonObject Latency(ulong handleId, byte value = 2) =>
        Add("FT_SetLatencyTimer", handleId, 0, new JsonObject
        {
            ["latency_timer"] = value
        }, root => root["latency_timer"] = value);

    public JsonObject BitMode(ulong handleId, byte mask = 0, byte mode = 0) =>
        Add("FT_SetBitMode", handleId, 0, new JsonObject
        {
            ["mask"] = mask,
            ["mode"] = mode
        }, root => root["bit_mode"] = new JsonObject
        {
            ["mask"] = mask,
            ["mode"] = mode
        });

    public void SetSequence(JsonObject record, ulong value)
    {
        record["sequence"] = value;
    }

    public async Task<string> WriteAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using StreamWriter writer = new(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (JsonObject record in records)
        {
            await writer.WriteLineAsync(record.ToJsonString(JsonOptions));
        }

        return path;
    }

    public string WriteLargeQueueCapture(string path, int queueRecords)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using StreamWriter writer = new(
            path,
            append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 128 * 1024);
        ulong largeSequence = 1;
        writer.WriteLine(CreateLargeRecord(
            largeSequence,
            "FT_OpenEx",
            77,
            "{\"selector_pointer\":\"0x00001000\",\"selector\":\"SYNTHETIC-DEVICE\",\"flags\":2}",
            ",\"returned_handle\":\"0x00002000\""));
        for (int index = 0; index < queueRecords; index++)
        {
            largeSequence++;
            writer.WriteLine(CreateLargeRecord(
                largeSequence,
                "FT_GetQueueStatus",
                77,
                "{}",
                $",\"queue_count\":{index % 3}"));
        }

        largeSequence++;
        writer.WriteLine(CreateLargeRecord(
            largeSequence,
            "FT_Close",
            77,
            "{\"handle\":\"0x00002000\"}",
            string.Empty,
            "close"));
        return path;
    }

    private static string CreateLargeRecord(
        ulong recordSequence,
        string function,
        ulong handleId,
        string arguments,
        string extraFields,
        string flushTrigger = "none")
    {
        string timestamp = Epoch.AddMilliseconds((long)recordSequence).ToString("O");
        long elapsed = checked((long)recordSequence * 1_000);
        return
            $"{{\"schema_version\":1,\"session_id\":\"11111111-2222-3333-4444-555555555555\",\"utc_timestamp\":\"{timestamp}\",\"elapsed_us\":{elapsed},\"process_id\":1234,\"thread_id\":5678,\"function\":\"{function}\",\"sequence\":{recordSequence},\"handle_id\":{handleId},\"status\":0,\"arguments\":{arguments}{extraFields},\"flush_trigger\":\"{flushTrigger}\"}}";
    }

    private JsonObject Add(
        string function,
        ulong handleId,
        uint status,
        JsonObject arguments,
        Action<JsonObject>? addFields = null)
    {
        sequence++;
        JsonObject root = new()
        {
            ["schema_version"] = 1,
            ["session_id"] = "11111111-2222-3333-4444-555555555555",
            ["utc_timestamp"] = Epoch.AddMilliseconds((long)sequence).ToString("O"),
            ["elapsed_us"] = checked((long)sequence * 1_000),
            ["process_id"] = 1234,
            ["thread_id"] = 5678,
            ["function"] = function,
            ["sequence"] = sequence,
            ["handle_id"] = handleId,
            ["status"] = status,
            ["arguments"] = arguments,
            ["flush_trigger"] = function == "FT_Close" ? "close" : "none"
        };
        addFields?.Invoke(root);
        records.Add(root);
        return root;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };
}
