using System.Buffers.Binary;
using System.Text;

namespace CraftConsole.Core.Rcon;

/// <summary>
/// Packet types from the Source RCON protocol
/// (https://developer.valvesoftware.com/wiki/Source_RCON_Protocol). ExecCommand
/// and AuthResponse share the wire value 2 — direction (who sent it) is what
/// disambiguates them, not the type field alone.
/// </summary>
public enum RconPacketType
{
    /// <summary>Server → client: a command's reply, or an empty ack before AuthResponse.</summary>
    ResponseValue = 0,

    /// <summary>Client → server: execute a command.</summary>
    ExecCommand = 2,

    /// <summary>Server → client: authentication result. Id echoes the request on success, -1 on failure.</summary>
    AuthResponse = 2,

    /// <summary>Client → server: authenticate with the RCON password.</summary>
    Auth = 3,
}

/// <summary>
/// One Source RCON packet: a little-endian int32 length prefix (covering
/// everything after itself), a request id, a type, the body, and a second null
/// terminator after the body's own.
/// </summary>
public sealed record RconPacket(int Id, RconPacketType Type, string Body)
{
    // The protocol documents ~4096 bytes as the practical body limit per packet
    // (which is exactly why a longer reply arrives split across several). This
    // cap is deliberately looser than that — it exists to catch a desynced
    // stream reading garbage as an enormous length, not to police conformance.
    private const int MaxFrameSize = 8192;

    public byte[] Encode()
    {
        var bodyBytes = Encoding.UTF8.GetBytes(Body);
        var payloadLength = 4 + 4 + bodyBytes.Length + 1 + 1; // id + type + body + 2 null terminators
        var buffer = new byte[4 + payloadLength];

        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0, 4), payloadLength);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4, 4), Id);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8, 4), (int)Type);
        bodyBytes.CopyTo(buffer.AsSpan(12));
        // Final two bytes stay 0 — the array is zero-initialized already.

        return buffer;
    }

    /// <summary>Reads one packet from the stream, or null if the connection closed cleanly before any bytes arrived.</summary>
    public static async Task<RconPacket?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var lengthBuffer = new byte[4];
        if (!await ReadExactAsync(stream, lengthBuffer, allowCleanEof: true, ct))
            return null;

        var length = BinaryPrimitives.ReadInt32LittleEndian(lengthBuffer);
        if (length < 10) // id(4) + type(4) + at least the two null terminators
            throw new InvalidDataException($"RCON packet length {length} is smaller than the minimum frame.");
        if (length > MaxFrameSize)
            throw new InvalidDataException($"RCON packet length {length} exceeds the sanity limit of {MaxFrameSize}.");

        var payload = new byte[length];
        await ReadExactAsync(stream, payload, allowCleanEof: false, ct);

        var id = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
        var type = (RconPacketType)BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(4, 4));
        var bodyLength = length - 4 - 4 - 2; // exclude id, type, and the two trailing nulls
        var body = bodyLength > 0 ? Encoding.UTF8.GetString(payload, 8, bodyLength) : "";

        return new RconPacket(id, type, body);
    }

    /// <returns>false only if allowCleanEof and the stream ended before any byte was read.</returns>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, bool allowCleanEof, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0)
            {
                if (allowCleanEof && offset == 0) return false;
                throw new EndOfStreamException("Connection closed mid-packet.");
            }
            offset += read;
        }
        return true;
    }
}
