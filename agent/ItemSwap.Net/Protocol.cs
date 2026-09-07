using System.Security.Cryptography;
using System.Text;

namespace ItemSwap.Net;

/// <summary>
/// Wire protocol for the ItemSwap connection client. Deliberately small and
/// written from scratch: the reference multiplayer mod this project takes
/// techniques (not code) from is GPLv3, and this project's feature surface
/// (connect + drop-item + claim-item, up to 3 players) is tiny enough that
/// reimplementing a similarly-shaped TLV framing is cheap and keeps this
/// project's own license independent.
///
/// Framing: [type:1 byte][len:2 bytes LE][payload:len bytes], one TCP
/// connection per joiner, always to the host - joiners never connect to
/// each other (see the plan's "star, host-relayed" topology).
/// </summary>
public enum MessageType : byte
{
    Hello = 0x01,
    HelloAck = 0x02,
    PlayerJoined = 0x03,
    PlayerLeft = 0x04,
    ItemDrop = 0x05,
    ItemClaim = 0x06,
    ItemClaimResolved = 0x07,
    Heartbeat = 0x08,
    Disconnect = 0x09,
}

public sealed record HelloMessage(byte ProtocolVersion, string Name, byte[] SecretHash);
public sealed record HelloAckMessage(byte ProtocolVersion, bool Ok, byte AssignedPlayerId);
public sealed record PlayerJoinedMessage(byte PlayerId, string Name);
public sealed record PlayerLeftMessage(byte PlayerId);

/// <summary>
/// X/Y/Z are carried for completeness (and possible future use, e.g. a
/// Milestone 2 presence overlay) but are NOT meant to be used for item
/// placement on the receiving side - every player is on an independent
/// single-player save, so the sender's world coordinates describe a
/// position the receiver's game has never loaded. See itemswap.lua's
/// ItemSwap_OnPeerDrop, which ignores them for exactly this reason.
/// </summary>
public sealed record ItemDropMessage(
    uint DropId, byte FromPlayerId, Guid ItemClass, ushort Amount, float Health,
    float X, float Y, float Z);

public sealed record ItemClaimMessage(uint DropId, byte FromPlayerId);
public sealed record ItemClaimResolvedMessage(uint DropId, byte WinnerPlayerId);

public static class Protocol
{
    public const byte ProtocolVersion = 1;
    public const int SecretHashLength = 32; // SHA-256
    public const int MaxNameLength = 255;

    public static byte[] HashSecret(string sharedSecret) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(sharedSecret));

    // ---- Frame-level helpers ----

    public static byte[] EncodeFrame(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(payload), "Payload too large for a 16-bit length prefix.");

        var frame = new byte[3 + payload.Length];
        frame[0] = (byte)type;
        BitConverter.TryWriteBytes(frame.AsSpan(1, 2), (ushort)payload.Length);
        payload.CopyTo(frame.AsSpan(3));
        return frame;
    }

    /// <summary>Reads exactly one frame from the stream. Returns null on a clean EOF before any header byte.</summary>
    public static async Task<(MessageType Type, byte[] Payload)?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[3];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        var type = (MessageType)header[0];
        var len = BitConverter.ToUInt16(header, 1);
        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Connection closed mid-frame.");

        return (type, payload);
    }

    /// <summary>Fills <paramref name="buffer"/> completely, or returns false if the stream hit EOF before any byte was read.</summary>
    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (offset == 0) return false;
                throw new EndOfStreamException("Connection closed mid-frame.");
            }
            offset += n;
        }
        return true;
    }

    // ---- Message encode/decode ----

    public static byte[] Encode(HelloMessage m)
    {
        var nameBytes = Encoding.UTF8.GetBytes(m.Name);
        if (nameBytes.Length > MaxNameLength) throw new ArgumentException("Name too long.", nameof(m));
        if (m.SecretHash.Length != SecretHashLength) throw new ArgumentException("Bad secret hash length.", nameof(m));

        var payload = new byte[1 + 1 + nameBytes.Length + SecretHashLength];
        payload[0] = m.ProtocolVersion;
        payload[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(payload, 2);
        m.SecretHash.CopyTo(payload, 2 + nameBytes.Length);
        return EncodeFrame(MessageType.Hello, payload);
    }

    public static HelloMessage DecodeHello(byte[] payload)
    {
        var protoVer = payload[0];
        var nameLen = payload[1];
        var name = Encoding.UTF8.GetString(payload, 2, nameLen);
        var hash = payload[(2 + nameLen)..(2 + nameLen + SecretHashLength)];
        return new HelloMessage(protoVer, name, hash);
    }

    public static byte[] Encode(HelloAckMessage m) =>
        EncodeFrame(MessageType.HelloAck, [m.ProtocolVersion, (byte)(m.Ok ? 1 : 0), m.AssignedPlayerId]);

    public static HelloAckMessage DecodeHelloAck(byte[] payload) =>
        new(payload[0], payload[1] != 0, payload[2]);

    public static byte[] Encode(PlayerJoinedMessage m)
    {
        var nameBytes = Encoding.UTF8.GetBytes(m.Name);
        var payload = new byte[1 + 1 + nameBytes.Length];
        payload[0] = m.PlayerId;
        payload[1] = (byte)nameBytes.Length;
        nameBytes.CopyTo(payload, 2);
        return EncodeFrame(MessageType.PlayerJoined, payload);
    }

    public static PlayerJoinedMessage DecodePlayerJoined(byte[] payload)
    {
        var nameLen = payload[1];
        return new PlayerJoinedMessage(payload[0], Encoding.UTF8.GetString(payload, 2, nameLen));
    }

    public static byte[] Encode(PlayerLeftMessage m) =>
        EncodeFrame(MessageType.PlayerLeft, [m.PlayerId]);

    public static PlayerLeftMessage DecodePlayerLeft(byte[] payload) => new(payload[0]);

    public static byte[] Encode(ItemDropMessage m)
    {
        var payload = new byte[4 + 1 + 16 + 2 + 4 + 4 + 4 + 4];
        var span = payload.AsSpan();
        BitConverter.TryWriteBytes(span[0..4], m.DropId);
        span[4] = m.FromPlayerId;
        m.ItemClass.ToByteArray().CopyTo(span[5..21]);
        BitConverter.TryWriteBytes(span[21..23], m.Amount);
        BitConverter.TryWriteBytes(span[23..27], m.Health);
        BitConverter.TryWriteBytes(span[27..31], m.X);
        BitConverter.TryWriteBytes(span[31..35], m.Y);
        BitConverter.TryWriteBytes(span[35..39], m.Z);
        return EncodeFrame(MessageType.ItemDrop, payload);
    }

    public static ItemDropMessage DecodeItemDrop(byte[] payload)
    {
        var span = payload.AsSpan();
        return new ItemDropMessage(
            DropId: BitConverter.ToUInt32(span[0..4]),
            FromPlayerId: span[4],
            ItemClass: new Guid(span[5..21]),
            Amount: BitConverter.ToUInt16(span[21..23]),
            Health: BitConverter.ToSingle(span[23..27]),
            X: BitConverter.ToSingle(span[27..31]),
            Y: BitConverter.ToSingle(span[31..35]),
            Z: BitConverter.ToSingle(span[35..39]));
    }

    public static byte[] Encode(ItemClaimMessage m)
    {
        var payload = new byte[5];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), m.DropId);
        payload[4] = m.FromPlayerId;
        return EncodeFrame(MessageType.ItemClaim, payload);
    }

    public static ItemClaimMessage DecodeItemClaim(byte[] payload) =>
        new(BitConverter.ToUInt32(payload.AsSpan(0, 4)), payload[4]);

    public static byte[] Encode(ItemClaimResolvedMessage m)
    {
        var payload = new byte[5];
        BitConverter.TryWriteBytes(payload.AsSpan(0, 4), m.DropId);
        payload[4] = m.WinnerPlayerId;
        return EncodeFrame(MessageType.ItemClaimResolved, payload);
    }

    public static ItemClaimResolvedMessage DecodeItemClaimResolved(byte[] payload) =>
        new(BitConverter.ToUInt32(payload.AsSpan(0, 4)), payload[4]);

    public static byte[] EncodeHeartbeat() => EncodeFrame(MessageType.Heartbeat, ReadOnlySpan<byte>.Empty);

    public static byte[] EncodeDisconnect(byte reason) => EncodeFrame(MessageType.Disconnect, [reason]);
}
