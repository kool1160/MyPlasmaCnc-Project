using MyPlasm.Inspector.Core.Safety;

namespace MyPlasm.Inspector.Transport.Fake;

public sealed record TransmissionRecord(
    DateTimeOffset TimestampUtc,
    string CommandName,
    CommandIntent Intent,
    byte[] Payload);
