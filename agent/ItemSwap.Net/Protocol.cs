using System.Security.Cryptography;
using System.Text;

namespace ItemSwap.Net;

/// <summary>
/// Wire protocol for the ItemSwap connection client. Deliberately small and
/// written from scratch: the reference multiplayer mod this project takes
/// techniques (not code) from is GPLv3, and this project's feature surface
/// (connect + drop-item + claim-item, up to 10 players) is tiny enough that
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
    PositionUpdate = 0x0A,
}

public sealed record HelloMessage(byte ProtocolVersion, string Name, byte[] SecretHash);
public sealed record HelloAckMessage(byte ProtocolVersion, bool Ok, byte AssignedPlayerId);
public sealed record PlayerJoinedMessage(byte PlayerId, string Name);
public sealed record PlayerLeftMessage(byte PlayerId);

/// <summary>
/// X/Y/Z are the dropping player's real world position at the moment of the
/// drop, and ARE used for placement on the receiving side: KCD2's open
/// world is the same static, shared map for every save (proven out by the
/// Milestone 2 presence markers), so a given (x, y, z) is the same physical
/// location in everyone's game. See itemswap.lua's ItemSwap_OnPeerDrop,
/// which spawns the item there instead of near the receiving player.
/// </summary>
public sealed record ItemDropMessage(
    uint DropId, byte FromPlayerId, Guid ItemClass, ushort Amount, float Health,
    float X, float Y, float Z);

public sealed record ItemClaimMessage(uint DropId, byte FromPlayerId);
public sealed record ItemClaimResolvedMessage(uint DropId, byte WinnerPlayerId);

/// <summary>
/// Unlike ItemDropMessage's X/Y/Z, these coordinates ARE meant to be used
/// directly on the receiving side. KCD2's open world is the same static,
/// shared map for every save - only NPC/quest/inventory state differs
/// between players, not the terrain itself - so a given (x, y, z) is the
/// same physical location in everyone's game. That's what makes a presence
/// marker meaningful at all: it can legitimately be far from the receiving
/// player, same as a real friend would be if you could see them on the map.
///
/// IsCrouching drives Milestone 3's marker crouch behavior (a lowered
/// height and a paused idle animation) - see itemswap.lua's
/// ItemSwap_OnPeerPosition.
///
/// CurrentHp/MaxHp are Milestone 4's label HP line. No HP-change event
/// exists on player/player.actor in this build (confirmed live, and the
/// reference project's own more thorough search never found one either -
/// it polls too), so this rides the same poll as everything else here. 0/0
/// means "unknown" (e.g. before the sender's first successful read), not
/// "dead" - a receiver should render "HP: ?/?" rather than "HP: 0/0" for
/// that case.
///
/// InCombat/InDanger are Milestone 7's [Dueling]/[Danger] panel tags, from
/// player.soul:IsInCombatMode()/IsInCombatDanger() - both confirmed live to
/// be real, dynamic reads (verified against an actual sparring match).
/// InCombat tracks momentary active engagement (flips off the instant you
/// turn away from an opponent); InDanger stays true for the whole
/// encounter regardless of facing - confirmed live they're genuinely
/// different signals, not aliases of each other.
///
/// InTense is the [Caught] tag, from player.soul:IsInTenseCircumstance().
/// Discovered live by the user, not guessed: this is the "being actively
/// hunted and spotted" state - the game's own HUD shows a rabbit icon that
/// starts merely "searching" (this reads false) and becomes two rabbits
/// fighting the instant a pursuer actually sees the player for real (this
/// flips true).
///
/// InDialog is the [Talking] tag, from player.human:IsInDialog() - confirmed
/// live to correctly flip true/false around a real NPC conversation.
///
/// InRiding is the [Riding] tag, from player.human:IsMounted(). InPickpocketing
/// is the [Pickpocketing] tag, from player.human:IsPickpocketing(). Both
/// confirmed live to be real booleans. A third candidate, IsOnLadder() for a
/// [Climbing] tag, was tested and found to return a number (0), not a real
/// boolean, unlike every other flag here - deliberately skipped rather than
/// special-cased, per the user's call.
///
/// InUnconscious ([Unconscious], player.actor:IsUnconscious()), InDead
/// ([Dead], player.actor:IsDead()), InWanted ([Wanted],
/// player.soul:IsPublicEnemy()), InArmed ([Armed],
/// player.human:IsWeaponDrawn()), and InCarryingCorpse ([Carrying Body],
/// player.actor:IsCarryingCorpse()) round out the panel tags - all five
/// individually type-checked live (real booleans, no IsOnLadder-style
/// surprises) before being wired up.
/// </summary>
public sealed record PositionUpdateMessage(byte PlayerId, float X, float Y, float Z, bool IsCrouching, float CurrentHp, float MaxHp, bool InCombat, bool InDanger, bool InTense, bool InDialog, bool InRiding, bool InPickpocketing, bool InUnconscious, bool InDead, bool InWanted, bool InArmed, bool InCarryingCorpse);

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

    public static byte[] Encode(PositionUpdateMessage m)
    {
        var payload = new byte[33];
        var span = payload.AsSpan();
        span[0] = m.PlayerId;
        BitConverter.TryWriteBytes(span[1..5], m.X);
        BitConverter.TryWriteBytes(span[5..9], m.Y);
        BitConverter.TryWriteBytes(span[9..13], m.Z);
        span[13] = (byte)(m.IsCrouching ? 1 : 0);
        BitConverter.TryWriteBytes(span[14..18], m.CurrentHp);
        BitConverter.TryWriteBytes(span[18..22], m.MaxHp);
        span[22] = (byte)(m.InCombat ? 1 : 0);
        span[23] = (byte)(m.InDanger ? 1 : 0);
        span[24] = (byte)(m.InTense ? 1 : 0);
        span[25] = (byte)(m.InDialog ? 1 : 0);
        span[26] = (byte)(m.InRiding ? 1 : 0);
        span[27] = (byte)(m.InPickpocketing ? 1 : 0);
        span[28] = (byte)(m.InUnconscious ? 1 : 0);
        span[29] = (byte)(m.InDead ? 1 : 0);
        span[30] = (byte)(m.InWanted ? 1 : 0);
        span[31] = (byte)(m.InArmed ? 1 : 0);
        span[32] = (byte)(m.InCarryingCorpse ? 1 : 0);
        return EncodeFrame(MessageType.PositionUpdate, payload);
    }

    public static PositionUpdateMessage DecodePositionUpdate(byte[] payload)
    {
        var span = payload.AsSpan();
        return new PositionUpdateMessage(
            PlayerId: span[0],
            X: BitConverter.ToSingle(span[1..5]),
            Y: BitConverter.ToSingle(span[5..9]),
            Z: BitConverter.ToSingle(span[9..13]),
            IsCrouching: span[13] != 0,
            CurrentHp: BitConverter.ToSingle(span[14..18]),
            MaxHp: BitConverter.ToSingle(span[18..22]),
            InCombat: span[22] != 0,
            InDanger: span[23] != 0,
            InTense: span[24] != 0,
            InDialog: span[25] != 0,
            InRiding: span.Length > 26 && span[26] != 0,
            InPickpocketing: span.Length > 27 && span[27] != 0,
            InUnconscious: span.Length > 28 && span[28] != 0,
            InDead: span.Length > 29 && span[29] != 0,
            InWanted: span.Length > 30 && span[30] != 0,
            InArmed: span.Length > 31 && span[31] != 0,
            InCarryingCorpse: span.Length > 32 && span[32] != 0);
    }
}
