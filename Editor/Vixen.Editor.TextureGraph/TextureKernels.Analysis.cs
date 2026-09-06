// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

namespace Vixen.Editor.TextureGraph;

/// <summary>Which field of a <c>Distance</c> node's flood is read.</summary>
/// <remarks>
///     ⚠ <b>The numbers are the contract with <c>Shaders/Distance.rvn</c>'s <c>mode</c>, which
///     compares against them literally.</b> Nothing in the compilation would notice a renumbering;
///     the picture would simply be the inside of the shape instead of the outside, which is a
///     perfectly plausible picture.
/// </remarks>
internal enum TextureDistanceMode {
    /// <summary>Zero on the shape, rising away from it. The usual one.</summary>
    Outside = 0,

    /// <summary>Zero off the shape, rising into it.</summary>
    Inside = 1,

    /// <summary>Both, signed about a half — a half exactly on the boundary.</summary>
    Both = 2
}

/// <summary>Which of a <c>Flood Fill</c>'s five pictures an op reads out of the settled record.</summary>
/// <remarks>
///     ⚠ <b><see cref="Size" /> is the island's bounding box and not its area</b> — see
///     <c>Shaders/FloodFill.rvn</c>. A pixel count is a sum over the island and the chain that
///     produces the record sums nothing: bounds merge by min and max, which is exactly what lets one
///     monotone iteration settle them.
/// </remarks>
internal enum TextureFloodOutput {
    /// <summary>
    ///     The island's name: the minimum corner of its box in red and green, and the whole record
    ///     hashed into blue.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Three channels, because the corner alone is not a name.</b> Two islands never share a
    ///     settled bounding <em>box</em> — <see href="https://github.com/Rikarin/Vixen/issues/691">
    ///     #691</see> says they can and they cannot; each would have to cross the shared box in both
    ///     directions and digital topology's 4/8 duality forbids the pair. But they share its
    ///     <em>minimum corner</em> all the time: a bar with a hook starting under its left end is
    ///     enough, and 1 273 of the 65 536 four-by-four masks contain such a pair. So red and green
    ///     stay the addressable corner and blue carries the rest, and a consumer comparing two ids
    ///     compares all three.
    /// </remarks>
    Id = 0,

    /// <summary>A value per island, seeded from the box and the op's own seed.</summary>
    Random = 1,

    /// <summary>Where in its own bounding box a texel is, in red and green.</summary>
    LocalUv = 2,

    /// <summary>The box itself: minimum in red and green, maximum in blue and alpha.</summary>
    BoundingBox = 3,

    /// <summary>The box's width, height and longer side, each as a fraction of the image.</summary>
    Size = 4
}

/// <summary>Doc 48 § 4.5's three analysis kernels, by the name a <see cref="TextureOp" /> gives.</summary>
/// <remarks>
///     <b>Names rather than a registry, for <c>TextureColourKernels</c>'s reason</b> —
///     <see cref="TextureKernels" /> already is one, and what is missing without this file is
///     somewhere for a plan to say <c>Kernel = "JumpFlood"</c> without a string literal, and somewhere
///     for the integer contracts above to live beside the sources that read them. ⚠ Six names for
///     three nodes: two of the three are chains rather than dispatches, which is what
///     <see cref="TextureAnalysis" /> exists to emit.
/// </remarks>
[TextureKernelSurface]
internal static class TextureAnalysisKernels {
    /// <summary>One ping-ponged step of the jump flood behind <c>Distance</c>.</summary>
    public const string JumpFlood = "JumpFlood";

    /// <summary>The settled flood record read out as a grey.</summary>
    public const string Distance = "Distance";

    /// <summary>A Sobel magnitude with a tap spacing and a soft threshold.</summary>
    public const string EdgeDetect = "EdgeDetect";

    /// <summary>One iteration of the bounds propagation behind <c>Flood Fill</c>.</summary>
    public const string FloodBounds = "FloodBounds";

    /// <summary>Whether the last <see cref="FloodBounds" /> iteration changed anything.</summary>
    public const string FloodResidual = "FloodResidual";

    /// <summary>The settled bounds record read out as one of five pictures.</summary>
    public const string FloodFill = "FloodFill";

    /// <summary>Every kernel this slice registers, which is what the roll call enumerates.</summary>
    public static IReadOnlyList<string> All { get; } = [
        Distance,
        EdgeDetect,
        FloodBounds,
        FloodFill,
        FloodResidual,
        JumpFlood
    ];
}

/// <summary>The op chains doc 48 § 4.5's three analysis nodes are.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two of the three are not one dispatch, and that is the whole difficulty of § 4.5.</b>
///         <c>Distance</c> is a jump flood — <c>log2(n)</c> ping-ponged dispatches over a record, then
///         a read — and <c>Flood Fill</c> is a label propagation to a fixed point whose length depends
///         on the <em>shape</em> of the mask. Both are built here rather than written out at a call
///         site, because the chains have to be emitted in the right order over the right images and a
///         plan built by hand is one transposition away from a picture that looks plausible.
///     </para>
///     <para>
///         ⚠ <b>These are the first builders in the assembly whose <em>op count</em> depends on the
///         resolution the graph is being baked at, which no other node's does.</b> A blur is one
///         dispatch at 1K and one at 4K with a wider radius; a jump flood is eleven dispatches at 1K
///         and thirteen at 4K, because the number of halvings is <c>log2</c> of the baked extent. So a
///         plan holding one of these is built for a <see cref="TexturePlan.BakeLevelOffset" /> and
///         cannot simply be re-baked at another — the ops themselves have to be re-emitted. ⚠ Every
///         op of both chains therefore carries <see cref="TextureOp.EmittedForExtent" />, which is
///         <a href="https://github.com/Rikarin/Vixen/issues/689">#689</a>: without it the re-bake
///         was silent, and <c>TexturePlan.Validate</c> now refuses the list at any other extent
///         rather than emitting too few halvings. (<c>AutoLevels</c>' reduction has the same
///         property and its chain is built at call sites, which owe the same stamp; that it cannot
///         be <em>tiled</em> is a different property and is still recorded nowhere.)
///     </para>
///     <para>
///         <b>Every builder emits the complete parameter set its kernel declares</b>, for
///         <see cref="TextureSources" />'s reason: <c>TexturePlanEvaluator.Uniforms</c> refuses an op
///         that leaves one out, and a plan written by hand is a chance to name the wrong one.
///     </para>
/// </remarks>
[TextureKernelSurface]
internal static class TextureAnalysis {
    /// <summary>The largest integer a half-float stores exactly, and the ceiling both chains have.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Doc 48 § D5 admits no 32-bit float format, and these are the two nodes in the
    ///         catalogue that want one.</b> A jump flood's record is an offset in texels and a flood
    ///         fill's is a pair of texel coordinates; a half is exact on the integers only up to 2048,
    ///         and past that a coordinate quantises to even texels — which merges the identities of
    ///         two islands two texels apart and shortens a distance field by up to a texel.
    ///     </para>
    ///     <para>
    ///         <b>So it is refused where the chain is built rather than clamped inside the kernel.</b>
    ///         A kernel that clamped would be silent, and silence about a number an artist chose is
    ///         the failure this repository has already paid for once. The refusal names both numbers.
    ///     </para>
    /// </remarks>
    public const int ExactExtent = 2048;

    /// <summary>How many <c>JumpFlood</c> dispatches an image of this size needs.</summary>
    /// <param name="width">The width of the image the node writes, in texels of this bake.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The number of ops <see cref="Distance(int, int, ImmutableArray{int}, int, int, TextureDistanceMode, float, float)" /> will emit before the read.</returns>
    /// <remarks>
    ///     ⚠ <b>The seeding pass is also the first jump, which is why this is <c>log2(n)</c> and not
    ///     <c>log2(n) + 1</c>.</b> <c>JumpFlood.rvn</c>'s <c>first</c> uniform makes its taps read the
    ///     mask and seed it in place, so the widest jump happens over the seeding rather than after
    ///     it. Counting one too many is a wasted dispatch; counting one too few leaves the field
    ///     wrong by whatever the last halving would have fixed, which is a plausible picture.
    /// </remarks>
    public static int FloodDispatches(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        return Math.Max(1, int.Log2(NextPowerOfTwo(Math.Max(width, height))));
    }

    /// <summary>Doc 48 § 4.5's <c>Distance</c>: the whole chain, ready to append to a plan.</summary>
    /// <param name="output">The grey image the node writes.</param>
    /// <param name="mask">The mask it measures from.</param>
    /// <param name="scratch">
    ///     The ping-pong images, exactly <see cref="FloodDispatches" /> of them, each the size of
    ///     <paramref name="output" /> and in a format that can hold a signed offset —
    ///     <see cref="TextureFormat.Rgba16Float" />.
    /// </param>
    /// <param name="width">The width of the image being written, in texels of this bake.</param>
    /// <param name="height">Its height.</param>
    /// <param name="mode">Which side of the shape is measured.</param>
    /// <param name="maxDistance">How far the field reaches, as a fraction of the image's longer side.</param>
    /// <param name="threshold">What counts as inside the mask.</param>
    /// <returns>The ops, in evaluation order.</returns>
    /// <exception cref="ArgumentException">The scratch list is the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    ///     The maximum distance is outside 0..1, or is more texels than a half-float record can hold
    ///     exactly — see <see cref="ExactExtent" />.
    /// </exception>
    public static ImmutableArray<TextureOp> Distance(
        int output,
        int mask,
        ImmutableArray<int> scratch,
        int width,
        int height,
        TextureDistanceMode mode = TextureDistanceMode.Outside,
        float maxDistance = 0.25f,
        float threshold = 0.5f
    ) {
        var dispatches = FloodDispatches(width, height);

        if (scratch.Length != dispatches) {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A {width}×{height} distance field is {dispatches} jump-flood dispatches and this names {scratch.Length} scratch image(s)."
                )
                + " Each dispatch writes one, because an image in a plan is written exactly once — that is "
                + "what makes its liveness the op order.",
                nameof(scratch)
            );
        }

        if (maxDistance is <= 0f or > 1f) {
            throw new ArgumentOutOfRangeException(
                nameof(maxDistance),
                maxDistance,
                "The maximum distance is a fraction of the image's longer side, so it is in 0..1."
            );
        }

        var texels = maxDistance * Math.Max(width, height);

        if (texels > ExactExtent) {
            throw new ArgumentOutOfRangeException(
                nameof(maxDistance),
                maxDistance,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{maxDistance} of a {width}×{height} image is {texels:0} texels, and a jump flood's record is an offset in a half-float, which is exact on the integers only to {ExactExtent}."
                )
                + " Past that the field quantises to even texels rather than failing, so it is refused here — "
                + "doc 48 § D5 admits no 32-bit float format and this is one of the two nodes that wants one."
            );
        }

        var ops = ImmutableArray.CreateBuilder<TextureOp>(dispatches + 1);
        var step = NextPowerOfTwo(Math.Max(width, height)) / 2;

        for (var pass = 0; pass < dispatches; pass++) {
            ops.Add(
                new() {
                    Kernel = TextureAnalysisKernels.JumpFlood,
                    Output = scratch[pass],
                    Inputs = [pass == 0 ? mask : scratch[pass - 1]],
                    // ⚠ #689: how many of these there are is log2 of the extent, so the list is
                    // emitted for one bake and TexturePlan.Validate refuses it at another.
                    EmittedForExtent = Math.Max(width, height),
                    Parameters = [
                        new("first", pass == 0 ? 1f : 0f),
                        new("step", Math.Max(step, 1)),
                        new("threshold", threshold),
                        new("maxDistance", maxDistance)
                    ]
                }
            );

            step /= 2;
        }

        ops.Add(
            new() {
                Kernel = TextureAnalysisKernels.Distance,
                Output = output,
                Inputs = [scratch[^1]],
                Parameters = [new("mode", (float)mode), new("maxDistance", maxDistance)]
            }
        );

        return ops.ToImmutable();
    }

    /// <summary>Doc 48 § 4.5's <c>Edge Detect</c>.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="source">The picture to find edges in, read from its red channel.</param>
    /// <param name="width">The operator's tap spacing, in texels at the plan's base resolution.</param>
    /// <param name="threshold">Magnitudes at or below this are black.</param>
    /// <returns>The op.</returns>
    public static TextureOp EdgeDetect(int output, int source, float width = 1f, float threshold = 0f) =>
        new() {
            Kernel = TextureAnalysisKernels.EdgeDetect,
            Output = output,
            Inputs = [source],
            Parameters = [
                new("width", width, TextureParameterUnit.TexelsAtBase),
                new("threshold", threshold)
            ]
        };

    /// <summary>Doc 48 § 4.5's <c>Flood Fill</c>: the propagation and the read.</summary>
    /// <param name="output">The image the node writes.</param>
    /// <param name="mask">The mask whose islands are found.</param>
    /// <param name="scratch">
    ///     The iteration images, one per iteration of the budget, each the size of
    ///     <paramref name="output" /> and <see cref="TextureFormat.Rgba16Float" />.
    /// </param>
    /// <param name="width">The width of the image being written, in texels of this bake.</param>
    /// <param name="height">Its height.</param>
    /// <param name="kind">Which of the five pictures to read out.</param>
    /// <param name="diagonal">Whether diagonally touching texels are one island.</param>
    /// <param name="threshold">What counts as inside the mask.</param>
    /// <returns>The ops, in evaluation order.</returns>
    /// <exception cref="ArgumentException">The scratch list is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The image is larger than a half-float record can name exactly.</exception>
    /// <remarks>
    ///     ⚠ <b>The budget is <paramref name="scratch" />'s length and nothing here can know whether
    ///     it was enough</b> — the settling time is a property of the mask's shape. Append
    ///     <see cref="Residual" /> over the last two scratch images and a <c>MinMaxReduce</c> chain
    ///     over that, and read the one texel: zero means the flood converged, anything else means it
    ///     was truncated. That is § 4.5's "reports truncation rather than a while-loop on the device",
    ///     and it is the reason the node is four kernels.
    /// </remarks>
    public static ImmutableArray<TextureOp> FloodFill(
        int output,
        int mask,
        ImmutableArray<int> scratch,
        int width,
        int height,
        TextureFloodOutput kind = TextureFloodOutput.Random,
        bool diagonal = false,
        float threshold = 0.5f
    ) {
        if (scratch.IsDefaultOrEmpty) {
            throw new ArgumentException(
                "A flood fill is at least one propagation dispatch, and the scratch images are its budget.",
                nameof(scratch)
            );
        }

        var extent = Math.Max(width, height);

        if (extent > ExactExtent) {
            throw new ArgumentOutOfRangeException(
                nameof(width),
                extent,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"A flood fill's record is a pair of texel coordinates in a half-float, which is exact on the integers only to {ExactExtent}, and this image is {width}×{height}."
                )
                + " Past that a bounding box quantises to even texels, which gives two islands two texels apart "
                + "the same identity — so it is refused rather than produced. Doc 48 § D5 admits no 32-bit "
                + "float format."
            );
        }

        var ops = ImmutableArray.CreateBuilder<TextureOp>(scratch.Length + 1);

        for (var pass = 0; pass < scratch.Length; pass++) {
            ops.Add(
                new() {
                    Kernel = TextureAnalysisKernels.FloodBounds,
                    Output = scratch[pass],
                    Inputs = [pass == 0 ? mask : scratch[pass - 1]],
                    // ⚠ #689: the budget is chosen against the mask's size in texels, and the
                    // half-float record's ceiling below is a property of this bake's extent too — so
                    // this list, like the jump flood's, is emitted for one resolution.
                    EmittedForExtent = extent,
                    Parameters = [
                        new("first", pass == 0 ? 1f : 0f),
                        new("threshold", threshold),
                        new("diagonal", diagonal ? 1f : 0f)
                    ]
                }
            );
        }

        ops.Add(FloodRead(output, scratch[^1], kind));

        return ops.ToImmutable();
    }

    /// <summary>One more picture out of a settled flood record, without flooding again.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="bounds">The settled record — the last image the propagation wrote.</param>
    /// <param name="kind">Which of the five pictures.</param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     ⚠ <b>This is why doc 48 § 4.5 can say the node has five outputs and still be one
    ///     node.</b> A graph that wants an island's random colour <em>and</em> its size runs the
    ///     propagation once and reads it twice, which is two dispatches rather than two floods. A front
    ///     end that re-ran the chain per output would be correct and would cost the settling time
    ///     again — which, for the shapes this node is slow on, is the whole cost of the node.
    /// </remarks>
    public static TextureOp FloodRead(int output, int bounds, TextureFloodOutput kind) =>
        new() {
            Kernel = TextureAnalysisKernels.FloodFill,
            Output = output,
            Inputs = [bounds],
            Parameters = [new("kind", (float)kind)]
        };

    /// <summary>Whether the last propagation iteration changed anything, as a picture.</summary>
    /// <param name="output">The image to write, one where the two records differ.</param>
    /// <param name="previous">The record before the last iteration.</param>
    /// <param name="current">The record after it.</param>
    /// <returns>The op. ⚠ It carries no parameters, because the kernel declares none.</returns>
    public static TextureOp Residual(int output, int previous, int current) =>
        new() { Kernel = TextureAnalysisKernels.FloodResidual, Output = output, Inputs = [previous, current] };

    /// <summary>One op per kernel this slice ships, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what a test over the builders prints on the day one of them is forgotten.</b> A
    ///     theory with an <c>InlineData</c> per kernel passes silently when a seventh is added and not
    ///     listed. The two chains are represented by their first op, which is the one that carries the
    ///     seeding parameters, and by the read that ends them.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [
        .. Distance(0, 1, [2, 3, 4, 5, 6, 7], 64, 64),
        EdgeDetect(0, 1),
        .. FloodFill(0, 1, [2, 3], 64, 64),
        Residual(0, 1, 2)
    ];

    static int NextPowerOfTwo(int value) => value <= 1 ? 1 : (int)BitOperations.RoundUpToPowerOf2((uint)value);
}
