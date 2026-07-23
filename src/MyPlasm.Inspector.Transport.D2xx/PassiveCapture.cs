namespace MyPlasm.Inspector.Transport.D2xx;

public sealed record PassiveCaptureChunk(DateTimeOffset TimestampUtc, uint QueueDepth, uint RequestedCount, uint ReturnedCount, D2xxStatus Status, byte[] Bytes);

public sealed record PassiveCaptureResult(DateTimeOffset StartedUtc, DateTimeOffset StoppedUtc, IReadOnlyList<PassiveCaptureChunk> Chunks, IReadOnlyList<D2xxDiagnostic> Diagnostics)
{
    public int TotalBytes => Chunks.Sum(chunk => chunk.Bytes.Length);
}
