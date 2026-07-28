// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Net.Messaging;

/// <summary>
///     A float, sent in fewer bits than a float takes, by saying what it is a float <i>of</i>.
/// </summary>
/// <remarks>
///     <para>
///         A position in a level a kilometre across, to the nearest three centimetres, is sixteen
///         bits rather than thirty-two. A normalised intensity is eight. The saving is not the point
///         on its own — the point is that it is <b>declared</b>, at the field, so the precision a
///         packet costs is a number somebody chose rather than whatever <c>float</c> happens to be,
///         and <see cref="MaxError" /> says what was given up.
///     </para>
///     <para>
///         Encoding is a round to the nearest level, so the error is bounded by half a level and is
///         symmetric. Values outside the range are clamped rather than wrapped: a player who walks
///         through the wall of the declared range should stop at it in the packet, not appear at the
///         other end of the level.
///     </para>
/// </remarks>
/// <param name="Min">The smallest value that can be sent exactly.</param>
/// <param name="Max">The largest.</param>
/// <param name="Bits">How many bits to spend, from 1 to 32.</param>
public readonly record struct QuantizeRange(float Min, float Max, int Bits) {
    /// <summary>How many steps the range is divided into.</summary>
    public uint Levels => Bits >= 32 ? uint.MaxValue : (1u << Bits) - 1;

    /// <summary>The most a value can be changed by a round trip through this range.</summary>
    /// <remarks>
    ///     The quantization error, which is what a caller is choosing when it picks a width. The
    ///     decoded value is then rounded to the nearest <see cref="float" />, so the observed error
    ///     can exceed this by up to half a ULP of the result — at 640 in a ±1000 range that is
    ///     3 × 10⁻⁵ on top of a stated 1.5 × 10⁻², which is why the arithmetic below is done in
    ///     <see cref="double" />: it keeps that last ULP the only thing this number does not cover.
    /// </remarks>
    public float MaxError => (float)((Max - (double)Min) / (2.0 * Levels));

    /// <summary>Whether this range can be encoded with.</summary>
    public bool IsValid => Bits is >= 1 and <= 32 && Max > Min && float.IsFinite(Min) && float.IsFinite(Max);

    /// <summary>Turns a value into the bits that will be sent.</summary>
    /// <param name="value">The value. Clamped to the range; a NaN encodes as the bottom of it.</param>
    /// <returns>The encoded value, in <see cref="Bits" /> bits.</returns>
    public uint Encode(float value) {
        var normalized = (value - (double)Min) / (Max - (double)Min);

        // NaN fails both comparisons and falls through to zero, which is the bottom of the range.
        // Propagating it would put a NaN into a receiver's world where it would spread.
        if (normalized > 1.0) {
            return Levels;
        }

        if (!(normalized > 0.0)) {
            return 0;
        }

        return (uint)((normalized * Levels) + 0.5);
    }

    /// <summary>Turns received bits back into a value.</summary>
    /// <param name="encoded">The encoded value.</param>
    /// <returns>The value, within <see cref="MaxError" /> of the one that was encoded.</returns>
    public float Decode(uint encoded) {
        if (encoded > Levels) {
            encoded = Levels;
        }

        return (float)(Min + ((double)encoded / Levels * (Max - (double)Min)));
    }
}
