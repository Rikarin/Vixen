// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.Editor.TextureGraph;

/// <summary>How a placement kernel folds one instance into what the ones before it left.</summary>
/// <remarks>
///     ⚠ <b>The numbers are the contract with <c>accumulation</c> in both placement kernels, which
///     compare against them literally.</b> Nothing in the compilation would notice a renumbering — the
///     picture would be a different, entirely plausible one. <c>TexturePlacementDeviceTests</c> pins
///     each by a closed form only it satisfies.
/// </remarks>
internal enum TexturePlacementAccumulation {
    /// <summary>The brightest of the overlapping stamps wins. Two discs look like two discs.</summary>
    Max = 0,

    /// <summary>
    ///     They sum. ⚠ <b>The only mode whose total is a closed form</b> — the mean of the result is
    ///     the instance count times the area of one instance times the pattern's own mean, exactly,
    ///     however the instances landed — which is why every quantitative assertion about these two
    ///     kernels is written against it.
    /// </summary>
    Add = 1,

    /// <summary>
    ///     Each instance is composited over the last, in the order the kernel walks them: cell raster
    ///     order for <c>TileSampler</c> and instance order for <c>Splatter</c>. Both are the same for
    ///     every texel, which is what makes an ordered composite well defined in a gather.
    /// </summary>
    Blend = 2
}

/// <summary>Doc 48 § 4.7's two placement kernels, as ops a plan can hold.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These two are what doc 48 § D7 puts in FX-Map's place, and the refusals below are the
///         half of that trade a type can carry.</b> What recursion buys is an instance count that
///         depends on the pattern; what it costs is a node whose cost nobody can state. Here the cost
///         is stated — <c>TileSampler</c> searches at most
///         <c>(2·<see cref="MaxSearch" />+1)²</c> cells per sub-sample and <c>Splatter</c>
///         walks at most <see cref="MaxInstances" /> instances — and the parameters that would need
///         more are <b>refused here rather than clamped in the kernel</b>.
///     </para>
///     <para>
///         ⚠ <b>That is <a href="https://github.com/Rikarin/Vixen/issues/678">#678</a>'s lesson.</b> A
///         kernel that clamps a size to its own constant is correct at the resolution it was tuned at
///         and quietly wrong at a large bake, because the clamp is invisible: the picture is a picture.
///         The kernels keep their ceilings — a loop bound is a correctness property, and a NaN arriving
///         in <c>scale</c> must not be a loop no invocation leaves — but a plan that would reach one
///         cannot be built through this class.
///     </para>
///     <para>
///         <b>Every parameter is a scalar</b>, because <c>TexturePlanEvaluator.Uniforms</c> writes one
///         <see cref="float" /> per uniform-block member: a <c>float2</c> would receive its x and a
///         zero. And <b>not one of them is a
///         <see cref="TextureParameterUnit.TexelsAtBase" /></b> — a grid is a count, a scale is a
///         fraction of a cell or of the image, and a rotation is an angle — so doc 48 § D8's
///         scaling never touches either kernel and the same op is the same picture at every bake
///         resolution by construction.
///     </para>
///     <para>
///         <b>Four inputs, and a plan that wants none of the maps binds the pattern again.</b> The
///         evaluator binds an op's inputs positionally over the textures a kernel declares and refuses
///         a count mismatch, so an optional input is not a shape the evaluator has. Binding one view
///         twice is free, and the <c>…Amount</c> uniforms at zero are what say it is not read —
///         the short <c>TileSampler</c> and <c>Splatter</c> overloads do exactly that.
///     </para>
/// </remarks>
[TextureKernelSurface]
internal static class TexturePlacement {
    /// <summary>A grid of cells with one instance of a pattern in each.</summary>
    public const string TileSamplerKernel = "TileSampler";

    /// <summary>A free scatter of a bounded number of instances.</summary>
    public const string SplatterKernel = "Splatter";

    /// <summary>The furthest, in cells, <c>TileSampler</c> looks for an instance that covers a texel.</summary>
    /// <remarks>
    ///     ⚠ <b>Duplicated from <c>Shaders/TileSampler.rvn</c>'s <c>MaxSearch</c>, exactly as
    ///     <c>TexturePlanEvaluator.GroupSize</c> is duplicated from every kernel's
    ///     <c>[ComputeShader]</c>.</b> And for the same reason: a Raven <c>const val</c> is not in the
    ///     reflection, so a host that has to reason about it has to know it.
    ///     <c>TexturePlacementKernelTests</c> asserts the two agree, which is the only thing that can.
    /// </remarks>
    public const int MaxSearch = 3;

    /// <summary>The most instances one <c>Splatter</c> op may place.</summary>
    /// <remarks>Duplicated from <c>Shaders/Splatter.rvn</c>'s <c>MaxInstances</c>, and asserted equal.</remarks>
    public const int MaxInstances = 256;

    /// <summary>Half the diagonal of a unit square: how far a rotated instance reaches past its size.</summary>
    const float HalfDiagonal = 0.70710678f;

    /// <summary>A grid of instances of one pattern, with everything about each drawn from the seed.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="pattern">The stamp, read as an atlas of <paramref name="patternCount" /> columns.</param>
    /// <param name="mask">Read at each instance's centre; under the threshold it is dropped.</param>
    /// <param name="sizeMap">Read at each instance's centre; shrinks it, under its own amount.</param>
    /// <param name="rotationMap">Read at each instance's centre; turns it, under its own amount.</param>
    /// <param name="gridX">How many cells across.</param>
    /// <param name="gridY">How many cells down.</param>
    /// <param name="scale">An instance's size as a fraction of its cell.</param>
    /// <param name="scaleJitter">How much an instance may randomly shrink, 0–1.</param>
    /// <param name="positionJitter">How far it may randomly move inside its cell, 0–1.</param>
    /// <param name="rotation">The rotation every instance starts at, in radians.</param>
    /// <param name="rotationJitter">How much it may randomly differ, in radians. At 2π it is free.</param>
    /// <param name="colourJitter">How much it may randomly darken, 0–1.</param>
    /// <param name="patternCount">How many equal-width columns the pattern holds.</param>
    /// <param name="alphaCoverage">Whether the pattern's alpha carries its coverage rather than its luminance.</param>
    /// <param name="maskThreshold">The mask value below which an instance is dropped.</param>
    /// <param name="sizeMapAmount">How much of the size map reaches an instance's scale, 0–1.</param>
    /// <param name="rotationMapAmount">How much of the rotation map is added, in radians.</param>
    /// <param name="accumulation">How overlapping instances combine.</param>
    /// <param name="opacity">How much of each instance reaches the result.</param>
    /// <returns>The op.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A grid, a count or a scale is not positive.</exception>
    /// <exception cref="ArgumentException">The instance would reach further than the kernel searches.</exception>
    public static TextureOp TileSampler(
        int output,
        int pattern,
        int mask,
        int sizeMap,
        int rotationMap,
        int gridX = 8,
        int gridY = 8,
        float scale = 1f,
        float scaleJitter = 0f,
        float positionJitter = 0f,
        float rotation = 0f,
        float rotationJitter = 0f,
        float colourJitter = 0f,
        int patternCount = 1,
        bool alphaCoverage = false,
        float maskThreshold = 0f,
        float sizeMapAmount = 0f,
        float rotationMapAmount = 0f,
        TexturePlacementAccumulation accumulation = TexturePlacementAccumulation.Max,
        float opacity = 1f
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gridX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gridY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(patternCount);

        // ⚠ A refusal and not a clamp. The kernel's own `clamp(…, 1, MaxSearch)` is a ceiling on a
        // loop, which is a correctness property — a NaN in `scale` must not be a dispatch that never
        // ends. What it cannot be is the *answer*: an instance the search does not reach is drawn
        // with a straight edge along a cell boundary, which looks like a pattern and not like a bug.
        var reach = (scale * HalfDiagonal) + (Math.Clamp(positionJitter, 0f, 1f) * 0.5f);

        if (reach > MaxSearch) {
            var cells = reach.ToString("0.###", CultureInfo.InvariantCulture);

            throw new ArgumentException(
                $"A scale of {scale} with a position jitter of {positionJitter} puts an instance {cells} cells "
                + $"from its own, and '{TileSamplerKernel}' searches {MaxSearch}. Past that an instance is cut off "
                + "along a cell boundary, which reads as a pattern rather than as a defect. Use a coarser grid, or "
                + "several ops blended.",
                nameof(scale)
            );
        }

        return new() {
            Kernel = TileSamplerKernel,
            Output = output,
            Inputs = [pattern, mask, sizeMap, rotationMap],
            Parameters = [
                new("gridX", gridX),
                new("gridY", gridY),
                new("scale", scale),
                new("scaleJitter", scaleJitter),
                new("positionJitter", positionJitter),
                new("rotation", rotation),
                new("rotationJitter", rotationJitter),
                new("colourJitter", colourJitter),
                new("patternCount", patternCount),
                new("alphaCoverage", alphaCoverage ? 1f : 0f),
                new("maskThreshold", maskThreshold),
                new("sizeMapAmount", sizeMapAmount),
                new("rotationMapAmount", rotationMapAmount),
                new("accumulation", (float)(int)accumulation),
                new("opacity", opacity)
            ]
        };
    }

    /// <summary>The same grid with no maps: the pattern is bound to all four inputs and none is read.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="pattern">The stamp, bound four times.</param>
    /// <param name="gridX">How many cells across.</param>
    /// <param name="gridY">How many cells down.</param>
    /// <param name="scale">An instance's size as a fraction of its cell.</param>
    /// <param name="scaleJitter">How much an instance may randomly shrink, 0–1.</param>
    /// <param name="positionJitter">How far it may randomly move inside its cell, 0–1.</param>
    /// <param name="rotation">The rotation every instance starts at, in radians.</param>
    /// <param name="rotationJitter">How much it may randomly differ, in radians. At 2π it is free.</param>
    /// <param name="colourJitter">How much it may randomly darken, 0–1.</param>
    /// <param name="patternCount">How many equal-width columns the pattern holds.</param>
    /// <param name="alphaCoverage">Whether the pattern's alpha carries its coverage.</param>
    /// <param name="accumulation">How overlapping instances combine.</param>
    /// <param name="opacity">How much of each instance reaches the result.</param>
    /// <returns>The op.</returns>
    /// <remarks>
    ///     ⚠ <b>The mask threshold is zero and the two map amounts are zero, which is what makes
    ///     binding the pattern into the map slots harmless.</b> A coverage is never below zero, so the
    ///     cull never fires; a map amount of zero drops the map's value out of the arithmetic
    ///     entirely. The alternative — a white 1×1 external image every plan has to carry — is the
    ///     shape <c>Blend.rvn</c> refused for its mask, and it would be one here too.
    /// </remarks>
    public static TextureOp TileSampler(
        int output,
        int pattern,
        int gridX = 8,
        int gridY = 8,
        float scale = 1f,
        float scaleJitter = 0f,
        float positionJitter = 0f,
        float rotation = 0f,
        float rotationJitter = 0f,
        float colourJitter = 0f,
        int patternCount = 1,
        bool alphaCoverage = false,
        TexturePlacementAccumulation accumulation = TexturePlacementAccumulation.Max,
        float opacity = 1f
    ) =>
        TileSampler(
            output,
            pattern,
            pattern,
            pattern,
            pattern,
            gridX,
            gridY,
            scale,
            scaleJitter,
            positionJitter,
            rotation,
            rotationJitter,
            colourJitter,
            patternCount,
            alphaCoverage,
            accumulation: accumulation,
            opacity: opacity
        );

    /// <summary>A bounded free scatter of one pattern.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="pattern">The stamp, read as an atlas of <paramref name="patternCount" /> columns.</param>
    /// <param name="mask">Read at each instance's centre; under the threshold it is dropped.</param>
    /// <param name="sizeMap">Read at each instance's centre; shrinks it, under its own amount.</param>
    /// <param name="rotationMap">Read at each instance's centre; turns it, under its own amount.</param>
    /// <param name="placement">Where the instances go: red and green read as a signed offset.</param>
    /// <param name="count">How many instances, at most <see cref="MaxInstances" />.</param>
    /// <param name="scale">An instance's size as a fraction of the image.</param>
    /// <param name="scaleJitter">How much an instance may randomly shrink, 0–1.</param>
    /// <param name="rotation">The rotation every instance starts at, in radians.</param>
    /// <param name="rotationJitter">How much it may randomly differ, in radians. At 2π it is free.</param>
    /// <param name="colourJitter">How much it may randomly darken, 0–1.</param>
    /// <param name="patternCount">How many equal-width columns the pattern holds.</param>
    /// <param name="alphaCoverage">Whether the pattern's alpha carries its coverage.</param>
    /// <param name="maskThreshold">The mask value below which an instance is dropped.</param>
    /// <param name="sizeMapAmount">How much of the size map reaches an instance's scale, 0–1.</param>
    /// <param name="rotationMapAmount">How much of the rotation map is added, in radians.</param>
    /// <param name="placementAmount">How far the placement map may move an instance, in fractions of the image.</param>
    /// <param name="accumulation">How overlapping instances combine.</param>
    /// <param name="opacity">How much of each instance reaches the result.</param>
    /// <returns>The op.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The count is not positive, or the scale is not.</exception>
    /// <exception cref="ArgumentException">The count is past what the kernel walks.</exception>
    public static TextureOp Splatter(
        int output,
        int pattern,
        int mask,
        int sizeMap,
        int rotationMap,
        int placement,
        int count = 16,
        float scale = 0.25f,
        float scaleJitter = 0f,
        float rotation = 0f,
        float rotationJitter = 0f,
        float colourJitter = 0f,
        int patternCount = 1,
        bool alphaCoverage = false,
        float maskThreshold = 0f,
        float sizeMapAmount = 0f,
        float rotationMapAmount = 0f,
        float placementAmount = 0f,
        TexturePlacementAccumulation accumulation = TexturePlacementAccumulation.Max,
        float opacity = 1f
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(patternCount);

        // ⚠ Refused rather than truncated. A splatter given more instances than the loop walks would
        // draw the first 256 and no warning — a picture that is right in every respect except how
        // many things are in it, which is precisely the parameter the artist was turning.
        if (count > MaxInstances) {
            throw new ArgumentException(
                $"'{SplatterKernel}' places at most {MaxInstances} instances and this op asks for {count}. The "
                + "bound is what doc 48 § D7 buys by refusing FX-Map's recursion — the cost of the node is knowable "
                + "before it runs — so a graph that wants more places several ops and blends them.",
                nameof(count)
            );
        }

        return new() {
            Kernel = SplatterKernel,
            Output = output,
            Inputs = [pattern, mask, sizeMap, rotationMap, placement],
            Parameters = [
                new("count", count),
                new("scale", scale),
                new("scaleJitter", scaleJitter),
                new("rotation", rotation),
                new("rotationJitter", rotationJitter),
                new("colourJitter", colourJitter),
                new("patternCount", patternCount),
                new("alphaCoverage", alphaCoverage ? 1f : 0f),
                new("maskThreshold", maskThreshold),
                new("sizeMapAmount", sizeMapAmount),
                new("rotationMapAmount", rotationMapAmount),
                new("placementAmount", placementAmount),
                new("accumulation", (float)(int)accumulation),
                new("opacity", opacity)
            ]
        };
    }

    /// <summary>The same scatter with no maps: the pattern is bound to all five inputs and none is read.</summary>
    /// <param name="output">The image to write.</param>
    /// <param name="pattern">The stamp, bound five times.</param>
    /// <param name="count">How many instances.</param>
    /// <param name="scale">An instance's size as a fraction of the image.</param>
    /// <param name="scaleJitter">How much an instance may randomly shrink, 0–1.</param>
    /// <param name="rotation">The rotation every instance starts at, in radians.</param>
    /// <param name="rotationJitter">How much it may randomly differ, in radians. At 2π it is free.</param>
    /// <param name="colourJitter">How much it may randomly darken, 0–1.</param>
    /// <param name="patternCount">How many equal-width columns the pattern holds.</param>
    /// <param name="alphaCoverage">Whether the pattern's alpha carries its coverage.</param>
    /// <param name="accumulation">How overlapping instances combine.</param>
    /// <param name="opacity">How much of each instance reaches the result.</param>
    /// <returns>The op.</returns>
    public static TextureOp Splatter(
        int output,
        int pattern,
        int count = 16,
        float scale = 0.25f,
        float scaleJitter = 0f,
        float rotation = 0f,
        float rotationJitter = 0f,
        float colourJitter = 0f,
        int patternCount = 1,
        bool alphaCoverage = false,
        TexturePlacementAccumulation accumulation = TexturePlacementAccumulation.Max,
        float opacity = 1f
    ) =>
        Splatter(
            output,
            pattern,
            pattern,
            pattern,
            pattern,
            pattern,
            count,
            scale,
            scaleJitter,
            rotation,
            rotationJitter,
            colourJitter,
            patternCount,
            alphaCoverage,
            accumulation: accumulation,
            opacity: opacity
        );

    /// <summary>Every op this class can build, for a test that wants to walk them.</summary>
    /// <remarks>
    ///     ⚠ <b>Ask what a test over the builders prints on the day one is forgotten.</b> A theory with
    ///     an <c>InlineData</c> per builder passes silently when a third is added and not listed; the
    ///     parameter-agreement test walks this, so a builder reaches it by existing.
    /// </remarks>
    public static ImmutableArray<TextureOp> All { get; } = [TileSampler(0, 1), Splatter(0, 1)];
}
