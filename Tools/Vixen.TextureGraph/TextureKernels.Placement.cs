// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Globalization;

namespace Vixen.TextureGraph;

/// <summary>The maps and the further patterns a <c>TileSampler</c> op may read, each null when none is wired.</summary>
/// <param name="Displacement">Read at each instance's centre; red and green about a half move it, in cells.</param>
/// <param name="DisplacementAmount">How far a fully deflected map moves an instance, in cells.</param>
/// <param name="Vector">Read at each instance's centre; red and green about a half turn it to face along them.</param>
/// <param name="VectorAmount">How much of that turn is taken, 0–1.</param>
/// <param name="Colour">Read at each instance's centre; multiplies its colour.</param>
/// <param name="ColourAmount">How much of the map's colour is taken, 0–1.</param>
/// <param name="Index">Read at each instance's centre; chooses its pattern instead of the seed.</param>
/// <param name="Patterns">Up to three more patterns an instance may be drawn from.</param>
/// <remarks>
///     Patina #170. A map that is null is bound as the pattern and its amount is written as zero, which is the
///     arrangement the first four inputs already had: the slot is never read.
/// </remarks>
internal readonly record struct TileSamplerMaps(
    int? Displacement = null,
    float DisplacementAmount = 0f,
    int? Vector = null,
    float VectorAmount = 0f,
    int? Colour = null,
    float ColourAmount = 0f,
    int? Index = null,
    ImmutableArray<int> Patterns = default
);

/// <summary>What each instance of a placement kernel draws.</summary>
/// <remarks>
///     The numbers are the contract with <c>output</c> in both placement kernels. Patina #170: the per-instance
///     data a tint, a mask from a range of instances or a ramp across each instance is made of.
/// </remarks>
internal enum TexturePlacementOutput {
    /// <summary>The pattern itself, as it always drew.</summary>
    Pattern = 0,

    /// <summary>One random grey per instance, shaped by the pattern's coverage.</summary>
    Random = 1,

    /// <summary>One random colour per instance, whose red is <see cref="Random" />'s grey.</summary>
    Colour = 2,

    /// <summary>The instance's number over the count, centred in its share: <c>(i + ½) / n</c>.</summary>
    Index = 3,

    /// <summary>Where in its own square a texel is, in red and green.</summary>
    Uv = 4
}

/// <summary>Where a splatter puts its instances.</summary>
/// <remarks>The numbers are the contract with <c>Shaders/Splatter.rvn</c>'s <c>distribution</c>.</remarks>
internal enum TextureSplatterDistribution {
    /// <summary>Anywhere, each by its own two random numbers.</summary>
    Scatter = 0,

    /// <summary>Evenly round a circle about the image's centre.</summary>
    Ring = 1
}

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

    /// <summary>How many patterns a <c>TileSampler</c> op binds beside its first.</summary>
    public const int MaxExtraPatterns = 3;

    /// <summary>The most instances one <c>Splatter</c> op may place.</summary>
    /// <remarks>Duplicated from <c>Shaders/Splatter.rvn</c>'s <c>MaxInstances</c>, and asserted equal.</remarks>
    public const int MaxInstances = 256;

    /// <summary>The most instances a <c>Placement/Splatter</c> node may place, as a chain of ops.</summary>
    /// <remarks>
    ///     ⚠ <b>Sixteen batches, each one <see cref="MaxInstances" /> instances and one dispatch</b> — Patina
    ///     #170 asked for the 256 to be raised or removed. A batch is still bounded by the kernel's loop, so
    ///     what this ceiling buys is a cost a reader can multiply: at most sixteen dispatches of what one cost
    ///     before. A count past it is refused, for the reason <see cref="MaxInstances" /> was.
    /// </remarks>
    public const int MaxBatchedInstances = 16 * MaxInstances;

    /// <summary>How many ops a splatter of so many instances is.</summary>
    /// <param name="count">The instances.</param>
    /// <returns>One per <see cref="MaxInstances" />, and at least one.</returns>
    public static int SplatterBatches(int count) => Math.Max(1, (count + MaxInstances - 1) / MaxInstances);

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
    /// <param name="draws">What each instance draws: the pattern or one of its per-instance numbers.</param>
    /// <param name="rowOffset">How far odd rows are shifted along x, as a fraction of a cell.</param>
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
        float opacity = 1f,
        TexturePlacementOutput draws = TexturePlacementOutput.Pattern,
        float rowOffset = 0f,
        TileSamplerMaps maps = default
    ) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gridX);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(gridY);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scale);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(patternCount);

        // ⚠ A refusal and not a clamp. The kernel's own `clamp(…, 1, MaxSearch)` is a ceiling on a
        // loop, which is a correctness property — a NaN in `scale` must not be a dispatch that never
        // ends. What it cannot be is the *answer*: an instance the search does not reach is drawn
        // with a straight edge along a cell boundary, which looks like a pattern and not like a bug.
        var displacement = maps.Displacement is null ? 0f : Math.Max(maps.DisplacementAmount, 0f);
        var reach = (scale * HalfDiagonal) + (Math.Clamp(positionJitter, 0f, 1f) * 0.5f) + Math.Clamp(rowOffset, 0f, 1f)
            + displacement;

        if (reach > MaxSearch) {
            var cells = reach.ToString("0.###", CultureInfo.InvariantCulture);

            throw new ArgumentException(
                $"A scale of {scale} with a position jitter of {positionJitter} and a displacement of {displacement} "
                + $"puts an instance {cells} cells "
                + $"from its own, and '{TileSamplerKernel}' searches {MaxSearch}. Past that an instance is cut off "
                + "along a cell boundary, which reads as a pattern rather than as a defect. Use a coarser grid, or "
                + "several ops blended.",
                nameof(scale)
            );
        }

        var extra = maps.Patterns.IsDefault ? [] : maps.Patterns;

        if (extra.Length > MaxExtraPatterns) {
            throw new ArgumentException(
                $"{extra.Length} more patterns were given and '{TileSamplerKernel}' binds {MaxExtraPatterns}.",
                nameof(maps)
            );
        }

        return new() {
            Kernel = TileSamplerKernel,
            Output = output,
            Inputs = [
                pattern,
                mask,
                sizeMap,
                rotationMap,
                maps.Displacement ?? pattern,
                maps.Vector ?? pattern,
                maps.Colour ?? pattern,
                maps.Index ?? pattern,
                extra.Length > 0 ? extra[0] : pattern,
                extra.Length > 1 ? extra[1] : pattern,
                extra.Length > 2 ? extra[2] : pattern
            ],

            // ⚠ #867. Every one of the four is read through **its own** extent — `Stamp` divides by
            // `pattern.GetDimensions(0)` and `At` by the map's — so stamping a 64² pattern over a
            // 1024² output is this node's ordinary use and not the smeared corner
            // `TexturePlan.Check` cautions about. `OtherExtentInputs` is left empty for the same
            // reason: the list would be every index, and empty already says every index.
            //
            // ⚠ **The caution it silences is one no compiled graph can raise, and #867's own
            // justification claimed otherwise** — [#900](https://github.com/Rikarin/Vixen/issues/900).
            // "the node an artist reaches for first was the one that complained" is false:
            // `TileSamplerNode.Compile` reads every input through `TextureEmitter.Read`, which does
            // `Rescale(arrived, level)` before the op is built, so an op this compiler emits has its
            // inputs at the output's own extent by construction and the two sizes `Check` compares
            // are equal whatever this flag says. What the flag is worth is what
            // `TextureOp.ReadsOtherExtents` says of `AutoLevels`: a **hand-built** plan is the
            // reachable case, and M7, `TextureGraphPreview.Base` and most of this project build one.
            ReadsOtherExtents = true,
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
                new("opacity", opacity),
                new("output", (float)draws),
                new("rowOffset", rowOffset),
                new("displacementAmount", displacement),
                new("vectorAmount", maps.Vector is null ? 0f : maps.VectorAmount),
                new("colourAmount", maps.Colour is null ? 0f : maps.ColourAmount),
                new("indexMap", maps.Index is null ? 0f : 1f),
                new("patternInputs", 1 + extra.Length)
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
    /// <param name="draws">What each instance draws: the pattern or one of its per-instance numbers.</param>
    /// <param name="distribution">Where the instances go: a scatter, or evenly round a ring.</param>
    /// <param name="ringRadius">The ring's radius, as a fraction of the image.</param>
    /// <param name="followRing">Whether an instance on a ring is turned by its angle round it.</param>
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
        float opacity = 1f,
        TexturePlacementOutput draws = TexturePlacementOutput.Pattern,
        TextureSplatterDistribution distribution = TextureSplatterDistribution.Scatter,
        float ringRadius = 0.3f,
        bool followRing = false
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

        return SplatterBatch(
            output,
            [pattern, mask, sizeMap, rotationMap, placement, pattern],
            0,
            false,
            [
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
                new("opacity", opacity),
                new("output", (float)draws),
                new("distribution", (float)distribution),
                new("ringRadius", ringRadius),
                new("followRing", followRing ? 1f : 0f)
            ]
        );
    }

    /// <summary>A splatter of up to <see cref="MaxBatchedInstances" />, as a chain of batches.</summary>
    /// <param name="output">The image the last batch writes.</param>
    /// <param name="scratch">
    ///     One image per batch but the last, the output's size and format: what each batch hands the next.
    /// </param>
    /// <param name="single">
    ///     The whole splatter as one op would be, with its count — built by
    ///     <see cref="Splatter(int, int, int, int, int, int, int, float, float, float, float, float, int, bool, float, float, float, float, TexturePlacementAccumulation, float, TexturePlacementOutput, TextureSplatterDistribution, float, bool)" />
    ///     at a count it accepts and given the real one here.
    /// </param>
    /// <param name="count">How many instances in all.</param>
    /// <returns>The ops, in evaluation order.</returns>
    /// <exception cref="ArgumentException">The count is past the ceiling, or the scratch list is the wrong length.</exception>
    public static ImmutableArray<TextureOp> SplatterChain(int output, ImmutableArray<int> scratch, TextureOp single, int count) {
        ArgumentNullException.ThrowIfNull(single);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (count > MaxBatchedInstances) {
            throw new ArgumentException(
                $"'{SplatterKernel}' places at most {MaxBatchedInstances} instances, in {MaxBatchedInstances / MaxInstances} "
                + $"batches of {MaxInstances}, and this asks for {count}. Each batch is a dispatch over the whole image, so "
                + "the cost of the node is knowable before it runs; a graph that wants more blends several.",
                nameof(count)
            );
        }

        var batches = SplatterBatches(count);

        if (scratch.Length != batches - 1) {
            throw new ArgumentException(
                $"A splatter of {count} is {batches} batches, and so {batches - 1} scratch images; this names {scratch.Length}.",
                nameof(scratch)
            );
        }

        var parameters = single.Parameters.Where(static parameter => parameter.Name is not ("count" or "first" or "batched"))
            .Append(new("count", count))
            .ToImmutableArray();

        var ops = ImmutableArray.CreateBuilder<TextureOp>(batches);

        for (var batch = 0; batch < batches; batch++) {
            ops.Add(
                SplatterBatch(
                    batch == batches - 1 ? output : scratch[batch],
                    [.. single.Inputs.Take(5), batch == 0 ? single.Inputs[0] : scratch[batch - 1]],
                    batch * MaxInstances,
                    batch > 0,
                    parameters
                )
            );
        }

        return ops.ToImmutable();
    }

    /// <summary>One batch of a splatter: the kernel's whole parameter set, with where this batch starts.</summary>
    static TextureOp SplatterBatch(
        int output,
        ImmutableArray<int> inputs,
        int first,
        bool batched,
        IEnumerable<TextureParameter> parameters
    ) =>
        new() {
            Kernel = SplatterKernel,
            Output = output,
            Inputs = inputs,

            // ⚠ #867, `TileSampler`'s reason and one more input. All five are read through their own
            // extent, so **every** input of this op is extent-agnostic — which is why
            // `OtherExtentInputs` stays empty here rather than listing 0 through 4: empty means every
            // input, and a list that has to be kept in step with `Inputs` is a second thing to get
            // wrong. See `TextureOp.OtherExtentInputs` for the op where the distinction is real.
            //
            // ⚠ And #900 applies here word for word: the caution this silences is one no compiled
            // graph can raise, because `SplatterNode.Compile` reads through `TextureEmitter.Read` and
            // that rescales to the node's level before the op is built. A hand-built plan is the
            // reachable case, exactly as it is for `AutoLevels`.
            ReadsOtherExtents = true,
            Parameters = [.. parameters, new("first", first), new("batched", batched ? 1f : 0f)]
        };

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
