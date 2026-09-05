// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>Doc 48 § 4.7's two placement kernels, on a real device, against closed forms.</summary>
/// <remarks>
///     <para>
///         <b>The closed form both kernels are read off is a total, and it is exact.</b> Under
///         <c>add</c> the mean of the result is the number of instances times the area one instance
///         covers times the pattern's own mean — a sum of identical stamps has no variance however
///         they landed, so this is arithmetic and not a statistic. For <c>TileSampler</c> that is
///         <c>scale²</c> <em>whatever the grid</em>, because a scale is a fraction of a cell; for
///         <c>Splatter</c> it is <c>count · scale²</c>, because a scale is a fraction of the image.
///     </para>
///     <para>
///         ⚠ <b>And it is only exact because neither kernel clips at the border.</b> Both wrap — the
///         grid folds its cell index, the splatter wraps its offset into ±½ — so the total is a test
///         of the wrap as much as of the placement. Deleting the wrap leaves a picture that looks
///         entirely reasonable and a mean a few per cent low, which is the half a number can see.
///     </para>
///     <para>
///         ⚠ <b>The strongest assertion here is not a total but an equality.</b> A one-by-one grid at
///         full scale is a <em>copy of the pattern</em>, texel for texel and channel for channel over
///         an image where no two texels are alike — which pins the coordinate frame, the bilinear
///         tap's half-texel offset and the sub-sample count all at once, none of which a mean can see.
///     </para>
///     <para>
///         <b>No CPU twin</b> — doc 48 § D3. Nothing here re-implements a hash or a placement in C#;
///         what is asserted is what the arithmetic must total to.
///     </para>
/// </remarks>
public class TexturePlacementDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>
    ///     ⚠ A one-by-one grid at full scale is a copy of the pattern, and every part of that sentence
    ///     is load-bearing.
    /// </summary>
    /// <remarks>
    ///     One cell is the whole image and an instance at <c>scale</c> 1 fills its cell, so the
    ///     instance's local coordinate is the image's own — the bilinear tap lands exactly on a texel
    ///     centre and the derived sub-sample count collapses to one. Any half-texel error in the
    ///     frame, a flipped y, a pattern atlas indexed off by one or a supersample taken in the wrong
    ///     space breaks this on the first texel, over 4 096 of them where no two are alike.
    /// </remarks>
    [Fact]
    public void A_one_by_one_grid_at_full_scale_is_a_copy_of_the_pattern() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
                Ops = [
                    // ⚠ Coverage from the alpha, so that the alpha channel survives as itself. Read
                    // from the luminance instead — the default, which is right for a grey stamp —
                    // this would be a copy of three channels and a luminance in the fourth.
                    TexturePlacement.TileSampler(1, 0, gridX: 1, gridY: 1, alphaCoverage: true)
                ],
                Outputs = [1]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            TextureKernelHarness.AssertSame(
                new(Side, Side, source),
                bake.Read(1),
                4,
                $"a 1×1 tile sampler on {TextureKernelHarness.Adapter(device)}"
            );
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ The coverage of a grid is <c>scale²</c> and does not depend on the grid at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is what "a scale is a fraction of its cell" means, expressed as a number: four
    ///         times as many cells at the same scale is four times as many instances at a quarter of
    ///         the area each, and the total is unchanged. A kernel whose scale was a fraction of the
    ///         <em>image</em> instead would give a quarter of the coverage at the finer grid and a
    ///         picture that still looks like a scatter.
    ///     </para>
    ///     <para>
    ///         And halving the scale quarters it, which is the area oracle this repository prefers to
    ///         eyeballing: an assertion of the form "this must halve" cannot be satisfied by a kernel
    ///         that draws something plausible.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(4, 0.5f, 0.25f)]
    [InlineData(8, 0.5f, 0.25f)]
    [InlineData(16, 0.5f, 0.25f)]
    [InlineData(4, 0.25f, 0.0625f)]
    [InlineData(8, 0.25f, 0.0625f)]
    public void The_coverage_of_a_grid_is_the_square_of_the_scale(int grid, float scale, float expected) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TexturePlacement.TileSampler(
                    1,
                    0,
                    gridX: grid,
                    gridY: grid,
                    scale: scale,
                    accumulation: TexturePlacementAccumulation.Add
                )
            ],
            Outputs = [1]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        var mean = Mean(bake.Read(1));

        // Proportional, for the reason the splatter's own total gives.
        Assert.True(
            Math.Abs(mean - expected) <= expected * 0.04f,
            $"a {grid}×{grid} grid at scale {scale} covers {mean:0.0000} and {expected} is the square of the "
            + $"scale ({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ A splatter's coverage is the count times the area of one instance, exactly.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The claim doc 48 § D7 makes about refusing FX-Map is that the cost of the node is
    ///         knowable before it runs. This is the same property from the other side: the
    ///         <em>output</em> is knowable too, because a bounded count of identical stamps sums to a
    ///         number no arrangement of them can change.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is only true because the field is toroidal.</b> Without the wrap, every
    ///         instance within half its own width of an edge loses the part that hangs off, and the
    ///         mean drops by roughly the perimeter's share — a few per cent at these sizes, invisible
    ///         in the picture and plain in this number.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is a closed form only while the accumulated sum stays inside the output's
    ///         range, which is why every row here expects well under a quarter.</b> A white stamp
    ///         added onto another white stamp is 2, the unorm store clips it to 1, and the total comes
    ///         out low — 16 instances of an eighth of the image read 0.2386 against a closed form of
    ///         0.25, which is a real 4.6% and not a tolerance. That is a property of the encoding and
    ///         not of the placement, so it is kept out of the rows rather than absorbed into them.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(4, 0.125f, 0.0625f)]
    [InlineData(8, 0.125f, 0.125f)]
    [InlineData(16, 0.0625f, 0.0625f)]
    [InlineData(8, 0.0625f, 0.03125f)]
    public void The_coverage_of_a_splatter_is_its_count_times_one_instance(int count, float scale, float expected) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 7717u,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TexturePlacement.Splatter(
                    1,
                    0,
                    count,
                    scale,
                    accumulation: TexturePlacementAccumulation.Add
                )
            ],
            Outputs = [1]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        var mean = Mean(bake.Read(1));

        // ⚠ **Proportional and not absolute.** Every way this total can be wrong — an instance
        // clipped at the border, a stamp drawn at the wrong size, a sub-sample budget that
        // under-counts an edge — costs a *fraction* of it, so an absolute tolerance is strict at the
        // top of the theory and vacuous at the bottom.
        Assert.True(
            Math.Abs(mean - expected) <= expected * 0.04f,
            $"{count} instances at scale {scale} cover {mean:0.0000} and {expected} is the count times the area of "
            + $"one ({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ One instance at full scale covers <b>every</b> texel, because the field wraps.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Written because a sabotage left the totals above green.</b> Deleting the toroidal
    ///         wrap from <c>Splatter.rvn</c> — one line — moved
    ///         <see cref="The_coverage_of_a_splatter_is_its_count_times_one_instance" /> by more than
    ///         its tolerance in exactly <em>one</em> of its four rows: how much a scatter loses at the
    ///         border depends on where its instances happened to land, and at four instances of an
    ///         eighth of the image the seed had put none of them across an edge. A closed form whose
    ///         sensitivity depends on the sample is a closed form only some of the time.
    ///     </para>
    ///     <para>
    ///         This one does not depend on the sample at all. An instance as wide as the image covers
    ///         the image wherever its centre is — that is what "toroidal" means — so the answer is
    ///         white, every texel of it, for every seed. Without the wrap it is a white square
    ///         somewhere and black around it, and the assertion is an equality rather than a total.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_full_scale_splatter_covers_every_texel_because_the_field_wraps() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 5501u,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TexturePlacement.Splatter(1, 0, count: 1, scale: 1f)
            ],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        TextureKernelHarness.AssertSame(
            new(Side, Side, TextureKernelHarness.Solid(Side, 255, 255, 255, 255)),
            bake.Read(1),
            3,
            $"one full-scale instance on {TextureKernelHarness.Adapter(device)}"
        );
    }

    /// <summary>
    ///     ⚠ A fully jittered grid still tiles the image, because the cell index wraps before it is
    ///     hashed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Written because a sabotage left the entire suite green.</b> Deleting the body of
    ///         <c>TileSampler.Fold</c> — the two modulos that fold a cell index into the grid — changed
    ///         nothing in 441 assertions. Every closed form above is a <em>total</em>, and a total
    ///         cannot see the fold: without it each cell still holds exactly one instance and still
    ///         contributes exactly its own area. What the fold decides is <em>which</em> instance the
    ///         cells outside the grid hold, and that is only visible where one of them overlaps the
    ///         image.
    ///     </para>
    ///     <para>
    ///         So the property is asserted where it bites, and ⚠ <b>the grid has to be one cell</b>.
    ///         The first version of this test used a 2×2 grid and went red against the honest kernel:
    ///         a fully jittered grid does <em>not</em> cover its interior, because two adjacent cells
    ///         draw independently-jittered squares that do not abut and the gap between them is real.
    ///         The fold is not about neighbours; it is about the <em>period</em>. With one cell, every
    ///         neighbour <em>is</em> the period, so the fold makes them the same instance, the copies
    ///         abut exactly, and the image is white. Without it the cell beyond the border is a
    ///         differently-jittered copy and the gap it leaves is black.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Max and not add.</b> Under <c>add</c> this is white either way — the sum over a
    ///         cell is one instance's area whether the copies line up or not, which is exactly the
    ///         blindness this test exists to cover.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_fully_jittered_grid_still_tiles_because_the_cell_index_wraps() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 3301u,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TexturePlacement.TileSampler(1, 0, gridX: 1, gridY: 1, scale: 1f, positionJitter: 1f)
            ],
            Outputs = [1]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        TextureKernelHarness.AssertSame(
            new(Side, Side, TextureKernelHarness.Solid(Side, 255, 255, 255, 255)),
            bake.Read(1),
            3,
            $"a jittered 1x1 grid at full scale on {TextureKernelHarness.Adapter(device)}"
        );
    }

    /// <summary>The three accumulation modes give three numbers, and each is only its own.</summary>
    /// <remarks>
    ///     ⚠ <b>Two instances at full scale cover every texel twice</b>, because the field wraps — so
    ///     the whole image is the answer to "what do two overlapping stamps of 0.4 grey do". Max keeps
    ///     0.4; add makes 0.8; blend composites 0.4 over 0.4 at a weight that is the stamp's own
    ///     coverage, <c>lerp(lerp(0, 0.4, 0.4), 0.4, 0.4)</c>, which is 0.256. Nothing about a
    ///     renumbering of <see cref="TexturePlacementAccumulation" /> would be visible without this:
    ///     all three are perfectly plausible fields of stamps.
    /// </remarks>
    [Theory]
    [InlineData((int)TexturePlacementAccumulation.Max, 102)]
    [InlineData((int)TexturePlacementAccumulation.Add, 204)]
    [InlineData((int)TexturePlacementAccumulation.Blend, 65)]
    public void An_accumulation_mode_folds_two_full_scale_instances_its_own_way(int mode, int expected) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 0.4f),
                TexturePlacement.Splatter(
                    1,
                    0,
                    count: 2,
                    scale: 1f,
                    accumulation: (TexturePlacementAccumulation)mode
                )
            ],
            Outputs = [1]
        };

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        var picture = bake.Read(1);
        var actual = TextureKernelHarness.At(picture, 20, 44, 0);

        Assert.True(
            Math.Abs(actual - expected) <= 2,
            $"accumulation {mode} of two 0.4 stamps is {actual} and the closed form is {expected} "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ The same plan places the same instances twice, which is what makes a golden possible at
    ///     all.
    /// </summary>
    /// <remarks>
    ///     Doc 48 § D5: a procedural texture whose output changes between runs is not a source asset.
    ///     The seed is the plan's, mixed with the op's index on the CPU by
    ///     <c>TexturePlan.SeedFor</c> — so this is an assertion about the whole path from that method
    ///     to the hash in the kernel, and it is an equality over every texel.
    /// </remarks>
    [Fact]
    public void The_same_plan_places_the_same_instances_twice() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = Scatter(41823u);

        using var evaluator = new TexturePlanEvaluator(device);
        using var first = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());
        var once = first.Read(1);

        using var second = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        TextureKernelHarness.AssertSame(
            once,
            second.Read(1),
            4,
            $"the same plan twice on {TextureKernelHarness.Adapter(device)}"
        );
    }

    /// <summary>And a different plan seed places different instances, so the seed reaches the kernel.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument check for the test above.</b> A kernel that ignored its <c>seed</c>
    ///     entirely — the uniform renamed, the member dropped, the evaluator's own
    ///     <c>SeedFor</c> branch never taken — would make every plan identical, and "the same plan
    ///     twice agrees" would be the loudest possible pass.
    /// </remarks>
    [Fact]
    public void A_different_seed_places_different_instances() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        using var evaluator = new TexturePlanEvaluator(device);
        using var first = evaluator.Evaluate(Scatter(41823u), new Dictionary<int, TextureHandle>());
        var once = first.Read(1);

        using var second = evaluator.Evaluate(Scatter(90101u), new Dictionary<int, TextureHandle>());
        var twice = second.Read(1);

        var differences = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (TextureKernelHarness.At(once, x, y, 0) != TextureKernelHarness.At(twice, x, y, 0)) {
                    differences++;
                }
            }
        }

        Assert.True(
            differences > Side * Side / 20,
            $"two seeds moved {differences} of {Side * Side} texels, which is not a scatter that read its seed "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>An instance the mask culls is not drawn, and one it passes is.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the behavioural half of the binding-order claim.</b>
    ///     <c>TexturePlacementKernelTests</c> says the mask is declared second and the evaluator binds
    ///     an op's inputs positionally; what would happen if the two disagreed is that a kernel would
    ///     cull by whichever image the plan named second and produce a scatter — so the cull is
    ///     exercised here against an image whose value is known.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f, 255)]
    public void An_instance_below_the_mask_threshold_is_not_drawn(float mask, int expected) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TextureSources.Uniform(1, mask),
                TexturePlacement.TileSampler(
                    2,
                    pattern: 0,
                    mask: 1,
                    sizeMap: 1,
                    rotationMap: 1,
                    gridX: 1,
                    gridY: 1,
                    maskThreshold: 0.5f
                )
            ],
            Outputs = [2]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        Assert.Equal(expected, TextureKernelHarness.At(bake.Read(2), 31, 17, 0));
    }

    /// <summary>A scatter of small stamps, which two seeds have to disagree about.</summary>
    static TexturePlan Scatter(uint seed) =>
        new() {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = seed,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TexturePlacement.Splatter(1, 0, count: 12, scale: 0.2f)
            ],
            Outputs = [1]
        };

    /// <summary>The mean of a picture's red channel, in 0..1.</summary>
    static float Mean(Bitmap picture) {
        var total = 0L;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                total += TextureKernelHarness.At(picture, x, y, 0);
            }
        }

        return total / (float)(picture.Width * picture.Height * 255);
    }
}
