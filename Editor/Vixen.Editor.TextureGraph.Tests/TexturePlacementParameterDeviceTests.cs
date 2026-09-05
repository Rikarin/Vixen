// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>
///     The § 4.7 placement uniforms nothing asserted, each against a closed form only it satisfies.
/// </summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/709">#709</a>: nine of
///         <c>TileSampler</c>'s fifteen uniforms had no behavioural assertion anywhere</b> — a kernel
///         that read none of them passed the whole suite. <c>TexturePlacementDeviceTests</c> covers
///         the grid, the scale, the position jitter, the alpha coverage, the mask threshold and the
///         accumulation mode; what is here is the rest: the scale jitter, the rotation, the rotation
///         jitter, the colour jitter, the pattern count, the two map amounts and the opacity.
///     </para>
///     <para>
///         ⚠ <b>The absence is what let the pattern atlas ship broken.</b> No test set
///         <c>patternCount</c> above one, so neither the atlas indexing nor the bleed across a column
///         boundary was ever exercised — the feature was unproven rather than merely unguarded, which
///         is why the first test here is the one that would have caught it.
///     </para>
///     <para>
///         <b>Closed forms and not pictures</b>, doc 48 § D3 and the file above's own method. Under
///         <c>add</c> the mean of the result is the instance count times the area of one instance
///         times the pattern's mean, so every modulation that multiplies a size or a colour has an
///         exact number waiting for it: a scale jitter of one leaves <c>E[(1 − U)²] = ⅓</c> of the
///         coverage, a colour jitter of one leaves <c>E[1 − U/1] = ½</c> of the <em>brightness</em>
///         and all of the coverage, and a size map at a half with its amount at one leaves a quarter.
///         ⚠ The three are different numbers, which is what stops one of them standing in for another.
///     </para>
/// </remarks>
public class TexturePlacementParameterDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>The two greys of the two-column atlas, and the value a bled edge would read.</summary>
    const byte Bright = 204;

    const byte Dim = 51;

    /// <summary>
    ///     ⚠ A stamp reads its <b>own</b> column of the atlas and no texel of a neighbouring one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect half of <a href="https://github.com/Rikarin/Vixen/issues/709">#709</a>.
    ///         </b> Both kernels built the atlas coordinate as
    ///         <c>(slot + local.x + 0.5) / patternCount</c> and sampled it with the bilinear helper
    ///         that clamps to the whole image — so at <c>|local.x| = 0.5</c>, which is the stamp's own
    ///         edge, the two taps sat either side of the boundary between column <c>slot</c> and its
    ///         neighbour and one texel of a different pattern bled in.
    ///     </para>
    ///     <para>
    ///         <b>The fixture makes that arithmetic instead of visual.</b> One instance filling the
    ///         image, an atlas of two flat columns, and no jitter of any kind: every texel of the
    ///         result is one column's constant, whichever column the seed drew. ⚠ A quarter of a
    ///         neighbouring column mixed into the edge reads 166 against 204 — thirty-eight counts,
    ///         and nothing else in the plan can produce a third value. It also fails the other way, on
    ///         a kernel that ignored <c>patternCount</c> and stretched the whole atlas across the
    ///         stamp: then half the picture is each grey.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both kernels, because <c>Splatter.rvn</c> carried a copy of the line.</b> A
    ///         single instance at full scale covers every texel of a toroidal field, which is the
    ///         property <c>TexturePlacementDeviceTests</c> already pins, so the same assertion reads
    ///         the same way there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Several seeds, and the first version of this with one seed was itself a test that
    ///         could not fail.</b> A single instance draws a single slot, and the slot the seed
    ///         happened to draw was the <em>last</em> column — for which "read the whole atlas instead
    ///         of one column" is indistinguishable from the truth, because both taps then clamp to the
    ///         image's last texel and the stamp comes out flat anyway. So the sabotage that widens a
    ///         column to the whole image left it green. What is asserted instead is over enough seeds
    ///         that <b>both</b> columns are drawn: each bake is one flat colour, and across the bakes
    ///         the two colours are the two the atlas holds.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_stamp_reads_only_its_own_column_of_the_pattern_atlas(bool splatter) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (texture, staging) = TextureKernelHarness.Upload(device, TwoColumns(Side), Side, Side);

        try {
            using var evaluator = new TexturePlanEvaluator(device);

            var drawn = new HashSet<int>();

            foreach (var seed in (uint[])[4409u, 5501u, 7717u, 9013u, 41823u, 90101u]) {
                var op = splatter
                    ? TexturePlacement.Splatter(1, 0, count: 1, scale: 1f, patternCount: 2, alphaCoverage: true)
                    : TexturePlacement.TileSampler(
                        1,
                        0,
                        gridX: 1,
                        gridY: 1,
                        scale: 1f,
                        patternCount: 2,
                        alphaCoverage: true
                    );

                var plan = new TexturePlan {
                    BaseWidth = Side,
                    BaseHeight = Side,
                    Seed = seed,
                    Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
                    Ops = [op],
                    Outputs = [1]
                };

                Assert.Empty(plan.Validate());

                using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

                var picture = bake.Read(1);
                var middle = TextureKernelHarness.At(picture, Side / 2, Side / 2, 0);

                output.WriteLine($"seed {seed} drew column value {middle}");

                Assert.True(
                    middle == Bright || middle == Dim,
                    $"the middle of the stamp reads {middle}, which is neither of the atlas's two columns "
                    + $"({Bright} and {Dim}) — so the stamp is not reading one column"
                );

                for (var y = 0; y < Side; y++) {
                    for (var x = 0; x < Side; x++) {
                        var value = TextureKernelHarness.At(picture, x, y, 0);

                        Assert.True(
                            value == middle,
                            $"seed {seed}: texel ({x}, {y}) reads {value} where the rest of the stamp reads "
                            + $"{middle}; a value between {Dim} and {Bright} is the neighbouring column bled "
                            + $"across the boundary, and the other column exactly is an atlas index that moved "
                            + $"({TextureKernelHarness.Adapter(device)})"
                        );
                    }
                }

                drawn.Add(middle);
            }

            // ⚠ The instrument check. Every assertion above is satisfied by a kernel that always
            // reads the last column, which is what a widened column degenerates to once both taps
            // clamp to the image's edge — so the seeds have to have reached both of them.
            Assert.Equal([Bright, Dim], drawn.Order().Reverse());
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ An instance overhanging the left border is the one its twin's <b>map</b> describes, not
    ///     the one the map's clamped edge does.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The claim <a href="https://github.com/Rikarin/Vixen/issues/709">#709</a> asked to
    ///         settle.</b> <c>TileSampler.rvn</c>'s header said flatly that the image is seamless when
    ///         repeated, "because the instance overhanging the left edge is the same instance found
    ///         again one period to the right". ⚠ <b>The <em>geometry</em> was, and the instance was
    ///         not.</b> The fold makes the outside cell carry the same id and therefore the same
    ///         jitters, so its square does sit exactly one period from its twin's — but its centre is
    ///         outside the image, and the mask, size and rotation maps are read there through a
    ///         bilinear tap that <em>clamps</em>. The two copies were modulated by different map
    ///         values, so a masked grid was cut differently at the two borders.
    ///     </para>
    ///     <para>
    ///         <b>The fixture turns that into one texel's worth of black or white.</b> Two cells
    ///         across, an instance 1.4 cells wide so it overhangs, and a mask that is black in the
    ///         left half of the image and white in the right. The right cell's instance passes the
    ///         cull; its copy one period to the left overhangs the left border and is the same
    ///         instance, so it has to be drawn too — and under the clamped lookup its centre read the
    ///         mask's leftmost texel, which is black, and it was culled. So <b>column 2 is lit when
    ///         the lookup wraps and black when it clamps</b>, while column 15, which no instance
    ///         reaches, stays black either way.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No total could see this.</b> Every closed form in this file and the one beside it
    ///         is a mean over identical stamps, and a mean cannot see <em>which</em> instances were
    ///         drawn — one cull at the left border traded for one at the right leaves it unmoved.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_instance_overhanging_the_border_reads_the_map_where_its_twin_is() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (texture, staging) = TextureKernelHarness.Upload(device, LeftDarkRightLight(Side), Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [
                    TextureSources.Uniform(1, 1f),
                    TexturePlacement.TileSampler(
                        2,
                        pattern: 1,
                        mask: 0,
                        sizeMap: 1,
                        rotationMap: 1,
                        gridX: 2,
                        gridY: 1,

                        // Wide enough to overhang by 0.2 of a cell and still inside the search radius
                        // the CPU allows: 1.4 × ½√2 is 0.99 cells.
                        scale: 1.4f,
                        maskThreshold: 0.5f
                    )
                ],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var picture = bake.Read(2);
            var row = Side / 2;

            var overhang = TextureKernelHarness.At(picture, 2, row, 0);
            var gap = TextureKernelHarness.At(picture, 15, row, 0);
            var body = TextureKernelHarness.At(picture, 40, row, 0);

            output.WriteLine($"overhang {overhang}, gap {gap}, body {body}");

            Assert.True(
                body > 250,
                $"the instance the mask passes reads {body} at column 40, so the fixture is not the fixture "
                + $"({TextureKernelHarness.Adapter(device)})"
            );

            Assert.Equal(0, gap);

            Assert.True(
                overhang > 250,
                $"column 2 reads {overhang}: the copy of the passing instance that overhangs the left border was "
                + $"not drawn, so its map lookup clamped to the image's edge instead of wrapping to where its "
                + $"twin is — and the grid is not seamless ({TextureKernelHarness.Adapter(device)})"
            );
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ A scale jitter of one leaves a <b>third</b> of the coverage, because it only ever
    ///     shrinks.
    /// </summary>
    /// <remarks>
    ///     An instance's size is multiplied by <c>1 − jitter·U</c> with <c>U</c> uniform, so its area
    ///     carries <c>E[(1 − U)²] = ⅓</c> — and the one-sidedness the kernel's header rests its search
    ///     radius on is exactly what makes that number a third rather than one. ⚠ A jitter that
    ///     <em>grew</em> an instance would read <c>E[(1 + U)²] = 7/3</c>, seven times the other
    ///     answer and off the far side of every tolerance here; a jitter that was ignored reads one.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0.25f)]
    [InlineData(1f, 0.0833f)]
    public void A_scale_jitter_only_ever_shrinks_and_a_full_one_leaves_a_third(float jitter, float expected) {
        using var device = TextureKernelHarness.Open();

        var mean = Coverage(
            device,
            TexturePlacement.TileSampler(
                1,
                0,
                gridX: 16,
                gridY: 16,
                scale: 0.5f,
                scaleJitter: jitter,
                accumulation: TexturePlacementAccumulation.Add
            ),
            0
        );

        // ⚠ Proportional and loose, because 256 cells of a random variable is a *sample*: the
        // coefficient of variation of (1 − U)² is 0.89, so the mean of 256 of them carries about 5.6%
        // of its own. The two candidates are three times apart and the wrong-signed one seven, so a
        // tenth is a tolerance that still refutes every alternative reading.
        Assert.True(
            Math.Abs(mean - expected) <= expected * 0.15f,
            $"a scale jitter of {jitter} covers {mean:0.0000} and the closed form is {expected} "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ A rotation of a quarter turn transposes the pattern exactly, and the rotation
    ///     <em>map</em> says the same thing in turns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The one assertion in this file that is an equality over every texel.</b> A 1×1 grid
    ///         at full scale maps the instance's local frame onto the image's own, so at a quarter
    ///         turn the sample points land on texel centres and the answer is the source read at
    ///         <c>(y, Side − 1 − x)</c> — no interpolation, no tolerance beyond the one count a
    ///         cosine of <c>π/2</c> that is not quite zero can move. ⚠ The <em>direction</em> is the
    ///         point: the other quarter turn is <c>(Side − 1 − y, x)</c>, an equally plausible
    ///         picture, and nothing else in the suite could tell them apart.
    ///     </para>
    ///     <para>
    ///         <b>The second row is <c>rotationMapAmount</c>, and it is the same picture.</b> The map
    ///         is read at the instance's centre and multiplied by a whole turn, so a uniform map of
    ///         0.25 at an amount of 1 is a quarter turn — which pins the unit (turns, not radians) and
    ///         the amount together against a number neither could reach alone.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_quarter_turn_transposes_the_pattern_whether_it_is_authored_or_mapped(bool mapped) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var source = TextureKernelHarness.Unique(Side);
        var (texture, staging) = TextureKernelHarness.Upload(device, source, Side, Side);

        try {
            var plan = new TexturePlan {
                BaseWidth = Side,
                BaseHeight = Side,
                Images = [
                    new(TextureFormat.Rgba8, External: true),
                    new(TextureFormat.Rgba16Float),
                    new(TextureFormat.Rgba8)
                ],
                Ops = [
                    // A quarter of a turn, for the row that asks the map for it.
                    TextureSources.Uniform(1, 0.25f),
                    TexturePlacement.TileSampler(
                        2,
                        pattern: 0,
                        mask: 1,
                        sizeMap: 1,
                        rotationMap: 1,
                        gridX: 1,
                        gridY: 1,
                        scale: 1f,
                        rotation: mapped ? 0f : MathF.PI / 2f,
                        alphaCoverage: true,
                        rotationMapAmount: mapped ? 1f : 0f
                    )
                ],
                Outputs = [2]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var picture = bake.Read(2);
            var expected = new Bitmap(Side, Side, source);

            for (var y = 0; y < Side; y++) {
                for (var x = 0; x < Side; x++) {
                    for (var channel = 0; channel < 3; channel++) {
                        var was = TextureKernelHarness.At(expected, y, Side - 1 - x, channel);
                        var now = TextureKernelHarness.At(picture, x, y, channel);

                        Assert.True(
                            Math.Abs(was - now) <= 1,
                            $"texel ({x}, {y}) channel {channel} reads {now} and a quarter turn clockwise owes "
                            + $"{was} — the source at ({y}, {Side - 1 - x}) — on "
                            + TextureKernelHarness.Adapter(device)
                        );
                    }
                }
            }
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ A rotation jitter turns instances without changing how much of the image they cover.
    /// </summary>
    /// <remarks>
    ///     <b>Both halves, because either alone is satisfied by the wrong kernel.</b> A rotation is an
    ///     area-preserving map, so the <c>add</c> total is the same number with the jitter and
    ///     without it — which a jitter wired into the <em>scale</em> by mistake would break. And the
    ///     picture has to actually move, which is what a kernel that dropped the uniform fails. ⚠ The
    ///     stamp is a column checkerboard rather than a flat fill: a rotated flat is the same picture,
    ///     so a flat pattern would make the second half unfalsifiable.
    /// </remarks>
    [Fact]
    public void A_rotation_jitter_turns_the_instances_without_changing_the_coverage() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (texture, staging) = TextureKernelHarness.Upload(device, TextureKernelHarness.Columns(Side), Side, Side);

        try {
            using var evaluator = new TexturePlanEvaluator(device);

            var still = Bake(0f);
            var spun = Bake(1f);
            var differences = 0;

            for (var y = 0; y < Side; y++) {
                for (var x = 0; x < Side; x++) {
                    if (TextureKernelHarness.At(still, x, y, 0) != TextureKernelHarness.At(spun, x, y, 0)) {
                        differences++;
                    }
                }
            }

            var before = Mean(still, 3);
            var after = Mean(spun, 3);

            output.WriteLine($"coverage {before:0.0000} → {after:0.0000}, {differences} texels moved");

            Assert.True(
                Math.Abs(before - after) <= before * 0.06f,
                $"a rotation covers {after:0.0000} against {before:0.0000} unrotated, and a turn preserves area "
                + $"({TextureKernelHarness.Adapter(device)})"
            );

            Assert.True(
                differences > Side * Side / 10,
                $"a full rotation jitter moved {differences} of {Side * Side} texels, which is not a kernel that "
                + $"read it ({TextureKernelHarness.Adapter(device)})"
            );

            return;

            Bitmap Bake(float jitter) {
                var plan = new TexturePlan {
                    BaseWidth = Side,
                    BaseHeight = Side,
                    Seed = 9013u,
                    Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba8)],
                    Ops = [
                        TexturePlacement.TileSampler(
                            1,
                            0,
                            gridX: 8,
                            gridY: 8,
                            scale: 0.5f,
                            rotationJitter: jitter,
                            alphaCoverage: true,
                            accumulation: TexturePlacementAccumulation.Add
                        )
                    ],
                    Outputs = [1]
                };

                Assert.Empty(plan.Validate());

                using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

                return bake.Read(1);
            }
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ A colour jitter darkens the colour and leaves the <b>coverage</b> alone, which is what
    ///     separates it from every other modulation here.
    /// </summary>
    /// <remarks>
    ///     The tint is <c>1 − jitter·U</c>, applied to the instance's colour and not to what
    ///     <c>Accumulate</c> weighs — so at a full jitter the red total halves, <c>E[1 − U] = ½</c>,
    ///     while the alpha total does not move at all. ⚠ <b>An opacity or a size map halves both</b>,
    ///     so reading one channel could not tell the three apart and reading the pair can.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0.25f)]
    [InlineData(1f, 0.125f)]
    public void A_colour_jitter_darkens_the_colour_and_not_the_coverage(float jitter, float expected) {
        using var device = TextureKernelHarness.Open();

        var op = TexturePlacement.TileSampler(
            1,
            0,
            gridX: 16,
            gridY: 16,
            scale: 0.5f,
            colourJitter: jitter,
            accumulation: TexturePlacementAccumulation.Add
        );

        var brightness = Coverage(device, op, 0);
        var covered = Coverage(device, op, 3);

        output.WriteLine($"colour jitter {jitter}: red {brightness:0.0000}, alpha {covered:0.0000}");

        Assert.True(
            Math.Abs(brightness - expected) <= expected * 0.1f,
            $"a colour jitter of {jitter} leaves {brightness:0.0000} of brightness and the closed form is "
            + $"{expected} ({TextureKernelHarness.Adapter(device)})"
        );

        Assert.True(
            Math.Abs(covered - 0.25f) <= 0.25f * 0.06f,
            $"a colour jitter of {jitter} left {covered:0.0000} of coverage, and a tint is not a size "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ A size map at a half, at full amount, quarters the coverage — and none of it is random.
    /// </summary>
    /// <remarks>
    ///     <c>mapped = 1 − amount·(1 − map)</c> multiplies the size, so the area carries its square:
    ///     at an amount of zero the map is not read at all and the coverage is <c>scale²</c>, and at
    ///     one a map of a half is a quarter of it. ⚠ <b>Every number here is exact</b> — the map is
    ///     uniform, so unlike the jitters above this has no sampling error and a six per cent
    ///     tolerance is the read-back's alone. A kernel that added the map rather than multiplying,
    ///     or that applied the amount to the area rather than to the size, misses both rows.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0.25f)]
    [InlineData(1f, 0.0625f)]
    public void A_size_map_multiplies_the_size_by_its_own_amount(float amount, float expected) {
        using var device = TextureKernelHarness.Open();

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TextureSources.Uniform(1, 0.5f),
                TexturePlacement.TileSampler(
                    2,
                    pattern: 0,
                    mask: 1,
                    sizeMap: 1,
                    rotationMap: 1,
                    gridX: 16,
                    gridY: 16,
                    scale: 0.5f,
                    sizeMapAmount: amount,
                    accumulation: TexturePlacementAccumulation.Add
                )
            ],
            Outputs = [2]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        var mean = Mean(bake.Read(2), 0);

        output.WriteLine($"size map amount {amount}: {mean:0.0000}");

        Assert.True(
            Math.Abs(mean - expected) <= expected * 0.06f,
            $"a size map of 0.5 at amount {amount} covers {mean:0.0000} and the closed form is {expected} "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>⚠ An opacity scales both what an instance contributes and how much it covers.</summary>
    /// <remarks>
    ///     The one modulation that is not a size: <c>Accumulate</c> multiplies the colour <em>and</em>
    ///     the weight by it, so under <c>add</c> both totals scale together — which is the pair that
    ///     separates it from <c>colourJitter</c> above, whose alpha does not move, and from a size
    ///     map, whose coverage falls as the <em>square</em>.
    /// </remarks>
    [Theory]
    [InlineData(1f, 0.25f)]
    [InlineData(0.5f, 0.125f)]
    public void An_opacity_scales_the_colour_and_the_coverage_together(float opacity, float expected) {
        using var device = TextureKernelHarness.Open();

        var op = TexturePlacement.TileSampler(
            1,
            0,
            gridX: 16,
            gridY: 16,
            scale: 0.5f,
            accumulation: TexturePlacementAccumulation.Add,
            opacity: opacity
        );

        var brightness = Coverage(device, op, 0);
        var covered = Coverage(device, op, 3);

        output.WriteLine($"opacity {opacity}: red {brightness:0.0000}, alpha {covered:0.0000}");

        Assert.True(
            Math.Abs(brightness - expected) <= expected * 0.06f,
            $"an opacity of {opacity} leaves {brightness:0.0000} and the closed form is {expected} "
            + $"({TextureKernelHarness.Adapter(device)})"
        );

        Assert.True(
            Math.Abs(covered - expected) <= expected * 0.06f,
            $"an opacity of {opacity} covers {covered:0.0000} and the closed form is {expected} "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>An atlas of two flat columns: the left one bright, the right one dim.</summary>
    /// <remarks>
    ///     <b>Flat columns rather than a picture</b>, so that "this texel came from that column" is an
    ///     equality against a constant and a boundary tap is a value neither column holds.
    /// </remarks>
    static byte[] TwoColumns(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var value = x < side / 2 ? Bright : Dim;

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>A mask that is black in the left half of the image and white in the right.</summary>
    static byte[] LeftDarkRightLight(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var value = (byte)(x < side / 2 ? 0 : 255);

                pixels[at] = value;
                pixels[at + 1] = value;
                pixels[at + 2] = value;
                pixels[at + 3] = 255;
            }
        }

        return pixels;
    }

    /// <summary>One placement op over a white pattern, as the mean of one channel.</summary>
    static float Coverage(VulkanDevice device, TextureOp op, int channel) {
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 6101u,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [TextureSources.Uniform(0, 1f), op],
            Outputs = [1]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        return Mean(bake.Read(1), channel);
    }

    /// <summary>The mean of one channel of a picture, in 0..1.</summary>
    static float Mean(Bitmap picture, int channel) {
        var total = 0L;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                total += TextureKernelHarness.At(picture, x, y, channel);
            }
        }

        return total / (float)(picture.Width * picture.Height * 255);
    }
}
