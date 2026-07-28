// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Messaging;

/// <summary>One fixed-width field of an encoding, as the delta codec sees it.</summary>
/// <param name="Name">
///     What the field is called, for attribution. The codec never reads it; a bandwidth report does,
///     and "which field is costing me thirty kilobits" is not answerable without it.
/// </param>
/// <param name="Bits">How wide it is.</param>
/// <param name="Offset">
///     Whether the difference between two of these means anything. True for a quantized level or an
///     integer, where a value near the last one encodes in a handful of bits; false for a raw float's
///     bit pattern or a flag, where subtracting two of them is nonsense and the value is sent whole.
/// </param>
/// <remarks>
///     Deliberately not "the field's type". By the time a value reaches the delta codec it is already
///     bits, and the only two things worth knowing about it are how many there are and whether
///     arithmetic on them is meaningful. That is what lets one implementation serve every component
///     rather than one being generated per component.
/// </remarks>
public readonly record struct WireLane(string Name, int Bits, bool Offset);

/// <summary>Encodes one version of a value as its difference from the previous one.</summary>
/// <remarks>
///     <para>
///         <b>A pure transform between three bit streams, with no knowledge of any component type.</b>
///         The previous encoding and the current encoding go in and a delta comes out; the delta and
///         the previous encoding go in and the current one comes back. Nothing here touches a world,
///         an entity or a field — which is why there is one of these rather than one per component,
///         and why "delta then apply equals full state" is a property that can be tested over random
///         bits instead of over a hand-written example.
///     </para>
///     <para>
///         The layout is: <b>one bit per lane</b> saying whether it changed, then, for each lane that
///         did, either the new value whole or a difference. A difference gets a two-bit selector
///         choosing 4, 8 or 16 bits of zig-zagged offset, or the fourth code meaning "the whole
///         value". So a lane that moved a little costs six bits where it cost sixteen, a lane that
///         jumped costs two more than it did, and a lane that did not move costs one.
///     </para>
///     <para>
///         <b>Narrow lanes are never offset.</b> Below <see cref="MinimumOffsetBits" /> the selector
///         costs more than the encoding saves, so those are written whole — a flag that changed is
///         one bit of mask and one bit of value, and a byte is nine.
///     </para>
/// </remarks>
public static class DeltaCodec {
    /// <summary>How many bits choose between the offset widths.</summary>
    public const int SelectorBits = 2;

    /// <summary>The narrowest lane worth encoding as an offset rather than whole.</summary>
    /// <remarks>
    ///     Nine, because the widest offset that saves anything on an eight-bit lane is four bits, and
    ///     four bits plus a two-bit selector is six against eight — a saving so small that the lanes
    ///     which do not benefit would pay the two bits for nothing. Above this the arithmetic is
    ///     one-sided in the other direction.
    /// </remarks>
    public const int MinimumOffsetBits = 9;

    /// <summary>The code meaning "not an offset, the value itself".</summary>
    const uint WholeValue = 3;

    static ReadOnlySpan<int> OffsetWidths => [4, 8, 16];

    /// <summary>How many bits an encoding described by these lanes occupies.</summary>
    /// <param name="lanes">The layout.</param>
    /// <returns>The total.</returns>
    /// <remarks>
    ///     What the server checks a captured encoding against before trusting the layout. A replicator
    ///     whose lanes do not add up to what its own <c>Write</c> produced is one whose deltas would
    ///     be silently wrong, so it simply does not get any.
    /// </remarks>
    public static int TotalBits(ReadOnlySpan<WireLane> lanes) {
        var total = 0;

        foreach (var lane in lanes) {
            total += lane.Bits;
        }

        return total;
    }

    /// <summary>How many bits of offset a selector code stands for, on a lane of a given width.</summary>
    /// <param name="selector">The code, as it appeared on the wire.</param>
    /// <param name="bits">The lane's width.</param>
    /// <returns>The width of what follows the selector.</returns>
    /// <remarks>
    ///     For whoever has to walk a difference without decoding it — the packet inspector, which
    ///     wants to know what a record cost and cannot know what it means. Keeping it here rather than
    ///     duplicating the table there is the point: there is one answer to "how wide is that field".
    /// </remarks>
    public static int OffsetWidth(uint selector, int bits) =>
        selector >= WholeValue ? bits : OffsetWidths[(int)selector];

    /// <summary>The most a delta over these lanes could ever cost.</summary>
    /// <param name="lanes">The layout.</param>
    /// <returns>The worst case, in bits.</returns>
    public static int MaxBits(ReadOnlySpan<WireLane> lanes) {
        var total = 0;

        foreach (var lane in lanes) {
            total += 1 + SelectorBits + lane.Bits;
        }

        return total;
    }

    /// <summary>Writes the difference between two encodings of the same value.</summary>
    /// <param name="lanes">The layout both encodings follow.</param>
    /// <param name="previous">The encoding the far end already has.</param>
    /// <param name="current">The encoding it should end up with.</param>
    /// <param name="delta">Where the difference goes.</param>
    /// <param name="costs">
    ///     Filled in, lane by lane, with what each one cost in bits — including its own changed bit,
    ///     so the entries add up to the whole. Leave it empty to skip the accounting; that is the
    ///     usual call, and the only thing it costs is a length check per lane.
    /// </param>
    /// <returns>Whether both inputs held what the layout said they would.</returns>
    public static bool TryEncode(
        ReadOnlySpan<WireLane> lanes,
        ref BitReader previous,
        ref BitReader current,
        ref BitWriter delta,
        Span<int> costs = default
    ) {
        for (var i = 0; i < lanes.Length; i++) {
            var lane = lanes[i];
            var before = delta.BitsWritten;

            if (!previous.TryRead(lane.Bits, out var was) || !current.TryRead(lane.Bits, out var now)) {
                return false;
            }

            if (was == now) {
                delta.WriteBool(false);
            } else {
                delta.WriteBool(true);
                WriteChange(lane, was, now, ref delta);
            }

            if (i < costs.Length) {
                costs[i] = delta.BitsWritten - before;
            }
        }

        return !delta.Overflowed;
    }

    static void WriteChange(in WireLane lane, uint was, uint now, ref BitWriter delta) {
        if (!lane.Offset || lane.Bits < MinimumOffsetBits) {
            delta.Write(now, lane.Bits);

            return;
        }

        var zigzag = ZigZag((long)now - was);
        var selector = Selector(zigzag, lane.Bits);

        delta.Write(selector, SelectorBits);

        if (selector == WholeValue) {
            delta.Write(now, lane.Bits);
        } else {
            delta.Write((uint)zigzag, OffsetWidths[(int)selector]);
        }
    }

    /// <summary>Rebuilds an encoding from the one before it and a difference.</summary>
    /// <param name="lanes">The layout.</param>
    /// <param name="previous">The encoding this end already had.</param>
    /// <param name="delta">The difference, as it arrived.</param>
    /// <param name="current">Where the rebuilt encoding goes.</param>
    /// <returns>Whether the difference was well-formed against this layout.</returns>
    public static bool TryDecode(
        ReadOnlySpan<WireLane> lanes,
        ref BitReader previous,
        ref BitReader delta,
        ref BitWriter current
    ) {
        foreach (var lane in lanes) {
            if (!previous.TryRead(lane.Bits, out var was) || !delta.TryReadBool(out var changed)) {
                return false;
            }

            if (!changed) {
                current.Write(was, lane.Bits);

                continue;
            }

            if (!lane.Offset || lane.Bits < MinimumOffsetBits) {
                if (!delta.TryRead(lane.Bits, out var whole)) {
                    return false;
                }

                current.Write(whole, lane.Bits);

                continue;
            }

            if (!delta.TryRead(SelectorBits, out var selector)) {
                return false;
            }

            if (selector == WholeValue) {
                if (!delta.TryRead(lane.Bits, out var whole)) {
                    return false;
                }

                current.Write(whole, lane.Bits);

                continue;
            }

            if (!delta.TryRead(OffsetWidths[(int)selector], out var zigzag)) {
                return false;
            }

            // Masked rather than checked: the sender's arithmetic was done on values of this width,
            // so wrapping here reproduces it exactly. A difference that does not round-trip would be
            // a desync nobody could see, which is why the encoder never emits one it cannot undo.
            current.Write((uint)unchecked(was + UnZigZag(zigzag)) & Mask(lane.Bits), lane.Bits);
        }

        return !current.Overflowed;
    }

    static uint Selector(ulong zigzag, int bits) {
        for (var i = 0; i < OffsetWidths.Length; i++) {
            if (OffsetWidths[i] < bits && zigzag < 1ul << OffsetWidths[i]) {
                return (uint)i;
            }
        }

        return WholeValue;
    }

    static uint Mask(int bits) => bits >= 32 ? uint.MaxValue : (1u << bits) - 1;

    static ulong ZigZag(long value) => (ulong)((value << 1) ^ (value >> 63));

    static long UnZigZag(uint value) => (long)(value >> 1) ^ -(long)(value & 1);
}
