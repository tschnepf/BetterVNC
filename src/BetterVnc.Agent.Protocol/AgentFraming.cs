using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterVnc.Agent.Protocol;

/// <summary>A decoded frame: JSON header plus optional binary payload.</summary>
public sealed record AgentFrame(AgentMessage Header, ReadOnlyMemory<byte> Payload)
{
    public AgentFrame(AgentMessage header) : this(header, ReadOnlyMemory<byte>.Empty) { }
}

public sealed class AgentProtocolException : Exception
{
    public AgentProtocolException(string message) : base(message) { }
    public AgentProtocolException(string message, Exception inner) : base(message, inner) { }
}

public sealed class AgentAuthenticationException : Exception
{
    public AgentAuthenticationException(string message) : base(message) { }
}

/// <summary>Pairing is needed, was denied, or is disabled; <see cref="Code"/> is one of <see cref="AgentErrorCode"/>.</summary>
public sealed class AgentPairingException : Exception
{
    public AgentPairingException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
    public bool NeedsPairing => Code == AgentErrorCode.NotPaired;
}

/// <summary>
/// Wire format of one frame:
/// <code>
/// u32 BE headerLength | u32 BE payloadLength | header (UTF-8 JSON, headerLength bytes) | payload (payloadLength bytes)
/// </code>
/// Both lengths are bounded so a bad peer cannot make us allocate arbitrarily.
/// </summary>
public static class AgentFraming
{
    public const int MaxHeaderBytes = 4 * 1024 * 1024;
    public const long DefaultMaxPayloadBytes = 256L * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] EncodeHeader(AgentMessage header) => JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions);

    public static AgentMessage DecodeHeader(ReadOnlySpan<byte> json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentMessage>(json, JsonOptions) ?? throw new AgentProtocolException("Empty frame header.");
        }
        catch (JsonException ex)
        {
            throw new AgentProtocolException("Malformed frame header: " + ex.Message, ex);
        }
    }

    public static async ValueTask WriteAsync(Stream stream, AgentMessage header, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        var json = EncodeHeader(header);
        var prefix = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, (uint)json.Length);
        BinaryPrimitives.WriteUInt32BigEndian(prefix.AsSpan(4), (uint)payload.Length);
        await stream.WriteAsync(prefix, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static ValueTask WriteAsync(Stream stream, AgentFrame frame, CancellationToken ct) => WriteAsync(stream, frame.Header, frame.Payload, ct);

    /// <summary>Reads one frame; returns null at a clean end of stream before any byte of a frame.</summary>
    public static async ValueTask<AgentFrame?> ReadAsync(Stream stream, long maxPayloadBytes, CancellationToken ct)
    {
        var prefix = new byte[8];
        int got = await ReadAtMostAsync(stream, prefix, ct).ConfigureAwait(false);
        if (got == 0) return null;
        if (got < 8) throw new AgentProtocolException("Connection closed in the middle of a frame.");
        uint headerLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(prefix.AsSpan(4));
        if (headerLength == 0 || headerLength > MaxHeaderBytes)
            throw new AgentProtocolException($"Frame header length {headerLength} is out of range.");
        if (payloadLength > maxPayloadBytes)
            throw new AgentProtocolException($"Frame payload of {payloadLength} bytes exceeds the limit of {maxPayloadBytes} bytes.");

        var json = new byte[headerLength];
        await ReadExactlyAsync(stream, json, ct).ConfigureAwait(false);
        var payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
        if (payloadLength > 0) await ReadExactlyAsync(stream, payload, ct).ConfigureAwait(false);
        return new AgentFrame(DecodeHeader(json), payload);
    }

    private static async ValueTask<int> ReadAtMostAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0) throw new AgentProtocolException("Connection closed in the middle of a frame.");
            total += n;
        }
    }
}
