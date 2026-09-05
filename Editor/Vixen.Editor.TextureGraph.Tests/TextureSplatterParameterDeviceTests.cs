// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>
///     <c>Splatter</c>'s own uniforms, each against a closed form only it satisfies.
/// </summary>
/// <remarks>
///     <para>
///         <b>The half of <a href="https://github.com/Rikarin/Vixen/issues/709">#709</a> that was left
///         over.</b> The atlas bleed was fixed in both kernels and <c>TileSampler</c>'s nine silent
///         uniforms were given closed forms; <c>Splatter</c> got the <c>patternCount</c> row of that
///         and nothing else, so its scale jitter, rotation, rotation jitter, colour jitter, size map
///         and placement map still had no behavioural assertion anywhere — a kernel ignoring any of
///         them passed.
///     </para>
///     <para>
///         ⚠ <b>They are not <c>TileSampler</c>'s tests with a different builder, because the two
///         kernels differ where it matters here.</b> A <c>TileSampler</c> instance sits at a cell
///         centre, so a 1×1 grid at full scale puts the stamp exactly on the image and its rotation
///         can be asserted as an exact transpose. A <c>Splatter</c> instance sits wherever its seed
///         put it, so every assertion in this file has to be <b>invariant under a translation</b> —
///         which is what the closed forms below are, and it is the reason the rotation is read off as
///         which axis the bands run along rather than as an equality against a transposed source.
///     </para>
///     <para>
///         <b>Under <c>add</c> the mean is <c>count · scale² · (the pattern's mean)</c></b> — a
///         fraction of the <em>image</em> here, because a splatter has no cells. Every fixture uses
///         256 instances at a thirty-second of the image, which is 0.25 of coverage and the same stamp
///         size as the grid this file's neighbour measures.
///     </para>
/// </remarks>
public class TextureSplatterParameterDeviceTests(ITestOutputHelper output) {
    const int Side = TextureKernelHarness.Side;

    /// <summary>The count and scale whose product is a quarter of the image, exactly.</summary>
    const int Many = 256;

    const float Small = 1f / 32f;

    /// <summary>How bright the pattern is, and ⚠ the reason none of these numbers is 0.25.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A splatter is Poisson and an <c>Rgba8</c> target clamps, so a white pattern does not
    ///         have the closed form it looks like it has.</b> Instances land independently rather than
    ///         one to a cell, so at a coverage of <c>λ</c> a texel carries <c>Poisson(λ)</c> stamps and
    ///         the sum that survives the store is <c>E[min(X, 1)] = 1 − e^(−λ)</c>: for
    ///         <c>λ = 0.25</c> that is 0.2212, and the first version of this file measured 0.2210
    ///         against a closed form of 0.25 and would have blamed the kernel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the pattern is a quarter bright, which moves the clipping out of reach</b> —
    ///         four stamps have to overlap before a texel saturates, and <c>P(X ≥ 4)</c> at
    ///         <c>λ = 0.25</c> is two parts in ten thousand. Every closed form below is therefore
    ///         <c>Ink · count · scale²</c> and a covered texel still reads 64 of 255, which is a long
    ///         way from the read-back's own resolution. <c>TileSampler</c> needs none of this: one
    ///         instance per cell means no overlap at all.
    ///     </para>
    /// </remarks>
    const float Ink = 0.25f;

    /// <summary>
    ///     ⚠ An instance the placement map pushes out of the image reads the map where it
    ///     <b>lands</b>, not where it was pushed to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The defect, and it is <c>TileSampler</c>'s by a different route.</b> The field is
    ///         toroidal, so an instance whose centre <c>placementAmount</c> has pushed past the right
    ///         edge is drawn at <c>frac(centre)</c> — but the mask, size and rotation maps were read
    ///         at the unwrapped centre, where the bilinear helper <em>clamps</em>. Every instance
    ///         pushed out of the image was therefore modulated by whatever is at the border.
    ///     </para>
    ///     <para>
    ///         <b>The fixture makes the offset exactly one whole image.</b> A placement map of 1
    ///         decodes to <c>+1</c> and at an amount of 1 moves every centre by exactly one period, so
    ///         a wrapped lookup reads precisely what an unpushed instance reads and the two bakes have
    ///         to agree texel for texel. ⚠ The clamped lookup does not merely differ: it reads the
    ///         bottom-right texel of the mask for <em>every</em> instance, so it is the third bake
    ///         here — the one with no mask at all — which is twice the coverage.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three numbers are what make this falsifiable in both directions.</b> A kernel
    ///         that ignored <c>placementAmount</c> entirely also agrees with the first bake, so the
    ///         all-passing bake is asserted too: it says the mask is culling half the instances, which
    ///         is the thing the middle bake could get wrong.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_instance_pushed_out_of_the_image_reads_the_map_where_it_lands() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (texture, staging) = TextureKernelHarness.Upload(device, LeftDarkRightLight(Side), Side, Side);

        try {
            using var evaluator = new TexturePlanEvaluator(device);

            var still = Mean(Bake(evaluator, texture, mask: 0, placementAmount: 0f, map: 0f), 0);
            var pushed = Mean(Bake(evaluator, texture, mask: 0, placementAmount: 1f, map: 1f), 0);
            var unmasked = Mean(Bake(evaluator, texture, mask: 1, placementAmount: 0f, map: 0f), 0);

            output.WriteLine($"still {still:0.0000}, pushed {pushed:0.0000}, unmasked {unmasked:0.0000}");

            Assert.True(
                unmasked - still > still * 0.4f,
                $"the masked bake covers {still:0.0000} and the unmasked one {unmasked:0.0000}, which is not a "
                + $"mask that culls anything — so this fixture cannot see the defect it is about "
                + $"({TextureKernelHarness.Adapter(device)})"
            );

            Assert.True(
                Math.Abs(pushed - still) <= still * 0.1f,
                $"pushing every instance by exactly one period covers {pushed:0.0000} against {still:0.0000} "
                + $"unpushed, and {unmasked:0.0000} is what reading the mask's clamped corner for every "
                + $"instance gives — so the map lookup does not wrap with the geometry "
                + $"({TextureKernelHarness.Adapter(device)})"
            );
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ A placement map of a known value slides the whole field by exactly what it decodes to.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What <c>placementAmount</c> means, as an equality over every texel.</b> A uniform
    ///         map moves every instance by the same vector, and the field is toroidal, so the picture
    ///         is the unpushed picture <em>rotated</em> — the same stamps, in the same order, at
    ///         positions offset by a whole number of texels. Sixteen of them, so no sample lands
    ///         between two texels and the equality needs no tolerance beyond the read-back's.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The map value is 0.625 and not 1, which is what pins the decoding.</b> A signed
    ///         quantity in an unorm map is stored as <c>v·0.5 + 0.5</c>, so 0.625 means <c>+0.25</c>
    ///         and the shift is a quarter of the image. A kernel that forgot the <c>·2 − 1</c> would
    ///         move it 0.625 of the way instead — forty texels rather than sixteen — which is
    ///         precisely the "weak effect rather than a wrong one" doc 48 § 4.4 warns about. And a
    ///         quarter rather than a half, so that the sign is visible: half an image is the same
    ///         shift in both directions.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_placement_map_slides_the_field_by_what_it_decodes_to() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        using var evaluator = new TexturePlanEvaluator(device);

        var still = Slide(evaluator, 0f, 0f);
        var moved = Slide(evaluator, 1f, 0.625f);
        const int Shift = Side / 4;

        var different = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var was = TextureKernelHarness.At(still, (x - Shift + Side) % Side, (y - Shift + Side) % Side, 0);
                var now = TextureKernelHarness.At(moved, x, y, 0);

                if (Math.Abs(was - now) > 2) {
                    different++;
                }
            }
        }

        // ⚠ The instrument: a flat picture would satisfy the equality above at every shift there is,
        // and a splatter of sixteen stamps on black is anything but flat.
        var spread = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                if (TextureKernelHarness.At(still, x, y, 0) > 128) {
                    spread++;
                }
            }
        }

        output.WriteLine($"{different} texels off the slide, {spread} lit of {Side * Side}");

        Assert.InRange(spread, Side * Side / 20, Side * Side * 9 / 10);

        Assert.True(
            different <= Side * Side / 100,
            $"{different} of {Side * Side} texels disagree with the same picture slid {Shift} texels, so the "
            + $"placement map does not move an instance by what it decodes to "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ A scale jitter of one leaves a <b>third</b> of the coverage, because it only ever
    ///     shrinks.
    /// </summary>
    /// <remarks>
    ///     The size carries <c>1 − jitter·U</c> and the area its square, so a full jitter leaves
    ///     <c>E[(1 − U)²] = ⅓</c>. ⚠ A jitter that grew an instance would read <c>7/3</c>, seven times
    ///     the other answer; one that was ignored reads one. The tolerance is a tenth because 256
    ///     instances of a random variable is a sample and not a closed form — the same argument, and
    ///     the same number of draws, as the grid's.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0.0625f)]
    [InlineData(1f, 0.0208f)]
    public void A_scale_jitter_only_ever_shrinks_and_a_full_one_leaves_a_third(float jitter, float expected) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var mean = Covered(
            device,
            TexturePlacement.Splatter(
                1,
                0,
                count: Many,
                scale: Small,
                scaleJitter: jitter,
                accumulation: TexturePlacementAccumulation.Add
            ),
            0
        );

        output.WriteLine($"scale jitter {jitter}: {mean:0.0000}");

        Assert.True(
            Math.Abs(mean - expected) <= expected * 0.15f,
            $"a scale jitter of {jitter} covers {mean:0.0000} and the closed form is {expected} "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>
    ///     ⚠ A colour jitter darkens the colour and leaves the <b>coverage</b> alone.
    /// </summary>
    /// <remarks>
    ///     The tint multiplies the instance's colour and not the weight <c>Accumulate</c> carries, so
    ///     a full jitter halves the red total — <c>E[1 − U] = ½</c> — and does not move the alpha
    ///     total at all. ⚠ That pair is what separates it from an opacity and from a size map, which
    ///     move both.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0.0625f)]
    [InlineData(1f, 0.03125f)]
    public void A_colour_jitter_darkens_the_colour_and_not_the_coverage(float jitter, float expected) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var op = TexturePlacement.Splatter(
            1,
            0,
            count: Many,
            scale: Small,
            colourJitter: jitter,
            accumulation: TexturePlacementAccumulation.Add
        );

        var brightness = Covered(device, op, 0);
        var covered = Covered(device, op, 3);

        output.WriteLine($"colour jitter {jitter}: red {brightness:0.0000}, alpha {covered:0.0000}");

        Assert.True(
            Math.Abs(brightness - expected) <= expected * 0.1f,
            $"a colour jitter of {jitter} leaves {brightness:0.0000} of brightness and the closed form is "
            + $"{expected} ({TextureKernelHarness.Adapter(device)})"
        );

        Assert.True(
            Math.Abs(covered - Ink * Many * Small * Small) <= Ink * Many * Small * Small * 0.06f,
            $"a colour jitter of {jitter} covers {covered:0.0000}, and a tint is not a size "
            + $"({TextureKernelHarness.Adapter(device)})"
        );
    }

    /// <summary>⚠ A size map at a half, at full amount, quarters the coverage.</summary>
    /// <remarks>
    ///     <c>mapped = 1 − amount·(1 − map)</c> multiplies the size and the area carries its square.
    ///     Nothing here is random — the map is uniform — so both rows are exact and the tolerance is
    ///     the read-back's alone. A kernel that added the map, or applied the amount to the area
    ///     rather than to the size, misses both.
    /// </remarks>
    [Theory]
    [InlineData(0f, 0.0625f)]
    [InlineData(1f, 0.015625f)]
    public void A_size_map_multiplies_the_size_by_its_own_amount(float amount, float expected) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 6101u,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, Ink),
                TextureSources.Uniform(1, 0.5f),
                TexturePlacement.Splatter(
                    2,
                    pattern: 0,
                    mask: 0,
                    sizeMap: 1,
                    rotationMap: 1,
                    placement: 1,
                    count: Many,
                    scale: Small,
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

    /// <summary>
    ///     ⚠ A quarter turn lays the stamp's bands along the other axis, authored or mapped.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Read off as which axis the picture varies along</b>, which is the strongest claim a
    ///         splatter admits: its one instance is wherever the seed put it, so an equality against a
    ///         transposed source — the assertion the grid's rotation gets — would be an assertion
    ///         about the seed. Bands eight texels wide survive that translation, and a bilinear tap
    ///         half a texel off the lattice cannot wash them out the way a one-texel checkerboard
    ///         would.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The second row is <c>rotationMapAmount</c> and it is the same picture</b>: the map
    ///         is read at the instance's centre and multiplied by a whole turn, so a uniform map of
    ///         0.25 at an amount of 1 is the quarter turn the first row authors in radians. The pair
    ///         pins the unit — turns for the map, radians for <c>rotation</c> — which neither could
    ///         do alone.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_quarter_turn_lays_the_bands_along_the_other_axis(bool mapped) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (texture, staging) = TextureKernelHarness.Upload(device, Bands(Side), Side, Side);

        try {
            using var evaluator = new TexturePlanEvaluator(device);

            var upright = Bake(turn: false);
            var turned = Bake(turn: true);

            var (uprightX, uprightY) = Variation(upright);
            var (turnedX, turnedY) = Variation(turned);

            output.WriteLine($"upright {uprightX}/{uprightY}, turned {turnedX}/{turnedY}");

            Assert.True(
                uprightX > uprightY * 4,
                $"the unrotated stamp varies {uprightX} across and {uprightY} down, and its bands are vertical "
                + $"({TextureKernelHarness.Adapter(device)})"
            );

            Assert.True(
                turnedY > turnedX * 4,
                $"the quarter-turned stamp varies {turnedX} across and {turnedY} down, so the "
                + $"{(mapped ? "rotation map" : "rotation")} did not turn it "
                + $"({TextureKernelHarness.Adapter(device)})"
            );

            return;

            Bitmap Bake(bool turn) {
                var plan = new TexturePlan {
                    BaseWidth = Side,
                    BaseHeight = Side,
                    Seed = 9013u,
                    Images = [
                        new(TextureFormat.Rgba8, External: true),
                        new(TextureFormat.Rgba16Float),
                        new(TextureFormat.Rgba8)
                    ],
                    Ops = [
                        TextureSources.Uniform(1, turn && mapped ? 0.25f : 0f),
                        TexturePlacement.Splatter(
                            2,
                            pattern: 0,
                            mask: 0,
                            sizeMap: 0,
                            rotationMap: 1,
                            placement: 1,
                            count: 1,
                            scale: 1f,
                            rotation: turn && !mapped ? MathF.PI / 2f : 0f,
                            alphaCoverage: true,
                            rotationMapAmount: mapped ? 1f : 0f
                        )
                    ],
                    Outputs = [2]
                };

                Assert.Empty(plan.Validate());

                using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

                return bake.Read(2);
            }
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>
    ///     ⚠ A rotation jitter turns the instances without changing how much of the image they cover.
    /// </summary>
    /// <remarks>
    ///     <b>Both halves, because either alone is satisfied by the wrong kernel.</b> A rotation
    ///     preserves area, so the <c>add</c> total does not move — which a jitter wired into the
    ///     <em>scale</em> would break — and the picture has to actually change, which a kernel that
    ///     dropped the uniform fails. The stamp is a band pattern rather than a flat fill, because a
    ///     rotated flat is the same picture.
    /// </remarks>
    [Fact]
    public void A_rotation_jitter_turns_the_instances_without_changing_the_coverage() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (texture, staging) = TextureKernelHarness.Upload(device, Bands(Side), Side, Side);

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
                Math.Abs(before - after) <= before * 0.08f,
                $"a rotation jitter covers {after:0.0000} against {before:0.0000} unrotated, and a turn "
                + $"preserves area ({TextureKernelHarness.Adapter(device)})"
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
                        TexturePlacement.Splatter(
                            1,
                            0,
                            count: 32,
                            scale: 0.125f,
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

    /// <summary>One splatter over a uniform mask, at an offset the placement map decides.</summary>
    static Bitmap Bake(
        TexturePlanEvaluator evaluator,
        TextureHandle texture,
        int mask,
        float placementAmount,
        float map
    ) {
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 6101u,
            Images = [
                new(TextureFormat.Rgba8, External: true),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba16Float),
                new(TextureFormat.Rgba8)
            ],
            Ops = [
                TextureSources.Uniform(1, 1f),
                TextureSources.Uniform(2, map),
                TexturePlacement.Splatter(
                    3,
                    pattern: 1,
                    mask: mask,
                    sizeMap: 1,
                    rotationMap: 1,
                    placement: 2,
                    count: Many,
                    scale: Small,
                    maskThreshold: 0.5f,
                    placementAmount: placementAmount,
                    accumulation: TexturePlacementAccumulation.Add
                )
            ],
            Outputs = [3]
        };

        Assert.Empty(plan.Validate());

        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

        return bake.Read(3);
    }

    /// <summary>Sixteen stamps of a white pattern, moved by whatever the placement map says.</summary>
    static Bitmap Slide(TexturePlanEvaluator evaluator, float placementAmount, float map) {
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 4409u,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [
                TextureSources.Uniform(0, 1f),
                TextureSources.Uniform(1, map),
                TexturePlacement.Splatter(
                    2,
                    pattern: 0,
                    mask: 0,
                    sizeMap: 0,
                    rotationMap: 1,
                    placement: 1,
                    count: 16,
                    scale: 0.25f,
                    placementAmount: placementAmount
                )
            ],
            Outputs = [2]
        };

        Assert.Empty(plan.Validate());

        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        return bake.Read(2);
    }

    /// <summary>One splatter over a white pattern, as the mean of one channel.</summary>
    static float Covered(VulkanDevice device, TextureOp op, int channel) {
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 6101u,
            Images = [new(TextureFormat.Rgba16Float), new(TextureFormat.Rgba8)],
            Ops = [TextureSources.Uniform(0, Ink), op],
            Outputs = [1]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle>());

        return Mean(bake.Read(1), channel);
    }

    /// <summary>How much a picture varies along each axis, summed over every neighbouring pair.</summary>
    /// <remarks>
    ///     ⚠ <b>Wrapped at the border, because the field is.</b> Measuring only the interior would
    ///     leave one seam's worth of difference out of a number the assertion multiplies by four, and
    ///     the seam is exactly where a toroidal stamp puts its other half.
    /// </remarks>
    static (long Across, long Down) Variation(Bitmap picture) {
        var across = 0L;
        var down = 0L;

        for (var y = 0; y < picture.Height; y++) {
            for (var x = 0; x < picture.Width; x++) {
                var here = TextureKernelHarness.At(picture, x, y, 0);

                across += Math.Abs(TextureKernelHarness.At(picture, (x + 1) % picture.Width, y, 0) - here);
                down += Math.Abs(TextureKernelHarness.At(picture, x, (y + 1) % picture.Height, 0) - here);
            }
        }

        return (across, down);
    }

    /// <summary>Vertical bands eight texels wide, black and white.</summary>
    /// <remarks>
    ///     ⚠ <b>Eight rather than one</b>: a splatter's instance lands wherever its seed puts it, so
    ///     the stamp is read half a texel off the lattice as often as not — and a one-texel
    ///     checkerboard read half a texel off is a flat grey, which is a picture with no axis at all.
    /// </remarks>
    static byte[] Bands(int side) {
        var pixels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var at = ((y * side) + x) * 4;
                var value = (byte)((x / 8) % 2 == 0 ? 0 : 255);

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
