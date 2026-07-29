using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;

namespace Arbiter;

public sealed class DeepPacketInspection
{
    // thanks to https://my-thos.github.io/rakwrite/, we have to do this
    // wow.
    public sealed class Options
    {
        public int MaxUDPPayload { get; init; } = 8192;
        public int MagicSearchLimit { get; init; } = 256;

        // split-packet guardrails
        public int MaxSplitFragments { get; init; } = 128;
        public int MaxTrackedSplitPacketsPerClient { get; init; } = 64;
        public int MaxFragmentPayloadBytes { get; init; } = 1024 * 1024;
        public int MaxTotalFragmentBytes { get; init; } = 4 * 1024 * 1024;

        // how long to remember split-packet state
        public int SplitStateTtlSeconds { get; init; } = 30;

        public int MaxAckRangeWidth { get; init; } = 1000;
        public int MaxAckRanges { get; init; } = 4096;
    }

    private readonly Options _options;

    private sealed class ClientState
    {
        public bool Trusted;
        public int SuspicionScore;
        public long LastSeenTicks;

        public readonly Dictionary<uint, SplitState> SplitPackets = new();
        public readonly object Gate = new();
    }

    private sealed class SplitState
    {
        public int DeclaredCount;
        public int HighestIndexSeen;
        public int SeenFragments;
        public int TotalPayloadBytes;
        public long FirstSeenTicks;
        public long LastSeenTicks;
        public readonly HashSet<int> SeenIndices = new();
    }

    private readonly ConcurrentDictionary<IPEndPoint, ClientState> _states = new();

    private static readonly byte[] RakNetMagic = { 0x00, 0xFF, 0xFF, 0x00, 0xFE };

    private static long NowTicks() => Stopwatch.GetTimestamp();
    private static long TicksPerSecond() => Stopwatch.Frequency;

    public DeepPacketInspection(Options? options = null)
    {
        _options = options ?? new Options();
    }

    public bool AllowClientToServer(IPEndPoint client, ReadOnlySpan<byte> datagram, out string reason)
    {
        reason = string.Empty;

        if (datagram.Length == 0)
        {
            reason = "empty datagram";
            return false;
        }

        if (datagram.Length > _options.MaxUDPPayload)
        {
            reason = "datagram too large";
            return false;
        }

        var state = _states.GetOrAdd(client, _ => new ClientState());

        lock (state.Gate)
        {
            state.LastSeenTicks = NowTicks();
            PruneLocked(state);

            // only trust traffic that looks like a handshake
            if (!state.Trusted)
            {
                if (!ContainsMagic(datagram, _options.MagicSearchLimit, out _))
                {
                    reason = "no RakNet magic before trust";
                    state.SuspicionScore++;
                    return false;
                }
                else
                {
                    state.Trusted = true;
                }
            }

            // a few cheap sanity checks
            if (!BasicRakNetSanity(datagram, out reason))
            {
                state.SuspicionScore++;
                return false;
            }

            if (!ValidateControlPacket(state, datagram, out reason))
            {
                state.SuspicionScore++;
                return false;
            }

            return true;
        }
    }

    public bool AllowServerToClient(IPEndPoint client, ReadOnlySpan<byte> datagram, out string reason)
    {
        reason = string.Empty;

        if (datagram.Length == 0)
        {
            reason = "empty datagram";
            return false;
        }

        if (datagram.Length > _options.MaxUDPPayload)
        {
            reason = "datagram too large";
            return false;
        }

        var state = _states.GetOrAdd(client, _ => new ClientState());

        lock (state.Gate)
        {
            state.LastSeenTicks = NowTicks();
            PruneLocked(state);

            // keep only cheap sanity checks here
            if (!BasicRakNetSanity(datagram, out reason))
            {
                state.SuspicionScore++;
                return false;
            }

            if (!ValidateControlPacket(state, datagram, out reason))
            {
                state.SuspicionScore++;
                return false;
            }

            return true;
        }
    }

    private bool BasicRakNetSanity(ReadOnlySpan<byte> datagram, out string reason)
    {
        reason = string.Empty;

        // not a full parser
        if (datagram.Length < 1)
        {
            reason = "too short";
            return false;
        }

        // handshake usually carries the magic
        // later packets aren't, so do not require it everywhere
        if (datagram.Length < 5 && !ContainsMagic(datagram, _options.MagicSearchLimit, out _))
        {
            reason = "tiny packet without RakNet magic";
            return false;
        }

        return true;
    }

    private bool ValidateControlPacket(ClientState state, ReadOnlySpan<byte> datagram, out string reason)
    {
        reason = string.Empty;

        // ACK/NAK is a separate control path
        if (!TryValidateAckNack(datagram, out reason))
            return false;

        // split metadata is only checked when the packet shape matches
        if (TryClassifySplitPacket(datagram, out var split))
        {
            if (!ValidateSplitPacketHeuristics(state, split, out reason))
                return false;
        }

        return true;
    }

    private bool TryValidateAckNack(ReadOnlySpan<byte> datagram, out string reason)
    {
        reason = string.Empty;

        if (datagram.Length < 1)
        {
            reason = "empty datagram";
            return false;
        }

        byte id = datagram[0];

        // frame set, not an ACK/NACK packet
        if (id >= 0x80 && id <= 0x8F)
            return true;

        // only validate actual ACK/NACK packets
        if (id != 0xA0 && id != 0xC0)
            return true;

        int offset = 1;

        if (!Helper.TryReadUInt16BE(datagram, ref offset, out ushort rangeCount))
        {
            reason = "malformed ACK header";
            return false;
        }

        if (rangeCount == 0 || rangeCount > _options.MaxAckRanges)
        {
            reason = "too many ACK ranges";
            return false;
        }

        for (int i = 0; i < rangeCount; i++)
        {
            if (!Helper.TryReadByte(datagram, ref offset, out byte single))
            {
                reason = "ACK range type missing";
                return false;
            }

            if (single != 0)
            {
                if (!Helper.TryReadUInt24LE(datagram, ref offset, out uint index))
                {
                    reason = "ACK index missing";
                    return false;
                }

                // RakNet uses uint24 sequence numbers
                // the maximum value is not a valid ACK index
                if (index == 0xFFFFFF)
                {
                    reason = "invalid ACK index";
                    return false;
                }

                continue;
            }

            if (!Helper.TryReadUInt24LE(datagram, ref offset, out uint min))
            {
                reason = "ACK min missing";
                return false;
            }

            if (!Helper.TryReadUInt24LE(datagram, ref offset, out uint max))
            {
                reason = "ACK max missing";
                return false;
            }

            if (min > max)
            {
                reason = "ACK range reversed";
                return false;
            }

            if (max == 0xFFFFFF)
            {
                reason = "invalid ACK max";
                return false;
            }

            if (max - min > (uint)_options.MaxAckRangeWidth)
            {
                reason = "ACK range too wide";
                return false;
            }
        }

        return true;
    }

    private static bool TryClassifySplitPacket(ReadOnlySpan<byte> datagram, out SplitDescriptor descriptor)
    {
        descriptor = default!;

        // frame-set packets sit in the 0x80..0x8f range
        if (datagram.Length < 4)
            return false;

        byte packetId = datagram[0];
        if (packetId < 0x80 || packetId > 0x8F)
            return false;

        int offset = 1;

        // frame-set index: uint24le
        if (!Helper.TryReadUInt24LE(datagram, ref offset, out _))
            return false;

        while (offset < datagram.Length)
        {
            // frame flags: top 3 bits reliability, split bit on the side
            if (!Helper.TryReadByte(datagram, ref offset, out byte flags))
                return false;

            // length in bits
            if (!Helper.TryReadUInt16LE(datagram, ref offset, out ushort lengthBits))
                return false;

            int reliability = (flags >> 5) & 0x07;
            bool isSplit = (flags & 0x10) != 0;

            bool hasReliableNumber = reliability is 2 or 3 or 4 or 6 or 7;
            bool hasSequencingNumber = reliability is 1 or 4;
            bool hasOrderingNumber = reliability is 1 or 3 or 4 or 7;

            // reliable frames carry a reliable index
            if (hasReliableNumber && !Helper.TryReadUInt24LE(datagram, ref offset, out _))
                return false;

            // sequenced frames carry a sequencing index
            if (hasSequencingNumber && !Helper.TryReadUInt24LE(datagram, ref offset, out _))
                return false;

            // ordered / sequenced frames carry ordering index + channel
            if (hasOrderingNumber)
            {
                if (!Helper.TryReadUInt24LE(datagram, ref offset, out _))
                    return false;

                if (!Helper.TryReadByte(datagram, ref offset, out _))
                    return false;
            }

            int bodyBytes = (lengthBits + 7) >> 3;
            if (bodyBytes < 0 || offset + bodyBytes > datagram.Length)
                return false;

            if (isSplit)
            {
                // split count (4)
                // split id (2)
                // split index (4)
                if (offset + 10 > datagram.Length)
                    return false;

                if (!Helper.TryReadUInt32LE(datagram, ref offset, out uint splitCount))
                    return false;

                if (!Helper.TryReadUInt16LE(datagram, ref offset, out ushort splitId))
                    return false;

                if (!Helper.TryReadUInt32LE(datagram, ref offset, out uint splitIndex))
                    return false;

                // split counts are bounded
                if (splitCount == 0 || splitCount > 4096)
                    return false;

                if (splitIndex >= splitCount)
                    return false;

                descriptor = new SplitDescriptor(
                    Count: (int)splitCount,
                    Index: (int)splitIndex,
                    SplitId: splitId,
                    FragmentPayloadBytes: bodyBytes
                );

                return true;
            }

            // skip the frame body and continue scanning later frames
            offset += bodyBytes;
        }

        return false;
    }

    private bool ValidateSplitPacketHeuristics(ClientState state, SplitDescriptor desc, out string reason)
    {
        reason = string.Empty;

        if (desc.Count <= 0 || desc.Count > _options.MaxSplitFragments)
        {
            reason = $"invalid split count ({desc.Count})";
            return false;
        }

        if (desc.Index < 0 || desc.Index >= desc.Count)
        {
            reason = $"invalid split index ({desc.Index}/{desc.Count})";
            return false;
        }

        if (desc.FragmentPayloadBytes <= 0)
        {
            reason = "empty split fragment";
            return false;
        }

        if (desc.FragmentPayloadBytes > _options.MaxFragmentPayloadBytes)
        {
            reason = "fragment payload too large";
            return false;
        }

        long impliedTotal = (long)desc.Count * desc.FragmentPayloadBytes;
        if (impliedTotal > _options.MaxTotalFragmentBytes)
        {
            reason = "split packet would exceed total byte budget";
            return false;
        }

        if (desc.Index * (long)desc.FragmentPayloadBytes > _options.MaxTotalFragmentBytes)
        {
            reason = "split packet offset too large";
            return false;
        }

        if (!state.SplitPackets.TryGetValue(desc.SplitId, out var split))
        {
            if (state.SplitPackets.Count >= _options.MaxTrackedSplitPacketsPerClient)
            {
                reason = "too many tracked split packets";
                return false;
            }

            split = new SplitState
            {
                DeclaredCount = desc.Count,
                HighestIndexSeen = desc.Index,
                SeenFragments = 1,
                TotalPayloadBytes = desc.FragmentPayloadBytes,
                FirstSeenTicks = NowTicks(),
                LastSeenTicks = NowTicks()
            };

            split.SeenIndices.Add(desc.Index);
            state.SplitPackets[desc.SplitId] = split;
            return true;
        }

        split.LastSeenTicks = NowTicks();

        if (split.DeclaredCount != desc.Count)
        {
            reason = "split count changed mid-stream";
            return false;
        }

        if (split.SeenIndices.Contains(desc.Index))
        {
            reason = "duplicate split fragment";
            return false;
        }

        split.SeenIndices.Add(desc.Index);
        split.SeenFragments++;
        split.TotalPayloadBytes += desc.FragmentPayloadBytes;
        split.HighestIndexSeen = Math.Max(split.HighestIndexSeen, desc.Index);

        if (split.TotalPayloadBytes > _options.MaxTotalFragmentBytes)
        {
            reason = "cumulative split payload too large";
            return false;
        }

        if (split.HighestIndexSeen >= split.DeclaredCount)
        {
            reason = "split index exceeded declared count";
            return false;
        }

        if (split.SeenFragments > split.DeclaredCount)
        {
            reason = "too many split fragments";
            return false;
        }

        return true;
    }

    private void PruneLocked(ClientState state)
    {
        var now = NowTicks();
        var ttlTicks = (long)_options.SplitStateTtlSeconds * TicksPerSecond();

        var expired = new List<uint>();

        foreach (var kvp in state.SplitPackets)
        {
            if (now - kvp.Value.LastSeenTicks > ttlTicks)
                expired.Add(kvp.Key);
        }

        foreach (var id in expired)
            state.SplitPackets.Remove(id);
    }

    private static bool ContainsMagic(ReadOnlySpan<byte> datagram, int searchLimit, out int magicIndex)
    {
        magicIndex = -1;

        var maxSearch = Math.Min(datagram.Length - RakNetMagic.Length, searchLimit);
        if (maxSearch < 0)
            return false;

        for (int i = 0; i <= maxSearch; i++)
        {
            bool match = true;

            for (int j = 0; j < RakNetMagic.Length; j++)
            {
                if (datagram[i + j] != RakNetMagic[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                magicIndex = i;
                return true;
            }
        }

        return false;
    }

    private sealed record SplitDescriptor(int Count, int Index, uint SplitId, int FragmentPayloadBytes);
}