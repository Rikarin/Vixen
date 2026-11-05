// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Numerics;
using System.Runtime.Intrinsics;

namespace Vixen.Core.Imaging.BlockCompression;

/// <summary>A 4×4 RGBA block in sixteen bytes, in any of BC7's eight modes.</summary>
/// <remarks>
///     <para>
///         BC7's modes trade subsets against precision. Modes 0 to 3 and 7 divide the block along
///         one of sixty-four partition patterns, which is what lets it hold an edge between two
///         materials without smearing them together; modes 4 and 5 give colour and alpha separate
///         lines, and can swap alpha with one colour channel so that an unrelated channel gets the
///         separate line instead; mode 6 is one line through RGBA at the highest precision. No mode
///         wins everywhere, so the encoder's whole job is choosing.
///     </para>
///     <para>
///         <b>How the search goes.</b> Mode 6 first, because it settles smooth content for almost
///         nothing. If it fell short, every partition is scored by the residual around one line per
///         subset (<see cref="BptcTables.ScorePartitions" />), and the likeliest few are fitted for
///         real: a principal axis, then rounds of assigning indices against the <i>quantised</i>
///         palette and re-solving the endpoints by least squares. Modes 4 and 5 are tried in each
///         rotation. At <see cref="BlockQuality.Best" /> the winner's endpoints are then walked a
///         code at a time, because the rounding of a least-squares answer is rarely the best code.
///     </para>
///     <para>
///         <b>Integer error, on purpose.</b> Candidates are compared by summed squared error over
///         integer texels and integer palettes, so the choice of mode is exact arithmetic with
///         nothing for a platform's float behaviour to disturb. Nothing here allocates; every buffer
///         is on the stack, because this runs once per block of a texture.
///     </para>
///     <para>
///         <b>Checked against a GPU.</b> <c>GpuBcnDecoderTests</c> reads arbitrary blocks forced to
///         every mode and partition, and this encoder's output, through Metal's decoder. That is the
///         proof of the tables and the bit layout, not the tests in this assembly.
///     </para>
/// </remarks>
static class Bc7Block {
    /// <summary>How many bytes one block is.</summary>
    public const int ByteLength = 16;

    /// <summary>How many texels one block covers.</summary>
    public const int Texels = 16;

    /// <summary>How many modes there are.</summary>
    public const int ModeCount = 8;

    /// <summary>What one mode stores.</summary>
    /// <param name="Subsets">How many lines the block is divided into.</param>
    /// <param name="PartitionBits">How many bits name the pattern.</param>
    /// <param name="RotationBits">How many bits name the channel swapped with alpha.</param>
    /// <param name="SelectionBits">How many bits choose which index set is colour's.</param>
    /// <param name="ColourBits">Stored bits per colour channel, without parity.</param>
    /// <param name="AlphaBits">Stored bits for alpha, without parity. Zero means alpha is opaque.</param>
    /// <param name="EndpointParity">Whether each endpoint has its own parity bit.</param>
    /// <param name="SharedParity">Whether each subset's two endpoints share one parity bit.</param>
    /// <param name="IndexBits">Bits per index in the first index set.</param>
    /// <param name="SecondIndexBits">Bits per index in the second set. Zero when there is one.</param>
    public readonly record struct ModeLayout(
        int Subsets,
        int PartitionBits,
        int RotationBits,
        int SelectionBits,
        int ColourBits,
        int AlphaBits,
        bool EndpointParity,
        bool SharedParity,
        int IndexBits,
        int SecondIndexBits
    );

    /// <summary>Every mode's layout.</summary>
    public static ReadOnlySpan<ModeLayout> Modes => ModeTable;

    static readonly ModeLayout[] ModeTable = [
        new(3, 4, 0, 0, 4, 0, true, false, 3, 0),
        new(2, 6, 0, 0, 6, 0, false, true, 3, 0),
        new(3, 6, 0, 0, 5, 0, false, false, 2, 0),
        new(2, 6, 0, 0, 7, 0, true, false, 2, 0),
        new(1, 0, 2, 1, 5, 6, false, false, 2, 3),
        new(1, 0, 2, 0, 7, 8, false, false, 2, 2),
        new(1, 0, 0, 0, 7, 7, true, false, 4, 0),
        new(2, 6, 0, 0, 5, 5, true, false, 2, 0)
    ];

    /// <summary>
    ///     For a colour channel value, two seven-bit endpoints that interpolate exactly to it at the
    ///     first index of mode 5. A block of one colour needs no search: mode 5's alpha is eight bits
    ///     stored outright, and this makes its colour exact too.
    /// </summary>
    static readonly (byte Low, byte High)[] SolidColour = BuildSolidColour();

    /// <summary>The best code for each byte, by precision and parity: <see cref="NearestIndex" />.</summary>
    static readonly byte[] Nearest = BuildNearest();

    /// <summary>What a code of a given precision expands to: <c>(precision &lt;&lt; 8) + code</c>.</summary>
    static readonly byte[] Expansion = BuildExpansion();

    /// <summary>Every texel of a block, for a line that covers the whole of it.</summary>
    static ReadOnlySpan<byte> Everyone => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15];

    /// <summary>The partitions to try, the refinement rounds and when to stop, per level.</summary>
    /// <param name="TwoSubsets">How many two-subset patterns get a real fit.</param>
    /// <param name="ThreeSubsets">How many three-subset patterns get a real fit.</param>
    /// <param name="Refinements">Rounds of re-solving endpoints from indices.</param>
    /// <param name="AllParities">Whether every parity assignment is tried rather than the nearest.</param>
    /// <param name="AllRotations">Whether modes 4 and 5 try each rotation, on opaque blocks too.</param>
    /// <param name="EnoughError">Squared error under which the search stops.</param>
    /// <param name="WalkRounds">Rounds of moving the winner's endpoints a code at a time.</param>
    readonly record struct Tuning(
        int TwoSubsets,
        int ThreeSubsets,
        int Refinements,
        bool AllParities,
        bool AllRotations,
        long EnoughError,
        int WalkRounds
    );

    static Tuning TuningOf(BlockQuality quality) =>
        quality switch {
            BlockQuality.Fast => new(1, 0, 1, false, false, 64, 0),
            BlockQuality.Best => new(16, 12, 4, true, true, 0, 3),
            _ => new(4, 3, 2, true, true, 12, 0)
        };

    /// <summary>Reads a block in any mode.</summary>
    /// <param name="block">Its sixteen bytes.</param>
    /// <param name="rgba">Sixty-four bytes to fill: sixteen texels of RGBA, row-major.</param>
    /// <remarks>
    ///     A first byte of zero names no mode at all, and the answer here is zero in every channel.
    ///     ⚠ Apple's decoder returns opaque black for it instead, so the two disagree on alpha for a
    ///     block no encoder writes.
    /// </remarks>
    public static void Decode(ReadOnlySpan<byte> block, Span<byte> rgba) {
        if (block[0] == 0) {
            rgba[..64].Clear();
            return;
        }

        var mode = BitOperations.TrailingZeroCount((uint)block[0]);
        var layout = ModeTable[mode];
        var reader = new BlockBitReader(block);

        reader.Read(mode + 1);

        var partition = (int)reader.Read(layout.PartitionBits);
        var rotation = (int)reader.Read(layout.RotationBits);
        var selection = reader.Read(layout.SelectionBits) != 0;

        var endpoints = layout.Subsets * 2;
        Span<int> codes = stackalloc int[3 * 2 * 4];

        for (var channel = 0; channel < 4; channel++) {
            var bits = channel == 3 ? layout.AlphaBits : layout.ColourBits;

            for (var endpoint = 0; endpoint < endpoints; endpoint++) {
                codes[(endpoint * 4) + channel] = (int)reader.Read(bits);
            }
        }

        Span<int> parities = stackalloc int[3 * 2];
        parities.Clear();

        if (layout.EndpointParity) {
            for (var endpoint = 0; endpoint < endpoints; endpoint++) {
                parities[endpoint] = (int)reader.Read(1);
            }
        } else if (layout.SharedParity) {
            for (var subset = 0; subset < layout.Subsets; subset++) {
                parities[subset * 2] = parities[(subset * 2) + 1] = (int)reader.Read(1);
            }
        }

        var hasParity = layout.EndpointParity || layout.SharedParity;
        Span<byte> expanded = stackalloc byte[3 * 2 * 4];

        for (var endpoint = 0; endpoint < endpoints; endpoint++) {
            for (var channel = 0; channel < 4; channel++) {
                var bits = channel == 3 ? layout.AlphaBits : layout.ColourBits;

                if (bits == 0) {
                    expanded[(endpoint * 4) + channel] = 255;
                    continue;
                }

                var code = codes[(endpoint * 4) + channel];

                if (hasParity) {
                    code = (code << 1) | parities[endpoint];
                    bits++;
                }

                expanded[(endpoint * 4) + channel] = Expand(code, bits);
            }
        }

        Span<byte> pattern = stackalloc byte[Texels];
        BptcTables.PatternOf(layout.Subsets, partition, pattern);

        Span<byte> first = stackalloc byte[Texels];
        Span<byte> second = stackalloc byte[Texels];

        ReadIndices(ref reader, layout.IndexBits, layout, partition, pattern, first);

        if (layout.SecondIndexBits != 0) {
            ReadIndices(ref reader, layout.SecondIndexBits, layout, partition, pattern, second);
        } else {
            first.CopyTo(second);
        }

        // Which index set is colour's: mode 4 can spend its three-bit indices on either.
        var colourIndices = selection ? second : first;
        var alphaIndices = selection ? first : second;
        var colourWeights = BptcTables.WeightsOf(selection ? layout.SecondIndexBits : layout.IndexBits);
        var alphaWeights = layout.SecondIndexBits == 0
            ? colourWeights
            : BptcTables.WeightsOf(selection ? layout.IndexBits : layout.SecondIndexBits);

        for (var texel = 0; texel < Texels; texel++) {
            var low = expanded.Slice(pattern[texel] * 8, 4);
            var high = expanded.Slice((pattern[texel] * 8) + 4, 4);
            var colourWeight = colourWeights[colourIndices[texel]];
            var alphaWeight = alphaWeights[alphaIndices[texel]];

            var r = Interpolate(low[0], high[0], colourWeight);
            var g = Interpolate(low[1], high[1], colourWeight);
            var b = Interpolate(low[2], high[2], colourWeight);
            var a = Interpolate(low[3], high[3], alphaWeight);

            switch (rotation) {
                case 1:
                    (r, a) = (a, r);
                    break;

                case 2:
                    (g, a) = (a, g);
                    break;

                case 3:
                    (b, a) = (a, b);
                    break;
            }

            rgba[texel * 4] = (byte)r;
            rgba[(texel * 4) + 1] = (byte)g;
            rgba[(texel * 4) + 2] = (byte)b;
            rgba[(texel * 4) + 3] = (byte)a;
        }
    }

    /// <summary>Writes a block.</summary>
    /// <param name="rgba">Sixty-four bytes: sixteen texels of RGBA, row-major.</param>
    /// <param name="block">Sixteen bytes to fill.</param>
    /// <param name="quality">How hard to search.</param>
    public static void Encode(ReadOnlySpan<byte> rgba, Span<byte> block, BlockQuality quality = BlockQuality.Balanced) {
        var tuning = TuningOf(quality);
        Span<Vector128<int>> points = stackalloc Vector128<int>[Texels];
        var opaque = true;
        var solid = true;

        for (var texel = 0; texel < Texels; texel++) {
            var at = texel * 4;

            points[texel] = Vector128.Create((int)rgba[at], rgba[at + 1], rgba[at + 2], rgba[at + 3]);
            opaque &= rgba[at + 3] == 255;
            solid &= rgba[at] == rgba[0]
                && rgba[at + 1] == rgba[1]
                && rgba[at + 2] == rgba[2]
                && rgba[at + 3] == rgba[3];
        }

        if (solid) {
            WriteSolid(rgba, block);
            return;
        }

        var search = new Candidate();
        Span<byte> codes = stackalloc byte[3 * 2 * 4];
        Span<byte> colourIndices = stackalloc byte[Texels];
        Span<byte> alphaIndices = stackalloc byte[Texels];
        Span<byte> workCodes = stackalloc byte[3 * 2 * 4];
        Span<byte> workColour = stackalloc byte[Texels];
        Span<byte> workAlpha = stackalloc byte[Texels];

        // Mode 6: one line, and for smooth content usually the end of the search.
        Span<Vector128<float>> seeds = stackalloc Vector128<float>[2];
        SeedLine(points, Everyone, seeds);
        search.Error = FitLine(points, Everyone, seeds, LineOf(6), tuning, codes, colourIndices, long.MaxValue);
        search.Mode = 6;

        if (search.Error <= tuning.EnoughError) {
            Finish(points, tuning, ref search, codes, colourIndices, alphaIndices, block);
            return;
        }

        TrySeparateAlpha(points, tuning, opaque, ref search, codes, colourIndices, alphaIndices, workCodes, workColour, workAlpha);

        if (search.Error <= tuning.EnoughError) {
            Finish(points, tuning, ref search, codes, colourIndices, alphaIndices, block);
            return;
        }

        Span<Vector128<float>> floats = stackalloc Vector128<float>[Texels];

        for (var texel = 0; texel < Texels; texel++) {
            floats[texel] = Vector128.ConvertToSingle(points[texel]);
        }

        Span<float> scores = stackalloc float[64];
        Span<byte> kept = stackalloc byte[16];

        BptcTables.ScorePartitions(floats, BptcTables.TwoSubsets, 2, scores);
        BptcTables.KeepBest(scores, kept[..tuning.TwoSubsets]);

        var modes = opaque ? (ReadOnlySpan<byte>)[1, 3] : [7, 1, 3];

        foreach (var partition in kept[..tuning.TwoSubsets]) {
            TryPartitioned(points, modes, partition, 2, tuning, ref search, codes, colourIndices, workCodes, workColour);

            if (search.Error <= tuning.EnoughError) {
                break;
            }
        }

        if (tuning.ThreeSubsets > 0 && search.Error > tuning.EnoughError) {
            var three = kept[..tuning.ThreeSubsets];

            BptcTables.ScorePartitions(floats, BptcTables.ThreeSubsets, 3, scores);
            BptcTables.KeepBest(scores, three);

            foreach (var partition in three) {
                TryPartitioned(points, [2], partition, 3, tuning, ref search, codes, colourIndices, workCodes, workColour);
            }

            // Mode 0 has more precision than mode 2 and only the first sixteen patterns.
            BptcTables.KeepBest(scores[..16], three);

            foreach (var partition in three) {
                TryPartitioned(points, [0], partition, 3, tuning, ref search, codes, colourIndices, workCodes, workColour);
            }
        }

        Finish(points, tuning, ref search, codes, colourIndices, alphaIndices, block);
    }

    /// <summary>The mode, its options and the error of the best thing found so far.</summary>
    struct Candidate {
        public int Mode;
        public int Partition;
        public int Rotation;
        public bool Selection;
        public long Error;
    }

    /// <summary>What one fitted line's endpoints can hold.</summary>
    /// <param name="colourBits">Bits per colour channel including parity. Zero fixes them at zero.</param>
    /// <param name="alphaBits">Bits for alpha including parity. Zero fixes it at 255.</param>
    /// <param name="parity">Which parity rule the codes obey.</param>
    /// <param name="weights">The index's table.</param>
    readonly ref struct Line(int colourBits, int alphaBits, Parity parity, ReadOnlySpan<byte> weights) {
        public readonly int ColourBits = colourBits;
        public readonly int AlphaBits = alphaBits;
        public readonly Parity Parity = parity;
        public readonly ReadOnlySpan<byte> Weights = weights;
    }

    /// <summary>How a mode's low bits are tied together.</summary>
    enum Parity {
        None,
        PerEndpoint,
        Shared
    }

    /// <summary>The line of a mode whose single index set covers every channel it stores.</summary>
    static Line LineOf(int mode) {
        var layout = ModeTable[mode];
        var extra = layout.EndpointParity || layout.SharedParity ? 1 : 0;

        return new(
            layout.ColourBits + extra,
            layout.AlphaBits == 0 ? 0 : layout.AlphaBits + extra,
            layout.EndpointParity ? Parity.PerEndpoint : layout.SharedParity ? Parity.Shared : Parity.None,
            BptcTables.WeightsOf(layout.IndexBits)
        );
    }

    /// <summary>Fits every subset of a pattern in each of the given modes, keeping any improvement.</summary>
    static void TryPartitioned(
        ReadOnlySpan<Vector128<int>> points,
        ReadOnlySpan<byte> modes,
        int partition,
        int subsets,
        in Tuning tuning,
        ref Candidate search,
        Span<byte> codes,
        Span<byte> indices,
        Span<byte> workCodes,
        Span<byte> workIndices
    ) {
        Span<byte> pattern = stackalloc byte[Texels];
        BptcTables.PatternOf(subsets, partition, pattern);

        Span<byte> members = stackalloc byte[Texels];
        Span<int> starts = stackalloc int[4];
        var filled = 0;

        for (var subset = 0; subset < subsets; subset++) {
            starts[subset] = filled;

            for (var texel = 0; texel < Texels; texel++) {
                if (pattern[texel] == subset) {
                    members[filled++] = (byte)texel;
                }
            }
        }

        starts[subsets] = filled;

        // The fit in continuous space is the same for every mode, so it is done once.
        Span<Vector128<float>> seeds = stackalloc Vector128<float>[3 * 2];

        for (var subset = 0; subset < subsets; subset++) {
            SeedLine(points, members[starts[subset]..starts[subset + 1]], seeds[(subset * 2)..]);
        }

        foreach (var mode in modes) {
            var line = LineOf(mode);
            var total = 0L;

            for (var subset = 0; subset < subsets && total < search.Error; subset++) {
                total += FitLine(
                    points,
                    members[starts[subset]..starts[subset + 1]],
                    seeds.Slice(subset * 2, 2),
                    line,
                    tuning,
                    workCodes.Slice(subset * 8, 8),
                    workIndices,
                    search.Error - total
                );
            }

            if (total >= search.Error) {
                continue;
            }

            search = new() { Mode = mode, Partition = partition, Error = total };
            workCodes.CopyTo(codes);
            workIndices.CopyTo(indices);
        }
    }

    /// <summary>Modes 4 and 5, which give alpha — or whichever channel is rotated into it — its own line.</summary>
    static void TrySeparateAlpha(
        ReadOnlySpan<Vector128<int>> points,
        in Tuning tuning,
        bool opaque,
        ref Candidate search,
        Span<byte> codes,
        Span<byte> colourIndices,
        Span<byte> alphaIndices,
        Span<byte> workCodes,
        Span<byte> workColour,
        Span<byte> workAlpha
    ) {
        if (opaque && !tuning.AllRotations) {
            return;
        }

        Span<Vector128<int>> colour = stackalloc Vector128<int>[Texels];
        Span<Vector128<int>> alpha = stackalloc Vector128<int>[Texels];
        Span<Vector128<float>> colourSeeds = stackalloc Vector128<float>[2];
        Span<Vector128<float>> alphaSeeds = stackalloc Vector128<float>[2];

        var rotations = tuning.AllRotations ? 4 : 1;
        var variants = tuning.AllRotations ? 3 : 1;

        for (var rotation = 0; rotation < rotations; rotation++) {
            SplitChannels(points, rotation, colour, alpha);
            SeedLine(colour, Everyone, colourSeeds);
            SeedLine(alpha, Everyone, alphaSeeds);

            for (var variant = 0; variant < variants; variant++) {
                // Mode 5, then mode 4 with its two ways of spending the three-bit indices.
                var mode = variant == 0 ? 5 : 4;
                var selection = variant == 2;
                var layout = ModeTable[mode];

                var colourLine = new Line(
                    layout.ColourBits,
                    0,
                    Parity.None,
                    BptcTables.WeightsOf(selection ? layout.SecondIndexBits : layout.IndexBits)
                );

                var colourError = FitLine(colour, Everyone, colourSeeds, colourLine, tuning, workCodes[..8], workColour, search.Error);

                if (colourError >= search.Error) {
                    continue;
                }

                var alphaLine = new Line(
                    0,
                    layout.AlphaBits,
                    Parity.None,
                    BptcTables.WeightsOf(selection ? layout.IndexBits : layout.SecondIndexBits)
                );

                var alphaError = FitLine(
                    alpha, Everyone, alphaSeeds, alphaLine, tuning, workCodes.Slice(8, 8), workAlpha, search.Error - colourError
                );

                if (colourError + alphaError >= search.Error) {
                    continue;
                }

                search = new() {
                    Mode = mode,
                    Rotation = rotation,
                    Selection = selection,
                    Error = colourError + alphaError
                };

                // One subset, so the alpha line's channel goes in beside the colour line's three.
                workCodes[..8].CopyTo(codes);
                codes[3] = workCodes[8 + 3];
                codes[7] = workCodes[8 + 4 + 3];
                workColour.CopyTo(colourIndices);
                workAlpha.CopyTo(alphaIndices);
            }
        }
    }

    /// <summary>
    ///     Divides a block into what modes 4 and 5 fit separately: colour with one channel swapped
    ///     for alpha, and the channel left over. A line's fixed channels hold their fixed values, so
    ///     they contribute nothing to its error.
    /// </summary>
    static void SplitChannels(
        ReadOnlySpan<Vector128<int>> points,
        int rotation,
        Span<Vector128<int>> colour,
        Span<Vector128<int>> alpha
    ) {
        for (var texel = 0; texel < Texels; texel++) {
            var point = points[texel];
            var (r, g, b, a) = (point[0], point[1], point[2], point[3]);

            switch (rotation) {
                case 1:
                    (r, a) = (a, r);
                    break;

                case 2:
                    (g, a) = (a, g);
                    break;

                case 3:
                    (b, a) = (a, b);
                    break;
            }

            colour[texel] = Vector128.Create(r, g, b, 255);
            alpha[texel] = Vector128.Create(0, 0, 0, a);
        }
    }

    /// <summary>
    ///     Refines the winner if the level asks for it, orders every subset's endpoints so its anchor's
    ///     top index bit is clear, and writes the block.
    /// </summary>
    static void Finish(
        ReadOnlySpan<Vector128<int>> points,
        in Tuning tuning,
        ref Candidate search,
        Span<byte> codes,
        Span<byte> colourIndices,
        Span<byte> alphaIndices,
        Span<byte> block
    ) {
        if (tuning.WalkRounds > 0 && search.Error > 0) {
            Walk(points, tuning, ref search, codes, colourIndices, alphaIndices);
        }

        Write(search, codes, colourIndices, alphaIndices, block);
    }

    /// <summary>
    ///     Moves each endpoint channel one code at a time and keeps whatever helps. Least squares
    ///     answers in continuous space, and rounding its answer is not the same as finding the best
    ///     code: this is the difference between the two.
    /// </summary>
    static void Walk(
        ReadOnlySpan<Vector128<int>> points,
        in Tuning tuning,
        ref Candidate search,
        Span<byte> codes,
        Span<byte> colourIndices,
        Span<byte> alphaIndices
    ) {
        var layout = ModeTable[search.Mode];

        if (layout.SecondIndexBits != 0) {
            Span<Vector128<int>> colour = stackalloc Vector128<int>[Texels];
            Span<Vector128<int>> alpha = stackalloc Vector128<int>[Texels];

            SplitChannels(points, search.Rotation, colour, alpha);

            var colourLine = new Line(
                layout.ColourBits,
                0,
                Parity.None,
                BptcTables.WeightsOf(search.Selection ? layout.SecondIndexBits : layout.IndexBits)
            );

            var alphaLine = new Line(
                0,
                layout.AlphaBits,
                Parity.None,
                BptcTables.WeightsOf(search.Selection ? layout.IndexBits : layout.SecondIndexBits)
            );

            var colourError = WalkLine(
                colour, Everyone, colourLine, tuning, codes[..8], colourIndices, Score(colour, Everyone, codes[..8], colourLine, colourIndices)
            );

            var alphaError = WalkLine(
                alpha, Everyone, alphaLine, tuning, codes[..8], alphaIndices, Score(alpha, Everyone, codes[..8], alphaLine, alphaIndices)
            );

            search.Error = colourError + alphaError;

            return;
        }

        Span<byte> pattern = stackalloc byte[Texels];
        BptcTables.PatternOf(layout.Subsets, search.Partition, pattern);

        Span<byte> members = stackalloc byte[Texels];
        var line = LineOf(search.Mode);
        var total = 0L;

        for (var subset = 0; subset < layout.Subsets; subset++) {
            var count = 0;

            for (var texel = 0; texel < Texels; texel++) {
                if (pattern[texel] == subset) {
                    members[count++] = (byte)texel;
                }
            }

            var part = members[..count];
            var subsetCodes = codes.Slice(subset * 8, 8);
            var error = Score(points, part, subsetCodes, line, colourIndices);

            total += WalkLine(points, part, line, tuning, subsetCodes, colourIndices, error);
        }

        search.Error = total;
    }

    /// <summary>One line's half of <see cref="Walk" />.</summary>
    /// <remarks>A line's fixed channels are not moved, so two lines can share eight bytes of codes.</remarks>
    static long WalkLine(
        ReadOnlySpan<Vector128<int>> points,
        ReadOnlySpan<byte> members,
        in Line line,
        in Tuning tuning,
        Span<byte> codes,
        Span<byte> indices,
        long error
    ) {
        var step = line.Parity == Parity.None ? 1 : 2;

        for (var round = 0; round < tuning.WalkRounds && error > 0; round++) {
            var improved = false;

            for (var at = 0; at < 8; at++) {
                var bits = (at & 3) == 3 ? line.AlphaBits : line.ColourBits;

                if (bits == 0) {
                    continue;
                }

                var kept = codes[at];

                for (var direction = -step; direction <= step; direction += step * 2) {
                    var moved = codes[at] + direction;

                    if (moved < 0 || moved >= 1 << bits) {
                        continue;
                    }

                    codes[at] = (byte)moved;

                    var score = Score(points, members, codes, line, indices, error);

                    if (score < error) {
                        error = score;
                        kept = (byte)moved;
                        improved = true;
                    } else {
                        codes[at] = kept;
                    }
                }
            }

            // A refused move left its indices behind, so the kept endpoints are scored once more.
            error = Score(points, members, codes, line, indices);

            if (!improved) {
                break;
            }
        }

        return error;
    }

    /// <summary>What a line's endpoints score, given as codes.</summary>
    static long Score(
        ReadOnlySpan<Vector128<int>> points,
        ReadOnlySpan<byte> members,
        ReadOnlySpan<byte> codes,
        in Line line,
        Span<byte> indices,
        long limit = long.MaxValue
    ) =>
        Assign(points, members, ExpandEndpoint(codes[..4], line), ExpandEndpoint(codes.Slice(4, 4), line), line.Weights, indices, limit);

    /// <summary>
    ///     Fits one line through the given texels: quantise, assign, re-solve, round after round.
    /// </summary>
    /// <param name="points">Every texel of the block.</param>
    /// <param name="members">Which of them are on this line.</param>
    /// <param name="seed">Two endpoints in continuous space to start from.</param>
    /// <param name="line">What the endpoints can hold.</param>
    /// <param name="tuning">How long to keep going.</param>
    /// <param name="codes">Eight bytes to fill: two endpoints of RGBA, as codes.</param>
    /// <param name="indices">Sixteen bytes, of which only the members' are written.</param>
    /// <param name="limit">Give up once this much error is certain.</param>
    /// <returns>The summed squared error, or <paramref name="limit" /> if abandoned.</returns>
    /// <remarks>
    ///     A first round over the limit is not abandoned at once: refinement routinely halves it, so
    ///     the first round is allowed twice the limit before the line is given up on.
    /// </remarks>
    static long FitLine(
        ReadOnlySpan<Vector128<int>> points,
        ReadOnlySpan<byte> members,
        ReadOnlySpan<Vector128<float>> seed,
        in Line line,
        in Tuning tuning,
        Span<byte> codes,
        Span<byte> indices,
        long limit
    ) {
        Span<byte> trial = stackalloc byte[8];
        Span<byte> trialIndices = stackalloc byte[Texels];
        var low = seed[0];
        var high = seed[1];
        var best = limit > long.MaxValue / 2 ? long.MaxValue : limit * 2;
        var found = false;
        var straddled = false;

        for (var round = 0; round <= tuning.Refinements; round++) {
            var last = round == tuning.Refinements;
            var improved = false;
            var variants = line.Parity switch {
                Parity.PerEndpoint when tuning.AllParities && (last || round == 0) => 4,
                Parity.Shared => 2,
                _ => 1
            };

            for (var variant = 0; variant < variants; variant++) {
                QuantiseLine(low, high, line, variants == 1 ? -1 : variant, trial);

                var candidate = Score(points, members, trial, line, trialIndices, best);

                if (candidate < best) {
                    best = candidate;
                    found = true;
                    improved = true;
                    trial.CopyTo(codes);

                    foreach (var member in members) {
                        indices[member] = trialIndices[member];
                    }
                }
            }

            // A round that found nothing will find nothing next time either: the same indices solve
            // to the same endpoints.
            if (!improved || best == 0 || last) {
                break;
            }

            if (Solve(points, members, indices, line.Weights, ref low, ref high)) {
                continue;
            }

            if (straddled) {
                break;
            }

            // Every member took one weight, so the line has collapsed to a point, and a point the
            // codes cannot hold stays wrong however often it is solved. Two neighbouring codes reach
            // it through the palette between them, a third of a step finer than either on its own.
            straddled = true;
            Straddle(line, ref low, ref high);
        }

        return !found || best >= limit ? limit : best;
    }

    /// <summary>Spreads a collapsed line across one code step, a third of the way from its first end.</summary>
    static void Straddle(in Line line, ref Vector128<float> low, ref Vector128<float> high) {
        var parity = line.Parity == Parity.None ? 0 : 1;
        var colour = line.ColourBits == 0 ? 0f : (float)(256 >> (line.ColourBits - parity));
        var alpha = line.AlphaBits == 0 ? 0f : (float)(256 >> (line.AlphaBits - parity));
        var step = Vector128.Create(colour, colour, colour, alpha);
        var middle = (low + high) * 0.5f;

        low = middle - (step * (1f / 3f));
        high = low + step;
    }

    /// <summary>Rounds two continuous endpoints to codes this line can hold.</summary>
    /// <param name="low">The first endpoint.</param>
    /// <param name="high">The second endpoint.</param>
    /// <param name="line">What it can hold.</param>
    /// <param name="variant">Which parity assignment, or −1 for whichever rounds closest.</param>
    /// <param name="codes">Eight bytes to fill.</param>
    static void QuantiseLine(Vector128<float> low, Vector128<float> high, in Line line, int variant, Span<byte> codes) {
        switch (line.Parity) {
            case Parity.None:
                QuantiseEndpoint(low, line, -1, codes);
                QuantiseEndpoint(high, line, -1, codes[4..]);
                return;

            case Parity.Shared: {
                var parity = variant & 1;
                QuantiseEndpoint(low, line, parity, codes);
                QuantiseEndpoint(high, line, parity, codes[4..]);
                return;
            }

            default:
                if (variant >= 0) {
                    QuantiseEndpoint(low, line, variant & 1, codes);
                    QuantiseEndpoint(high, line, variant >> 1, codes[4..]);
                    return;
                }

                QuantiseClosest(low, line, codes);
                QuantiseClosest(high, line, codes[4..]);
                return;
        }
    }

    /// <summary>Rounds one endpoint with whichever parity bit loses least.</summary>
    static void QuantiseClosest(Vector128<float> endpoint, in Line line, Span<byte> codes) {
        Span<byte> zero = stackalloc byte[4];
        Span<byte> one = stackalloc byte[4];

        QuantiseEndpoint(endpoint, line, 0, zero);
        QuantiseEndpoint(endpoint, line, 1, one);

        var lossZero = 0f;
        var lossOne = 0f;

        for (var channel = 0; channel < 4; channel++) {
            var bits = channel == 3 ? line.AlphaBits : line.ColourBits;

            if (bits == 0) {
                continue;
            }

            var wanted = endpoint[channel];
            var ifZero = Expand(zero[channel], bits) - wanted;
            var ifOne = Expand(one[channel], bits) - wanted;

            lossZero += ifZero * ifZero;
            lossOne += ifOne * ifOne;
        }

        (lossZero <= lossOne ? zero : one).CopyTo(codes);
    }

    static void QuantiseEndpoint(Vector128<float> endpoint, in Line line, int parity, Span<byte> codes) {
        for (var channel = 0; channel < 4; channel++) {
            var bits = channel == 3 ? line.AlphaBits : line.ColourBits;

            if (bits == 0) {
                codes[channel] = channel == 3 ? (byte)255 : (byte)0;
                continue;
            }

            var wanted = (int)(Math.Clamp(endpoint[channel], 0f, 255f) + 0.5f);

            codes[channel] = Nearest[NearestIndex(bits, parity, wanted)];
        }
    }

    /// <summary>Where a byte's best code lives: nine precisions of three parities of 256 entries.</summary>
    /// <param name="bits">The code's width, parity included.</param>
    /// <param name="parity">The low bit the code must have, or −1 if free.</param>
    /// <param name="value">The byte.</param>
    /// <returns>The offset into <see cref="Nearest" />.</returns>
    static int NearestIndex(int bits, int parity, int value) => (((bits * 3) + parity + 1) * 256) + value;

    static Vector128<int> ExpandEndpoint(ReadOnlySpan<byte> codes, in Line line) {
        var colour = line.ColourBits;
        var alpha = line.AlphaBits;

        return Vector128.Create(
            colour == 0 ? 0 : Expansion[(colour << 8) + codes[0]],
            colour == 0 ? 0 : Expansion[(colour << 8) + codes[1]],
            colour == 0 ? 0 : Expansion[(colour << 8) + codes[2]],
            alpha == 0 ? 255 : Expansion[(alpha << 8) + codes[3]]
        );
    }

    /// <summary>Gives every member its nearest palette entry, and sums what that costs.</summary>
    /// <returns>The summed squared error, or <see cref="long.MaxValue" /> once past <paramref name="limit" />.</returns>
    static long Assign(
        ReadOnlySpan<Vector128<int>> points,
        ReadOnlySpan<byte> members,
        Vector128<int> first,
        Vector128<int> second,
        ReadOnlySpan<byte> weights,
        Span<byte> indices,
        long limit
    ) {
        Span<Vector128<int>> palette = stackalloc Vector128<int>[16];
        var entries = weights.Length;
        var rounding = Vector128.Create(32);

        for (var entry = 0; entry < entries; entry++) {
            var weight = weights[entry];

            palette[entry] = Vector128.ShiftRightArithmetic((first * (64 - weight)) + (second * weight) + rounding, 6);
        }

        var total = 0L;

        foreach (var member in members) {
            var point = points[member];
            var bestDistance = int.MaxValue;
            var bestEntry = 0;

            for (var entry = 0; entry < entries; entry++) {
                var difference = point - palette[entry];
                var distance = Vector128.Sum(difference * difference);

                if (distance < bestDistance) {
                    bestDistance = distance;
                    bestEntry = entry;
                }
            }

            indices[member] = (byte)bestEntry;
            total += bestDistance;

            if (total >= limit) {
                return long.MaxValue;
            }
        }

        return total;
    }

    /// <summary>Re-solves two endpoints from the indices by least squares.</summary>
    /// <returns>False when every member took the same weight and there is nothing to solve.</returns>
    static bool Solve(
        ReadOnlySpan<Vector128<int>> points,
        ReadOnlySpan<byte> members,
        ReadOnlySpan<byte> indices,
        ReadOnlySpan<byte> weights,
        ref Vector128<float> low,
        ref Vector128<float> high
    ) {
        float a = 0f, b = 0f, c = 0f;
        var right0 = Vector128<float>.Zero;
        var right1 = Vector128<float>.Zero;

        foreach (var member in members) {
            var weight = weights[indices[member]] / 64f;
            var inverse = 1f - weight;
            var point = Vector128.ConvertToSingle(points[member]);

            a += inverse * inverse;
            b += inverse * weight;
            c += weight * weight;
            right0 += point * inverse;
            right1 += point * weight;
        }

        var determinant = (a * c) - (b * b);

        if (MathF.Abs(determinant) < 1e-4f) {
            return false;
        }

        var scale = 1f / determinant;

        low = ((right0 * c) - (right1 * b)) * scale;
        high = ((right1 * a) - (right0 * b)) * scale;

        return true;
    }

    /// <summary>The two ends of the members' spread along their principal axis.</summary>
    static void SeedLine(ReadOnlySpan<Vector128<int>> points, ReadOnlySpan<byte> members, Span<Vector128<float>> seed) {
        var mean = Vector128<float>.Zero;

        foreach (var member in members) {
            mean += Vector128.ConvertToSingle(points[member]);
        }

        mean *= 1f / members.Length;

        Span<float> covariance = stackalloc float[16];
        covariance.Clear();

        foreach (var member in members) {
            var centred = Vector128.ConvertToSingle(points[member]) - mean;

            for (var row = 0; row < 4; row++) {
                var column = centred * centred[row];

                covariance[row * 4] += column[0];
                covariance[(row * 4) + 1] += column[1];
                covariance[(row * 4) + 2] += column[2];
                covariance[(row * 4) + 3] += column[3];
            }
        }

        Span<float> axis = stackalloc float[4];
        PrincipalAxis.Find(covariance, 4, axis);

        var direction = Vector128.Create(axis[0], axis[1], axis[2], axis[3]);
        var lowest = float.MaxValue;
        var highest = float.MinValue;

        foreach (var member in members) {
            var centred = Vector128.ConvertToSingle(points[member]) - mean;
            var along = (centred[0] * axis[0]) + (centred[1] * axis[1]) + (centred[2] * axis[2]) + (centred[3] * axis[3]);

            lowest = Math.Min(lowest, along);
            highest = Math.Max(highest, along);
        }

        seed[0] = mean + (direction * lowest);
        seed[1] = mean + (direction * highest);
    }

    /// <summary>Writes a block of one colour in mode 5, which holds any colour exactly.</summary>
    static void WriteSolid(ReadOnlySpan<byte> rgba, Span<byte> block) {
        Span<byte> codes = stackalloc byte[8];
        Span<byte> colourIndices = stackalloc byte[Texels];
        Span<byte> alphaIndices = stackalloc byte[Texels];

        for (var channel = 0; channel < 3; channel++) {
            var (low, high) = SolidColour[rgba[channel]];

            codes[channel] = low;
            codes[channel + 4] = high;
        }

        codes[3] = codes[7] = rgba[3];
        colourIndices.Fill(1);
        alphaIndices.Clear();

        Write(new() { Mode = 5 }, codes, colourIndices, alphaIndices, block);
    }

    /// <summary>Orders each line's endpoints so the anchors' top bits are clear, then lays out the fields.</summary>
    static void Write(
        in Candidate found,
        Span<byte> codes,
        Span<byte> colourIndices,
        Span<byte> alphaIndices,
        Span<byte> block
    ) {
        var layout = ModeTable[found.Mode];
        Span<byte> pattern = stackalloc byte[Texels];
        BptcTables.PatternOf(layout.Subsets, found.Partition, pattern);

        var separate = layout.SecondIndexBits != 0;
        var colourBits = separate && found.Selection ? layout.SecondIndexBits : layout.IndexBits;
        var alphaBits = found.Selection ? layout.IndexBits : layout.SecondIndexBits;

        for (var subset = 0; subset < layout.Subsets; subset++) {
            var anchor = BptcTables.AnchorOf(layout.Subsets, found.Partition, subset);
            var endpoints = codes.Slice(subset * 8, 8);

            if (colourIndices[anchor] >= 1 << (colourBits - 1)) {
                for (var channel = 0; channel < (separate ? 3 : 4); channel++) {
                    (endpoints[channel], endpoints[channel + 4]) = (endpoints[channel + 4], endpoints[channel]);
                }

                for (var texel = 0; texel < Texels; texel++) {
                    if (pattern[texel] == subset) {
                        colourIndices[texel] = (byte)((1 << colourBits) - 1 - colourIndices[texel]);
                    }
                }
            }

            if (separate && alphaIndices[0] >= 1 << (alphaBits - 1)) {
                (endpoints[3], endpoints[7]) = (endpoints[7], endpoints[3]);

                for (var texel = 0; texel < Texels; texel++) {
                    alphaIndices[texel] = (byte)((1 << alphaBits) - 1 - alphaIndices[texel]);
                }
            }
        }

        var writer = new BlockBitWriter(block);

        writer.Write(1u << found.Mode, found.Mode + 1);
        writer.Write((uint)found.Partition, layout.PartitionBits);
        writer.Write((uint)found.Rotation, layout.RotationBits);
        writer.Write(found.Selection ? 1u : 0u, layout.SelectionBits);

        var drop = layout.EndpointParity || layout.SharedParity ? 1 : 0;

        for (var channel = 0; channel < 4; channel++) {
            var bits = channel == 3 ? layout.AlphaBits : layout.ColourBits;

            if (bits == 0) {
                continue;
            }

            for (var endpoint = 0; endpoint < layout.Subsets * 2; endpoint++) {
                writer.Write((uint)(codes[(endpoint * 4) + channel] >> drop), bits);
            }
        }

        if (layout.EndpointParity) {
            for (var endpoint = 0; endpoint < layout.Subsets * 2; endpoint++) {
                writer.Write((uint)(codes[endpoint * 4] & 1), 1);
            }
        } else if (layout.SharedParity) {
            for (var subset = 0; subset < layout.Subsets; subset++) {
                writer.Write((uint)(codes[subset * 8] & 1), 1);
            }
        }

        if (!separate) {
            WriteIndices(ref writer, layout.IndexBits, layout, found.Partition, pattern, colourIndices);
            return;
        }

        // The first set in the stream is always the narrower one; the selection bit says whose it is.
        WriteIndices(ref writer, layout.IndexBits, layout, found.Partition, pattern, found.Selection ? alphaIndices : colourIndices);
        WriteIndices(ref writer, layout.SecondIndexBits, layout, found.Partition, pattern, found.Selection ? colourIndices : alphaIndices);
    }

    static void WriteIndices(
        ref BlockBitWriter writer,
        int bits,
        in ModeLayout layout,
        int partition,
        scoped ReadOnlySpan<byte> pattern,
        scoped ReadOnlySpan<byte> indices
    ) {
        for (var texel = 0; texel < Texels; texel++) {
            var anchor = texel == BptcTables.AnchorOf(layout.Subsets, partition, pattern[texel]);

            writer.Write(indices[texel], anchor ? bits - 1 : bits);
        }
    }

    static void ReadIndices(
        ref BlockBitReader reader,
        int bits,
        in ModeLayout layout,
        int partition,
        scoped ReadOnlySpan<byte> pattern,
        scoped Span<byte> indices
    ) {
        for (var texel = 0; texel < Texels; texel++) {
            var anchor = texel == BptcTables.AnchorOf(layout.Subsets, partition, pattern[texel]);

            indices[texel] = (byte)reader.Read(anchor ? bits - 1 : bits);
        }
    }

    /// <summary>The specification's interpolation, which is exact and leaves nothing to choose.</summary>
    static int Interpolate(int first, int second, int weight) => (((64 - weight) * first) + (weight * second) + 32) >> 6;

    /// <summary>Widens a code to eight bits by repeating its top bits below it.</summary>
    static byte Expand(int code, int bits) {
        var shifted = code << (8 - bits);

        return (byte)(shifted | (shifted >> bits));
    }

    static (byte, byte)[] BuildSolidColour() {
        var table = new (byte, byte)[256];

        for (var value = 0; value < 256; value++) {
            var bestError = int.MaxValue;
            var near = value >> 1;

            for (var low = Math.Max(near - 4, 0); low <= Math.Min(near + 4, 127) && bestError > 0; low++) {
                for (var high = Math.Max(near - 4, 0); high <= Math.Min(near + 4, 127); high++) {
                    var decoded = Interpolate(Expand(low, 7), Expand(high, 7), BptcTables.Weights2[1]);
                    var error = Math.Abs(decoded - value);

                    if (error < bestError) {
                        bestError = error;
                        table[value] = ((byte)low, (byte)high);
                    }
                }
            }
        }

        return table;
    }

    static byte[] BuildNearest() {
        var table = new byte[9 * 3 * 256];

        for (var bits = 1; bits <= 8; bits++) {
            for (var parity = -1; parity <= 1; parity++) {
                for (var value = 0; value < 256; value++) {
                    var bestDistance = int.MaxValue;
                    var best = 0;

                    for (var code = 0; code < 1 << bits; code++) {
                        if (parity >= 0 && (code & 1) != parity) {
                            continue;
                        }

                        var distance = Math.Abs(Expand(code, bits) - value);

                        if (distance < bestDistance) {
                            bestDistance = distance;
                            best = code;
                        }
                    }

                    table[NearestIndex(bits, parity, value)] = (byte)best;
                }
            }
        }

        return table;
    }

    static byte[] BuildExpansion() {
        var table = new byte[9 * 256];

        for (var bits = 1; bits <= 8; bits++) {
            for (var code = 0; code < 1 << bits; code++) {
                table[(bits << 8) + code] = Expand(code, bits);
            }
        }

        return table;
    }
}
