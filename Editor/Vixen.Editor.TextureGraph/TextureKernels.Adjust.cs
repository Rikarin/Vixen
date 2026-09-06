// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Editor.TextureGraph;

/// <summary>The one op chain in doc 48 § 4.2, which is <c>Auto Levels</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A builder for the same reason <see cref="TextureAnalysis" /> is one, and it was owed
///         for a batch</b> — <a href="https://github.com/Rikarin/Vixen/issues/713">#713</a>.
///         <c>Auto Levels</c> is <em>one dispatch per reduction level plus the map</em>, so how many
///         ops it is depends on the resolution the plan is baked at; written out at a call site, the
///         chain is one editor session away from being two dispatches short at 4K, which stops the
///         reduction at 4×4 and stretches the picture by the extremes of a corner block. That is a
///         slightly flat picture and not a broken one, which is why nothing noticed.
///     </para>
///     <para>
///         <b>So every reduction op carries <see cref="TextureOp.EmittedForExtent" /></b> and
///         <see cref="TexturePlan.Validate" /> refuses the list at any other bake. ⚠ The stamp is the
///         extent of the op's <em>own output</em> — a reduction op writes a smaller image than it
///         reads, and <c>Validate</c> compares the stamp with the longer side of
///         <see cref="TextureOp.Output" />, so stamping the source's extent would refuse the plan at
///         the very bake it was emitted for.
///     </para>
///     <para>
///         ⚠ <b>What this does not record is that <c>Auto Levels</c> cannot be evaluated in
///         tiles.</b> Its output depends on every texel of its input, and a tiled evaluator would
///         produce a different stretch in every tile — a plausible picture again.
///         <a href="https://github.com/Rikarin/Vixen/issues/636">#636</a> is that property and it is
///         still recorded nowhere.
///     </para>
/// </remarks>
internal static class TextureAdjust {
    /// <summary>How far one <c>MinMaxReduce</c> dispatch reduces each axis.</summary>
    /// <remarks>
    ///     ⚠ <b>The contract with <c>Shaders/MinMaxReduce.rvn</c>'s <c>MaxBlock</c>, which is a
    ///     ceiling on its own loop and therefore on how many levels one dispatch may skip.</b> A
    ///     ladder built for a wider block than the kernel loops over reads only the first 8×8 of each
    ///     block and finds the extremes of a corner — a plausible pair. <c>TextureAdjustTests</c>
    ///     asserts the two numbers agree by reading the source.
    /// </remarks>
    public const int ReduceBlock = 8;

    /// <summary>How many mip levels the widest rung skips, which is <c>log2</c> of the block.</summary>
    const int Wide = 3;

    /// <summary>The level offset of each scratch image, counted from the image being stretched.</summary>
    /// <param name="width">The width of the image the node reads, in texels of this bake.</param>
    /// <param name="height">Its height.</param>
    /// <returns>
    ///     A rising list of level offsets, one per reduction dispatch, the last of which names a 1×1
    ///     image.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">Either extent is not positive.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>What a caller declares its images from</b>, so that the ladder in the image table
    ///         and the ladder in the op list cannot be two different ladders. On a power-of-two image
    ///         it is <c>3, 6, 9…</c>: a block of <see cref="ReduceBlock" /> is three mip levels.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>But not always three, and the exception is a defect rather than a nicety.</b> A
    ///         level is a <em>floored</em> halving, so a rung's block is
    ///         <c>ceil(parent / (parent >> 3))</c> — which for a 9-texel axis is 9, one more than the
    ///         kernel's <c>MaxBlock</c> loops over. The last column would simply not be read, and the
    ///         extremes would be the extremes of the rest of the image: a plausible pair, quietly
    ///         wrong, on any extent that is not a multiple of eight. Where three does not fit the
    ///         step is two, whose block is at most seven for every extent there is.
    ///     </para>
    /// </remarks>
    public static ImmutableArray<int> ReductionLevels(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        var levels = ImmutableArray.CreateBuilder<int>();
        var level = 0;

        while (level == 0 || Math.Max(Reduced(width, level), Reduced(height, level)) > 1) {
            var parentWidth = Reduced(width, level);
            var parentHeight = Reduced(height, level);

            level += Fits(parentWidth, Wide) && Fits(parentHeight, Wide) ? Wide : Wide - 1;
            levels.Add(level);
        }

        return levels.ToImmutable();
    }

    /// <summary>How many <c>MinMaxReduce</c> dispatches an image of this size needs.</summary>
    /// <param name="width">The width of the image the node reads, in texels of this bake.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The number of ops <see cref="AutoLevels" /> emits before the map.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Either extent is not positive.</exception>
    /// <remarks>
    ///     ⚠ <b>Counted off the ladder rather than as <c>log8</c> of the longer side</b>, because the
    ///     two differ twice over: on a non-square image an axis that has reached one texel stays
    ///     there while the other keeps going, and on an extent that is not a multiple of eight a rung
    ///     is two levels rather than three.
    /// </remarks>
    public static int ReductionDispatches(int width, int height) => ReductionLevels(width, height).Length;

    /// <summary>Doc 48 § 4.2's <c>Auto Levels</c>: the reduction and the map, ready to append.</summary>
    /// <param name="output">The stretched image the node writes.</param>
    /// <param name="source">The image whose extremes are found and which is then stretched.</param>
    /// <param name="scratch">
    ///     The reduction images, exactly <see cref="ReductionDispatches" /> of them, declared at the
    ///     offsets <see cref="ReductionLevels" /> gives and in a format that holds a pair —
    ///     <see cref="TextureFormat.Rgba16Float" />.
    /// </param>
    /// <param name="width">The width of <paramref name="source" />, in texels of this bake.</param>
    /// <param name="height">Its height.</param>
    /// <returns>The ops, in evaluation order.</returns>
    /// <exception cref="ArgumentException">The scratch list is the wrong length.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Either extent is not positive.</exception>
    /// <remarks>
    ///     ⚠ <b><c>first</c> is <c>1</c> on the first dispatch and <c>0</c> on every one after it</b>,
    ///     and it is not a flag about the plan: it says whether a tap is a grey value or an already
    ///     reduced <c>(min, max)</c> pair. Leaving it <c>1</c> on a later level reduces the pair's
    ///     minimum against itself and loses the maximum entirely — a picture stretched from
    ///     <c>(min, min)</c>, which is black.
    /// </remarks>
    public static ImmutableArray<TextureOp> AutoLevels(
        int output,
        int source,
        ImmutableArray<int> scratch,
        int width,
        int height
    ) {
        var levels = ReductionLevels(width, height);
        var dispatches = levels.Length;

        if (scratch.Length != dispatches) {
            throw new ArgumentException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Auto Levels over a {width}×{height} image is {dispatches} reduction dispatch(es), and this names {scratch.Length} scratch image(s)."
                )
                + " Each dispatch writes one, because an image in a plan is written exactly once — and a"
                + " chain that is one short leaves the extremes of a block rather than of the image.",
                nameof(scratch)
            );
        }

        var ops = ImmutableArray.CreateBuilder<TextureOp>(dispatches + 1);

        for (var pass = 0; pass < dispatches; pass++) {
            ops.Add(
                new() {
                    Kernel = TextureColourKernels.MinMaxReduce,
                    Output = scratch[pass],
                    Inputs = [pass == 0 ? source : scratch[pass - 1]],

                    // Each pass reads the level above the one it writes, which is the reduction —
                    // #801.
                    ReadsOtherExtents = true,
                    // ⚠ #713: the number of these is a function of the baked extent, so the list is
                    // emitted for one bake and TexturePlan.Validate refuses it at another. The
                    // number is this op's own output extent, which is what Validate compares.
                    EmittedForExtent = Math.Max(Reduced(width, levels[pass]), Reduced(height, levels[pass])),
                    Parameters = [new("first", pass == 0 ? 1f : 0f)]
                }
            );
        }

        ops.Add(
            new() {
                Kernel = TextureColourKernels.AutoLevels,
                Output = output,
                Inputs = [source, scratch[^1]],

                // ⚠ Not a resampler — it is pointwise over `source` — and it still reads another
                // size, because the second input is the 1×1 the reduction ended on. #801's own list
                // of six rescaling kernels would not have held this op, which is why the property is
                // on the op rather than on the kernel.
                ReadsOtherExtents = true,

                // ⚠ And the flag is narrowed to that second input, because it is the *first* one the
                // guard exists for — #878. This is the one op in the library where a per-op
                // declaration silences something worth checking: `source` is read pointwise at the
                // coordinate being written, so a plan handing this dispatch a source of another size
                // draws its top-left corner smeared, and until the list existed the 1×1 statistics
                // image bought that mismatch its silence too.
                OtherExtentInputs = [scratch[^1]]
            }
        );

        return ops.ToImmutable();
    }

    /// <summary>One axis at a level offset, on the plan's own arithmetic — a floored halving.</summary>
    static int Reduced(int extent, int level) => Math.Max(1, extent >> Math.Min(level, 31));

    /// <summary>Whether one axis can skip that many levels within the kernel's block.</summary>
    /// <remarks>
    ///     ⚠ <b>A ceiling and not a ratio.</b> The kernel reads
    ///     <c>ceil(parent / target)</c> texels per axis and clamps that at
    ///     <see cref="ReduceBlock" />; a rung where the ceiling is larger loses the remainder
    ///     silently, and the remainder is where an artist's brightest texel is as often as anywhere
    ///     else.
    /// </remarks>
    static bool Fits(int parent, int level) {
        var target = Reduced(parent, level);

        return (parent + target - 1) / target <= ReduceBlock;
    }
}
