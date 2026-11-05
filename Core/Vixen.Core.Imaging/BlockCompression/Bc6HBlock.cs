// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.Intrinsics;

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>A 4×4 block of HDR colour in sixteen bytes, in any of BC6H's fourteen modes.</summary>
/// <remarks>
///     <para>
///         BC6H is the only compressed format that holds high dynamic range, which makes it the one
///         a prefiltered environment map ships in — six faces of half-float at 8 bytes a texel is
///         four times what the same cube costs here.
///     </para>
///     <para>
///         <b>Fourteen modes, and they differ in how an endpoint's bits are spent.</b> Ten divide the
///         block in two along one of thirty-two patterns; four do not. Twelve store one endpoint
///         outright and the others as signed deltas from it, so high precision is only available to a
///         block whose endpoints are close together. The bit layouts are in <see cref="Layouts" />,
///         written in the specification's notation so that they can be checked by reading them, and
///         both directions walk the same table.
///     </para>
///     <para>
///         <b>Endpoints are fitted in half-float bit space, not in linear light.</b> A half's bit
///         pattern is monotonic in its value and its steps are proportional rather than absolute,
///         which is the behaviour an HDR error metric wants: being ten units wrong at a radiance of
///         ten thousand does not matter, and being ten units wrong at a radiance of one does. It is
///         also where the format interpolates, so a least-squares solve there is exact.
///     </para>
///     <para>
///         <b>Infinity is not representable in the unsigned format, and that is the format being
///         careful rather than this encoder being careful.</b> The largest unquantised endpoint widens
///         to 0xFFFF and finishes as the half bit pattern 0x7BFF — 65 504, the largest <i>finite</i>
///         half. ⚠ <b>The signed format has exactly one way past it, and this file used to say it had
///         none.</b> Mode 14 stores sixteen-bit endpoints with no special cases, so the code −32768
///         finishes as magnitude 0x7C00: negative infinity. The GPU returns 0xFC00 for it. So the
///         encoder treats −32767 as the lowest code, in <see cref="Quantise" /> and in the walk alike.
///         An infinity or a NaN arriving in the source is clamped on the way in, where it is still
///         obvious what happened.
///     </para>
///     <para>
///         ⚠ <b>A delta can be too small for the endpoints that were wanted</b>, and clamping it
///         moves the endpoint after the indices were chosen. So the encoder orients each subset before
///         it quantises, and restricts each anchor to the lower half of the palette rather than
///         swapping endpoints afterwards: a swap would change which endpoint the deltas are measured
///         from. Whatever happens to the endpoints, the block it writes is valid.
///     </para>
///     <para>
///         <b>Checked against a GPU.</b> <c>GpuBcnDecoderTests</c> reads arbitrary blocks of every
///         mode, in both signednesses and with every partition forced, and this encoder's output,
///         through Metal's decoder.
///     </para>
/// </remarks>
static class Bc6HBlock {
    /// <summary>How many bytes one block is.</summary>
    public const int ByteLength = 16;

    /// <summary>How many texels one block covers.</summary>
    public const int Texels = 16;

    /// <summary>How many modes there are.</summary>
    public const int ModeCount = 14;

    /// <summary>How many partition patterns the two-subset modes have.</summary>
    public const int PartitionCount = 32;

    /// <summary>The largest finite half's bit pattern, which is the most any endpoint can finish as.</summary>
    public const int LargestHalf = 0x7BFF;

    /// <summary>One bit of a block: which field it belongs to and which of that field's bits it is.</summary>
    /// <param name="Field">Zero to eleven for the twelve endpoint channels, <see cref="PartitionField" /> otherwise.</param>
    /// <param name="Bit">Which bit of the field.</param>
    public readonly record struct FieldBit(byte Field, byte Bit);

    /// <summary>The field number of the partition in a <see cref="FieldBit" />.</summary>
    public const int PartitionField = 12;

    /// <summary>What one mode stores.</summary>
    /// <param name="ModeBits">The value of its mode field.</param>
    /// <param name="ModeBitCount">How wide the mode field is.</param>
    /// <param name="Subsets">One or two.</param>
    /// <param name="EndpointBits">How wide an endpoint is once decoded.</param>
    /// <param name="DeltaRed">How wide the stored red of the other endpoints is.</param>
    /// <param name="DeltaGreen">How wide the stored green of the other endpoints is.</param>
    /// <param name="DeltaBlue">How wide the stored blue of the other endpoints is.</param>
    /// <param name="Transformed">Whether the other endpoints are deltas from the first.</param>
    /// <param name="Layout">The specification's bit-by-bit description of the header.</param>
    public sealed record ModeInfo(
        int ModeBits,
        int ModeBitCount,
        int Subsets,
        int EndpointBits,
        int DeltaRed,
        int DeltaGreen,
        int DeltaBlue,
        bool Transformed,
        string Layout
    ) {
        /// <summary>The header, one entry per bit in stream order, after the mode field.</summary>
        public FieldBit[] Bits { get; } = Parse(Layout);

        /// <summary>The width of the stored form of a channel of the endpoints after the first.</summary>
        /// <param name="channel">Red, green or blue.</param>
        /// <returns>Its width.</returns>
        public int DeltaBits(int channel) => channel switch { 0 => DeltaRed, 1 => DeltaGreen, _ => DeltaBlue };
    }

    /// <summary>
    ///     The fourteen modes, in the specification's numbering less one, and each one's header as the
    ///     specification writes it.
    /// </summary>
    /// <remarks>
    ///     <c>r0</c> to <c>r3</c> are the four red endpoints and so on, <c>d</c> is the partition.
    ///     A run reads right to left, so <c>r0[9:0]</c> is bit 0 first — and <c>r0[10:15]</c> is bit
    ///     15 first, which reads like a typo in the specification and is not one.
    /// </remarks>
    public static IReadOnlyList<ModeInfo> Layouts => Modes;

    static readonly ModeInfo[] Modes = [
        new(0b00, 2, 2, 10, 5, 5, 5, true,
            "g2[4],b2[4],b3[4],r0[9:0],g0[9:0],b0[9:0],r1[4:0],g3[4],g2[3:0],g1[4:0],b3[0],g3[3:0],b1[4:0],b3[1],"
            + "b2[3:0],r2[4:0],b3[2],r3[4:0],b3[3],d[4:0]"),
        new(0b01, 2, 2, 7, 6, 6, 6, true,
            "g2[5],g3[4],g3[5],r0[6:0],b3[0],b3[1],b2[4],g0[6:0],b2[5],b3[2],g2[4],b0[6:0],b3[3],b3[5],b3[4],r1[5:0],"
            + "g2[3:0],g1[5:0],g3[3:0],b1[5:0],b2[3:0],r2[5:0],r3[5:0],d[4:0]"),
        new(0b00010, 5, 2, 11, 5, 4, 4, true,
            "r0[9:0],g0[9:0],b0[9:0],r1[4:0],r0[10],g2[3:0],g1[3:0],g0[10],b3[0],g3[3:0],b1[3:0],b0[10],b3[1],b2[3:0],"
            + "r2[4:0],b3[2],r3[4:0],b3[3],d[4:0]"),
        new(0b00110, 5, 2, 11, 4, 5, 4, true,
            "r0[9:0],g0[9:0],b0[9:0],r1[3:0],r0[10],g3[4],g2[3:0],g1[4:0],g0[10],g3[3:0],b1[3:0],b0[10],b3[1],b2[3:0],"
            + "r2[3:0],b3[0],b3[2],r3[3:0],g2[4],b3[3],d[4:0]"),
        new(0b01010, 5, 2, 11, 4, 4, 5, true,
            "r0[9:0],g0[9:0],b0[9:0],r1[3:0],r0[10],b2[4],g2[3:0],g1[3:0],g0[10],b3[0],g3[3:0],b1[4:0],b0[10],b2[3:0],"
            + "r2[3:0],b3[1],b3[2],r3[3:0],b3[4],b3[3],d[4:0]"),
        new(0b01110, 5, 2, 9, 5, 5, 5, true,
            "r0[8:0],b2[4],g0[8:0],g2[4],b0[8:0],b3[4],r1[4:0],g3[4],g2[3:0],g1[4:0],b3[0],g3[3:0],b1[4:0],b3[1],b2[3:0],"
            + "r2[4:0],b3[2],r3[4:0],b3[3],d[4:0]"),
        new(0b10010, 5, 2, 8, 6, 5, 5, true,
            "r0[7:0],g3[4],b2[4],g0[7:0],b3[2],g2[4],b0[7:0],b3[3],b3[4],r1[5:0],g2[3:0],g1[4:0],b3[0],g3[3:0],b1[4:0],"
            + "b3[1],b2[3:0],r2[5:0],r3[5:0],d[4:0]"),
        new(0b10110, 5, 2, 8, 5, 6, 5, true,
            "r0[7:0],b3[0],b2[4],g0[7:0],g2[5],g2[4],b0[7:0],g3[5],b3[4],r1[4:0],g3[4],g2[3:0],g1[5:0],g3[3:0],b1[4:0],"
            + "b3[1],b2[3:0],r2[4:0],b3[2],r3[4:0],b3[3],d[4:0]"),
        new(0b11010, 5, 2, 8, 5, 5, 6, true,
            "r0[7:0],b3[1],b2[4],g0[7:0],b2[5],g2[4],b0[7:0],b3[5],b3[4],r1[4:0],g3[4],g2[3:0],g1[4:0],b3[0],g3[3:0],"
            + "b1[5:0],b2[3:0],r2[4:0],b3[2],r3[4:0],b3[3],d[4:0]"),
        new(0b11110, 5, 2, 6, 6, 6, 6, false,
            "r0[5:0],g3[4],b3[0],b3[1],b2[4],g0[5:0],g2[5],b2[5],b3[2],g2[4],b0[5:0],g3[5],b3[3],b3[5],b3[4],r1[5:0],"
            + "g2[3:0],g1[5:0],g3[3:0],b1[5:0],b2[3:0],r2[5:0],r3[5:0],d[4:0]"),
        new(0b00011, 5, 1, 10, 10, 10, 10, false, "r0[9:0],g0[9:0],b0[9:0],r1[9:0],g1[9:0],b1[9:0]"),
        new(0b00111, 5, 1, 11, 9, 9, 9, true,
            "r0[9:0],g0[9:0],b0[9:0],r1[8:0],r0[10],g1[8:0],g0[10],b1[8:0],b0[10]"),
        new(0b01011, 5, 1, 12, 8, 8, 8, true,
            "r0[9:0],g0[9:0],b0[9:0],r1[7:0],r0[10:11],g1[7:0],g0[10:11],b1[7:0],b0[10:11]"),
        new(0b01111, 5, 1, 16, 4, 4, 4, true,
            "r0[9:0],g0[9:0],b0[9:0],r1[3:0],r0[10:15],g1[3:0],g0[10:15],b1[3:0],b0[10:15]")
    ];

    /// <summary>Which mode each five-bit mode field names, or −1 for the four reserved values.</summary>
    static readonly sbyte[] ModeOfField = BuildModeOfField();

    /// <summary>How the search spends its time, per level.</summary>
    /// <param name="Partitions">How many patterns get a real fit.</param>
    /// <param name="Refinements">Rounds of re-solving the winner's endpoints from its indices.</param>
    /// <param name="WalkRounds">Rounds of moving the winner's endpoints a code at a time.</param>
    /// <param name="AnyDelta">Whether a mode is tried even when its deltas plainly cannot reach.</param>
    /// <param name="EnoughError">Squared error under which partitions are not tried.</param>
    readonly record struct Tuning(int Partitions, int Refinements, int WalkRounds, bool AnyDelta, long EnoughError);

    static Tuning TuningOf(BlockQuality quality) =>
        quality switch {
            BlockQuality.Fast => new(1, 1, 0, false, 16 * 3 * 40 * 40),
            BlockQuality.Best => new(32, 4, 3, true, 0),
            _ => new(6, 2, 0, false, 16 * 3 * 4 * 4)
        };

    /// <summary>Reads a block.</summary>
    /// <param name="block">Its sixteen bytes.</param>
    /// <param name="rgb">Forty-eight half-float bit patterns to fill: sixteen texels of RGB, row-major.</param>
    /// <param name="signed">Whether this is the signed format.</param>
    /// <remarks>The four reserved mode values decode to zero, as the specification says and the GPU agrees.</remarks>
    public static void Decode(ReadOnlySpan<byte> block, Span<ushort> rgb, bool signed = false) {
        var reader = new BlockBitReader(block);
        var field = (int)reader.Read(2);

        if (field >= 2) {
            field |= (int)reader.Read(3) << 2;
        }

        var modeIndex = ModeOfField[field];

        if (modeIndex < 0) {
            rgb[..48].Clear();
            return;
        }

        var mode = Modes[modeIndex];
        Span<int> fields = stackalloc int[PartitionField + 1];
        fields.Clear();

        foreach (var bit in mode.Bits) {
            fields[bit.Field] |= (int)reader.Read(1) << bit.Bit;
        }

        var partition = fields[PartitionField];
        var endpointCount = mode.Subsets * 2;
        Span<int> endpoints = stackalloc int[4 * 3];

        for (var channel = 0; channel < 3; channel++) {
            var first = fields[FieldOf(0, channel)];

            if (signed) {
                first = SignExtend(first, mode.EndpointBits);
            }

            endpoints[channel] = first;

            for (var endpoint = 1; endpoint < endpointCount; endpoint++) {
                var stored = fields[FieldOf(endpoint, channel)];

                if (mode.Transformed) {
                    stored = (first + SignExtend(stored, mode.DeltaBits(channel))) & ((1 << mode.EndpointBits) - 1);
                }

                endpoints[(endpoint * 3) + channel] = signed ? SignExtend(stored, mode.EndpointBits) : stored;
            }
        }

        for (var at = 0; at < endpointCount * 3; at++) {
            endpoints[at] = Unquantise(endpoints[at], mode.EndpointBits, signed);
        }

        Span<byte> pattern = stackalloc byte[Texels];
        BptcTables.PatternOf(mode.Subsets, partition, pattern);

        var weights = mode.Subsets == 2 ? BptcTables.Weights3 : BptcTables.Weights4;
        var indexBits = mode.Subsets == 2 ? 3 : 4;

        for (var texel = 0; texel < Texels; texel++) {
            var subset = pattern[texel];
            var anchor = texel == BptcTables.AnchorOf(mode.Subsets, partition, subset);
            var weight = weights[(int)reader.Read(anchor ? indexBits - 1 : indexBits)];

            for (var channel = 0; channel < 3; channel++) {
                var low = endpoints[(subset * 6) + channel];
                var high = endpoints[(subset * 6) + 3 + channel];
                var interpolated = (((64 - weight) * low) + (weight * high) + 32) >> 6;

                rgb[(texel * 3) + channel] = PatternOf(Finish(interpolated, signed));
            }
        }
    }

    /// <summary>Writes a block.</summary>
    /// <param name="rgb">Forty-eight half-float bit patterns: sixteen texels of RGB, row-major.</param>
    /// <param name="block">Sixteen bytes to fill.</param>
    /// <param name="quality">How hard to search.</param>
    /// <param name="signed">Whether to write the signed format.</param>
    public static void Encode(
        ReadOnlySpan<ushort> rgb,
        Span<byte> block,
        BlockQuality quality = BlockQuality.Balanced,
        bool signed = false
    ) {
        var tuning = TuningOf(quality);
        Span<int> points = stackalloc int[Texels * 3];

        for (var at = 0; at < points.Length; at++) {
            points[at] = Clamped(rgb[at], signed);
        }

        Span<int> bestCodes = stackalloc int[4 * 3];
        Span<byte> bestIndices = stackalloc byte[Texels];
        Span<float> bestLine = stackalloc float[4 * 3];
        Span<int> codes = stackalloc int[4 * 3];
        Span<byte> indices = stackalloc byte[Texels];
        Span<float> line = stackalloc float[4 * 3];
        Span<byte> pattern = stackalloc byte[Texels];
        var search = new Winner { Error = long.MaxValue };

        // One subset first: it settles smooth content for almost nothing.
        pattern.Clear();
        SeedLines(points, pattern, 1, 0, line);

        for (var modeIndex = 10; modeIndex < ModeCount; modeIndex++) {
            Consider(points, pattern, signed, modeIndex, 0, line, tuning, ref search, codes, indices, bestCodes, bestIndices, bestLine);
        }

        if (search.Error > tuning.EnoughError) {
            Span<Vector128<float>> floats = stackalloc Vector128<float>[Texels];

            for (var texel = 0; texel < Texels; texel++) {
                floats[texel] = Vector128.Create(
                    (float)points[texel * 3],
                    points[(texel * 3) + 1],
                    points[(texel * 3) + 2],
                    0f
                );
            }

            Span<float> scores = stackalloc float[PartitionCount];
            Span<byte> kept = stackalloc byte[PartitionCount];

            BptcTables.ScorePartitions(floats, BptcTables.TwoSubsets[..(PartitionCount * 16)], 2, scores);
            BptcTables.KeepBest(scores, kept[..tuning.Partitions]);

            foreach (var partition in kept[..tuning.Partitions]) {
                BptcTables.PatternOf(2, partition, pattern);
                SeedLines(points, pattern, 2, partition, line);

                for (var modeIndex = 0; modeIndex < 10; modeIndex++) {
                    Consider(
                        points, pattern, signed, modeIndex, partition, line, tuning, ref search, codes, indices, bestCodes,
                        bestIndices, bestLine
                    );
                }
            }
        }

        Refine(points, signed, tuning, ref search, codes, indices, bestCodes, bestIndices, bestLine, line);
        Write(search, bestCodes, bestIndices, block);
    }

    /// <summary>The best block found so far.</summary>
    struct Winner {
        public int Mode;
        public int Partition;
        public long Error;
    }

    /// <summary>Scores one mode against a set of continuous endpoints, keeping it if it wins.</summary>
    static void Consider(
        ReadOnlySpan<int> points,
        ReadOnlySpan<byte> pattern,
        bool signed,
        int modeIndex,
        int partition,
        ReadOnlySpan<float> line,
        in Tuning tuning,
        ref Winner search,
        Span<int> codes,
        Span<byte> indices,
        Span<int> bestCodes,
        Span<byte> bestIndices,
        Span<float> bestLine
    ) {
        var mode = Modes[modeIndex];
        var mustTry = !mode.Transformed || tuning.AnyDelta;

        if (!QuantiseLines(line, mode, signed, codes) && !mustTry) {
            return;
        }

        var error = Score(points, pattern, mode, partition, signed, codes, indices, search.Error);

        if (error >= search.Error) {
            return;
        }

        search = new() { Mode = modeIndex, Partition = partition, Error = error };
        codes.CopyTo(bestCodes);
        indices.CopyTo(bestIndices);
        line.CopyTo(bestLine);
    }

    /// <summary>
    ///     Re-solves the winner's endpoints from its indices, then at the highest level walks them a
    ///     code at a time. The mode and the partition are settled by now.
    /// </summary>
    static void Refine(
        ReadOnlySpan<int> points,
        bool signed,
        in Tuning tuning,
        ref Winner search,
        Span<int> codes,
        Span<byte> indices,
        Span<int> bestCodes,
        Span<byte> bestIndices,
        Span<float> bestLine,
        Span<float> line
    ) {
        var mode = Modes[search.Mode];
        Span<byte> pattern = stackalloc byte[Texels];
        BptcTables.PatternOf(mode.Subsets, search.Partition, pattern);

        for (var round = 0; round < tuning.Refinements && search.Error > 0; round++) {
            bestLine.CopyTo(line);

            if (!Solve(points, pattern, mode.Subsets, bestIndices, line)) {
                break;
            }

            OrientLines(points, mode.Subsets, search.Partition, line);
            QuantiseLines(line, mode, signed, codes);

            var error = Score(points, pattern, mode, search.Partition, signed, codes, indices, search.Error);

            if (error >= search.Error) {
                break;
            }

            search.Error = error;
            codes.CopyTo(bestCodes);
            indices.CopyTo(bestIndices);
            line.CopyTo(bestLine);
        }

        for (var round = 0; round < tuning.WalkRounds && search.Error > 0; round++) {
            var improved = false;

            for (var at = 0; at < mode.Subsets * 6; at++) {
                for (var direction = -1; direction <= 1; direction += 2) {
                    bestCodes.CopyTo(codes);
                    codes[at] += direction;

                    if (!Representable(codes, mode, signed)) {
                        continue;
                    }

                    var error = Score(points, pattern, mode, search.Partition, signed, codes, indices, search.Error);

                    if (error < search.Error) {
                        search.Error = error;
                        bestCodes[at] = codes[at];
                        indices.CopyTo(bestIndices);
                        improved = true;
                    }
                }
            }

            if (!improved) {
                break;
            }
        }
    }

    /// <summary>Whether a set of decoded endpoints can actually be stored in a mode.</summary>
    static bool Representable(ReadOnlySpan<int> codes, ModeInfo mode, bool signed) {
        // Symmetric in the signed format: its lowest code is the only way to reach infinity.
        var highest = signed ? (1 << (mode.EndpointBits - 1)) - 1 : (1 << mode.EndpointBits) - 1;
        var lowest = signed ? -highest : 0;
        var endpoints = mode.Subsets * 2;

        for (var channel = 0; channel < 3; channel++) {
            var reach = 1 << (mode.DeltaBits(channel) - 1);

            for (var endpoint = 0; endpoint < endpoints; endpoint++) {
                var code = codes[(endpoint * 3) + channel];

                if (code < lowest || code > highest) {
                    return false;
                }

                if (mode.Transformed && endpoint > 0) {
                    var delta = code - codes[channel];

                    if (delta < -reach || delta >= reach) {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    /// <summary>
    ///     Rounds continuous endpoints to a mode's precision and makes its deltas fit.
    /// </summary>
    /// <returns>False when a delta needed more than one bit beyond what the mode has.</returns>
    static bool QuantiseLines(ReadOnlySpan<float> line, ModeInfo mode, bool signed, Span<int> codes) {
        var endpoints = mode.Subsets * 2;
        var sensible = true;

        for (var at = 0; at < endpoints * 3; at++) {
            codes[at] = Quantise(line[at], mode.EndpointBits, signed);
        }

        if (!mode.Transformed) {
            return true;
        }

        for (var channel = 0; channel < 3; channel++) {
            var reach = 1 << (mode.DeltaBits(channel) - 1);
            var first = codes[channel];

            for (var endpoint = 1; endpoint < endpoints; endpoint++) {
                var delta = codes[(endpoint * 3) + channel] - first;

                sensible &= delta >= -2 * reach && delta < 2 * reach;

                codes[(endpoint * 3) + channel] = first + Math.Clamp(delta, -reach, reach - 1);
            }
        }

        return sensible;
    }

    /// <summary>
    ///     Scores a set of decoded endpoints: builds each subset's palette and gives every texel its
    ///     nearest entry, holding each anchor to the lower half.
    /// </summary>
    /// <returns>The summed squared error, or <see cref="long.MaxValue" /> once past <paramref name="limit" />.</returns>
    static long Score(
        ReadOnlySpan<int> points,
        ReadOnlySpan<byte> pattern,
        ModeInfo mode,
        int partition,
        bool signed,
        ReadOnlySpan<int> codes,
        Span<byte> indices,
        long limit
    ) {
        Span<int> palette = stackalloc int[2 * 16 * 3];
        var weights = mode.Subsets == 2 ? BptcTables.Weights3 : BptcTables.Weights4;
        var entries = weights.Length;

        for (var subset = 0; subset < mode.Subsets; subset++) {
            for (var channel = 0; channel < 3; channel++) {
                var low = Unquantise(codes[(subset * 6) + channel], mode.EndpointBits, signed);
                var high = Unquantise(codes[(subset * 6) + 3 + channel], mode.EndpointBits, signed);

                for (var entry = 0; entry < entries; entry++) {
                    var weight = weights[entry];

                    palette[(((subset * 16) + entry) * 3) + channel] =
                        Finish((((64 - weight) * low) + (weight * high) + 32) >> 6, signed);
                }
            }
        }

        var total = 0L;

        for (var texel = 0; texel < Texels; texel++) {
            var subset = pattern[texel];
            var anchor = texel == BptcTables.AnchorOf(mode.Subsets, partition, subset);
            var limitEntries = anchor ? entries / 2 : entries;
            var entriesOfSubset = palette.Slice(subset * 16 * 3, limitEntries * 3);
            var (r, g, b) = (points[texel * 3], points[(texel * 3) + 1], points[(texel * 3) + 2]);
            var bestDistance = long.MaxValue;
            var bestEntry = 0;

            for (var entry = 0; entry < limitEntries; entry++) {
                long dr = r - entriesOfSubset[entry * 3];
                long dg = g - entriesOfSubset[(entry * 3) + 1];
                long db = b - entriesOfSubset[(entry * 3) + 2];
                var distance = (dr * dr) + (dg * dg) + (db * db);

                if (distance < bestDistance) {
                    bestDistance = distance;
                    bestEntry = entry;
                }
            }

            indices[texel] = (byte)bestEntry;
            total += bestDistance;

            if (total >= limit) {
                return long.MaxValue;
            }
        }

        return total;
    }

    /// <summary>Fits one line per subset and orders its ends so each anchor is nearer the first.</summary>
    static void SeedLines(ReadOnlySpan<int> points, ReadOnlySpan<byte> pattern, int subsets, int partition, Span<float> line) {
        Span<float> mean = stackalloc float[3];
        Span<float> covariance = stackalloc float[9];
        Span<float> centred = stackalloc float[3];
        Span<float> axis = stackalloc float[3];

        for (var subset = 0; subset < subsets; subset++) {
            mean.Clear();
            covariance.Clear();

            var count = 0;

            for (var texel = 0; texel < Texels; texel++) {
                if (pattern[texel] != subset) {
                    continue;
                }

                count++;

                for (var channel = 0; channel < 3; channel++) {
                    mean[channel] += points[(texel * 3) + channel];
                }
            }

            for (var channel = 0; channel < 3; channel++) {
                mean[channel] /= count;
            }

            for (var texel = 0; texel < Texels; texel++) {
                if (pattern[texel] != subset) {
                    continue;
                }

                for (var channel = 0; channel < 3; channel++) {
                    centred[channel] = points[(texel * 3) + channel] - mean[channel];
                }

                for (var row = 0; row < 3; row++) {
                    for (var column = 0; column < 3; column++) {
                        covariance[(row * 3) + column] += centred[row] * centred[column];
                    }
                }
            }

            PrincipalAxis.Find(covariance, 3, axis);

            var lowest = float.MaxValue;
            var highest = float.MinValue;

            for (var texel = 0; texel < Texels; texel++) {
                if (pattern[texel] != subset) {
                    continue;
                }

                var along = ((points[texel * 3] - mean[0]) * axis[0])
                    + ((points[(texel * 3) + 1] - mean[1]) * axis[1])
                    + ((points[(texel * 3) + 2] - mean[2]) * axis[2]);

                lowest = Math.Min(lowest, along);
                highest = Math.Max(highest, along);
            }

            for (var channel = 0; channel < 3; channel++) {
                line[(subset * 6) + channel] = mean[channel] + (axis[channel] * lowest);
                line[(subset * 6) + 3 + channel] = mean[channel] + (axis[channel] * highest);
            }
        }

        OrientLines(points, subsets, partition, line);
    }

    /// <summary>Swaps a subset's ends when its anchor texel is nearer the second one.</summary>
    static void OrientLines(ReadOnlySpan<int> points, int subsets, int partition, Span<float> line) {
        for (var subset = 0; subset < subsets; subset++) {
            var anchor = BptcTables.AnchorOf(subsets, partition, subset);
            var nearFirst = 0f;
            var nearSecond = 0f;

            for (var channel = 0; channel < 3; channel++) {
                var toFirst = points[(anchor * 3) + channel] - line[(subset * 6) + channel];
                var toSecond = points[(anchor * 3) + channel] - line[(subset * 6) + 3 + channel];

                nearFirst += toFirst * toFirst;
                nearSecond += toSecond * toSecond;
            }

            if (nearSecond < nearFirst) {
                for (var channel = 0; channel < 3; channel++) {
                    (line[(subset * 6) + channel], line[(subset * 6) + 3 + channel]) =
                        (line[(subset * 6) + 3 + channel], line[(subset * 6) + channel]);
                }
            }
        }
    }

    /// <summary>Re-solves each subset's endpoints from the indices by least squares.</summary>
    /// <returns>False when no subset had anything to solve.</returns>
    static bool Solve(ReadOnlySpan<int> points, ReadOnlySpan<byte> pattern, int subsets, ReadOnlySpan<byte> indices, Span<float> line) {
        var weights = subsets == 2 ? BptcTables.Weights3 : BptcTables.Weights4;
        Span<float> right0 = stackalloc float[3];
        Span<float> right1 = stackalloc float[3];
        var solved = false;

        for (var subset = 0; subset < subsets; subset++) {
            float a = 0f, b = 0f, c = 0f;
            right0.Clear();
            right1.Clear();

            for (var texel = 0; texel < Texels; texel++) {
                if (pattern[texel] != subset) {
                    continue;
                }

                var weight = weights[indices[texel]] / 64f;
                var inverse = 1f - weight;

                a += inverse * inverse;
                b += inverse * weight;
                c += weight * weight;

                for (var channel = 0; channel < 3; channel++) {
                    right0[channel] += inverse * points[(texel * 3) + channel];
                    right1[channel] += weight * points[(texel * 3) + channel];
                }
            }

            var determinant = (a * c) - (b * b);

            if (MathF.Abs(determinant) < 1e-4f) {
                continue;
            }

            solved = true;

            for (var channel = 0; channel < 3; channel++) {
                line[(subset * 6) + channel] = ((c * right0[channel]) - (b * right1[channel])) / determinant;
                line[(subset * 6) + 3 + channel] = ((a * right1[channel]) - (b * right0[channel])) / determinant;
            }
        }

        return solved;
    }

    /// <summary>
    ///     Rounds a value in half-float bit space to the nearest decoded endpoint of a precision.
    ///     Close to a closed form rather than one, because the unquantisation has special cases at
    ///     both ends of its range and the neighbours are cheap to check.
    /// </summary>
    /// <param name="value">The wanted bit pattern, negative for a negative half.</param>
    /// <param name="bits">The endpoint's precision.</param>
    /// <param name="signed">Whether this is the signed format.</param>
    /// <returns>The endpoint.</returns>
    public static int Quantise(float value, int bits, bool signed) {
        var limit = (float)LargestHalf;
        var wanted = (int)MathF.Round(Math.Clamp(value, signed ? -limit : 0f, limit));
        var magnitude = Math.Abs(wanted);

        int guess;
        int lowest;
        int highest;

        if (signed) {
            guess = (int)((magnitude * 32L / 31L) >> (16 - bits));
            highest = (1 << (bits - 1)) - 1;
            lowest = -highest;

            if (wanted < 0) {
                guess = -guess;
            }
        } else {
            guess = (int)((magnitude * 64L / 31L) >> (16 - bits));
            lowest = 0;
            highest = (1 << bits) - 1;
        }

        var best = Math.Clamp(guess, lowest, highest);
        var bestDistance = Math.Abs(Finish(Unquantise(best, bits, signed), signed) - wanted);

        for (var near = Math.Max(guess - 2, lowest); near <= Math.Min(guess + 2, highest); near++) {
            var distance = Math.Abs(Finish(Unquantise(near, bits, signed), signed) - wanted);

            if (distance < bestDistance) {
                bestDistance = distance;
                best = near;
            }
        }

        return best;
    }

    /// <summary>Widens a decoded endpoint to the sixteen-bit value the interpolation runs on.</summary>
    /// <param name="code">The endpoint, signed if the format is.</param>
    /// <param name="bits">The mode's endpoint precision.</param>
    /// <param name="signed">Whether this is the signed format.</param>
    /// <returns>The widened value.</returns>
    public static int Unquantise(int code, int bits, bool signed) {
        if (signed) {
            if (bits >= 16) {
                return code;
            }

            var negative = code < 0;
            var magnitude = negative ? -code : code;
            var widened = magnitude == 0
                ? 0
                : magnitude >= (1 << (bits - 1)) - 1
                    ? 0x7FFF
                    : ((magnitude << 15) + 0x4000) >> (bits - 1);

            return negative ? -widened : widened;
        }

        if (bits >= 15) {
            return code;
        }

        return code == 0
            ? 0
            : code == (1 << bits) - 1
                ? 0xFFFF
                : ((code << 15) + 0x4000) >> (bits - 1);
    }

    /// <summary>
    ///     Scales an interpolated value into half-float bit space. Negative stays negative here, so
    ///     that errors can be measured straight through zero.
    /// </summary>
    /// <param name="interpolated">The interpolated value.</param>
    /// <param name="signed">Whether this is the signed format.</param>
    /// <returns>The half's bits, as a signed magnitude.</returns>
    public static int Finish(int interpolated, bool signed) =>
        !signed
            ? (interpolated * 31) >> 6
            : interpolated < 0
                ? -((-interpolated * 31) >> 5)
                : (interpolated * 31) >> 5;

    /// <summary>A signed magnitude from <see cref="Finish" /> as the half's own bits.</summary>
    static ushort PatternOf(int finished) => finished < 0 ? (ushort)(0x8000 | -finished) : (ushort)finished;

    /// <summary>A half's bits as a signed magnitude this format can reach.</summary>
    static int Clamped(ushort pattern, bool signed) {
        var magnitude = Math.Min(pattern & 0x7FFF, LargestHalf);

        return (pattern & 0x8000) == 0 ? magnitude : signed ? -magnitude : 0;
    }

    static int SignExtend(int value, int bits) {
        var shift = 32 - bits;

        return (value << shift) >> shift;
    }

    /// <summary>Which field holds a channel of an endpoint: <c>r0 g0 b0 r1 g1 b1 r2 g2 b2 r3 g3 b3</c>.</summary>
    static int FieldOf(int endpoint, int channel) => (endpoint * 3) + channel;

    /// <summary>Lays out the fields and the indices.</summary>
    static void Write(in Winner found, ReadOnlySpan<int> codes, ReadOnlySpan<byte> indices, Span<byte> block) {
        var mode = Modes[found.Mode];
        Span<int> fields = stackalloc int[PartitionField + 1];

        fields[PartitionField] = found.Partition;

        for (var channel = 0; channel < 3; channel++) {
            fields[FieldOf(0, channel)] = codes[channel] & ((1 << mode.EndpointBits) - 1);

            for (var endpoint = 1; endpoint < mode.Subsets * 2; endpoint++) {
                var code = codes[(endpoint * 3) + channel];

                fields[FieldOf(endpoint, channel)] = mode.Transformed
                    ? (code - codes[channel]) & ((1 << mode.DeltaBits(channel)) - 1)
                    : code & ((1 << mode.EndpointBits) - 1);
            }
        }

        var writer = new BlockBitWriter(block);

        writer.Write((uint)mode.ModeBits, mode.ModeBitCount);

        foreach (var bit in mode.Bits) {
            writer.Write((uint)(fields[bit.Field] >> bit.Bit) & 1, 1);
        }

        Span<byte> pattern = stackalloc byte[Texels];
        BptcTables.PatternOf(mode.Subsets, found.Partition, pattern);

        var indexBits = mode.Subsets == 2 ? 3 : 4;

        for (var texel = 0; texel < Texels; texel++) {
            var anchor = texel == BptcTables.AnchorOf(mode.Subsets, found.Partition, pattern[texel]);

            writer.Write(indices[texel], anchor ? indexBits - 1 : indexBits);
        }
    }

    /// <summary>Reads the specification's notation into one entry per bit.</summary>
    static FieldBit[] Parse(string layout) {
        var bits = new List<FieldBit>(82);

        foreach (var run in layout.Split(',')) {
            var open = run.IndexOf('[');
            var name = run[..open];
            var range = run[(open + 1)..^1].Split(':');
            var start = int.Parse(range[0], CultureInfo.InvariantCulture);
            var end = range.Length == 2 ? int.Parse(range[1], CultureInfo.InvariantCulture) : start;

            var field = name == "d"
                ? PartitionField
                : FieldOf(name[1] - '0', name[0] switch { 'r' => 0, 'g' => 1, _ => 2 });

            // Right to left, whichever way round the range is written.
            for (var bit = end; bit != start + Math.Sign(start - end); bit += Math.Sign(start - end)) {
                bits.Add(new((byte)field, (byte)bit));
            }

            if (start == end) {
                bits.Add(new((byte)field, (byte)start));
            }
        }

        return [.. bits];
    }

    static sbyte[] BuildModeOfField() {
        var table = new sbyte[32];

        Array.Fill(table, (sbyte)-1);

        for (var mode = 0; mode < ModeCount; mode++) {
            table[Modes[mode].ModeBits] = (sbyte)mode;
        }

        return table;
    }
}
