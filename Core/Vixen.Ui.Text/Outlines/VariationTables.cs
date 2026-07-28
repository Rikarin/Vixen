// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Ui.Text.Outlines;

/// <summary>The two tables that describe a variable font's axes, as opposed to its deltas.</summary>
/// <remarks>
///     <c>fvar</c> says what the axes are and what range each covers; <c>avar</c> optionally warps the
///     normalised coordinate before anything reads a delta with it. Neither carries an outline, which
///     is why they are read here and applied in <see cref="GlyphVariations" />.
/// </remarks>
internal static class VariationTables {
    /// <summary>Reads a font's axes out of <c>fvar</c>.</summary>
    /// <param name="fvar">The table, or an empty array for a font that is not variable.</param>
    /// <returns>The axes in the font's own order, or empty.</returns>
    /// <remarks>
    ///     ⚠ <b>The order is the font's and it is load-bearing.</b> Every variation table indexes
    ///     axes positionally — a <c>gvar</c> tuple is a coordinate per axis with no tags in it — so
    ///     sorting these by tag, or de-duplicating them, silently reads one axis's deltas against
    ///     another's coordinate.
    /// </remarks>
    public static ImmutableArray<FontAxis> ReadAxes(byte[] fvar) {
        if (fvar.Length < 16) {
            return [];
        }

        var header = new SfntReader(fvar) { Position = 4 };
        var arrayOffset = header.U16();
        header.Position += 2;                              // reserved
        var count = header.U16();
        var size = header.U16();

        // A record is 20 bytes in every version of the table, but the size is stored rather than
        // assumed precisely so that a later revision can grow it — so honour it rather than stride
        // by 20 and read a future font's axes out of the middle of its own records.
        if (count == 0 || size < 20) {
            return [];
        }

        var axes = ImmutableArray.CreateBuilder<FontAxis>(count);

        for (var i = 0; i < count; i++) {
            var at = arrayOffset + (i * size);
            if (at + 20 > fvar.Length) {
                break;
            }

            var reader = new SfntReader(fvar) { Position = at };
            var tag = Tag(ref reader);
            var minimum = reader.Fixed();
            var initial = reader.Fixed();
            var maximum = reader.Fixed();

            // A font whose range is inside out is malformed, and the two ends are what everything
            // else divides by. Ordering them here means `Normalize` cannot produce a negative span.
            axes.Add(new FontAxis(tag, Math.Min(minimum, maximum), initial, Math.Max(minimum, maximum)));
        }

        return axes.ToImmutable();
    }

    /// <summary>Reads the per-axis segment maps out of <c>avar</c>.</summary>
    /// <param name="avar">The table, or an empty array.</param>
    /// <param name="axisCount">How many axes <c>fvar</c> declared.</param>
    /// <returns>One map per axis, or empty when the font has no <c>avar</c>.</returns>
    /// <remarks>
    ///     ⚠ <b>A table whose axis count disagrees with <c>fvar</c>'s is discarded whole.</b> Reading
    ///     the maps it does have and leaving the rest at identity would apply axis 0's warp to axis 0
    ///     and nothing to axis 1, which is a plausible-looking font that renders subtly wrong;
    ///     refusing the table renders the unwarped instance, which is wrong in a way a designer can
    ///     see and report.
    /// </remarks>
    public static ImmutableArray<AxisSegmentMap> ReadSegmentMaps(byte[] avar, int axisCount) {
        if (avar.Length < 8 || axisCount <= 0) {
            return [];
        }

        var reader = new SfntReader(avar) { Position = 6 };
        if (reader.U16() != axisCount) {
            return [];
        }

        var maps = ImmutableArray.CreateBuilder<AxisSegmentMap>(axisCount);

        for (var i = 0; i < axisCount; i++) {
            if (!reader.Has(2)) {
                return [];
            }

            var positions = reader.U16();
            if (!reader.Has(positions * 4)) {
                return [];
            }

            var pairs = ImmutableArray.CreateBuilder<(float From, float To)>(positions);

            for (var j = 0; j < positions; j++) {
                var from = reader.F2Dot14();
                var to = reader.F2Dot14();
                pairs.Add((from, to));
            }

            maps.Add(new AxisSegmentMap(pairs.MoveToImmutable()));
        }

        return maps.ToImmutable();
    }

    /// <summary>Four bytes as a tag, trailing spaces kept.</summary>
    /// <remarks>
    ///     Kept because that is what the font stores: a tag is four bytes, and a custom axis called
    ///     <c>M1</c> is <c>M1&#160;&#160;</c> on disk. <see cref="FontVariation.Create" /> is where
    ///     the padding stops mattering, because it is the only place a caller's tag meets a font's.
    /// </remarks>
    static string Tag(ref SfntReader reader) {
        Span<char> characters = stackalloc char[4];

        for (var i = 0; i < 4; i++) {
            characters[i] = (char)reader.U8();
        }

        return new string(characters);
    }
}
