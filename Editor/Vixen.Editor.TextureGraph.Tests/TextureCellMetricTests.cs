// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Core.Imaging;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
using Vixen.Graphics.Vulkan;
using Xunit;

namespace Tests;

/// <summary>
///     <c>Worley</c>'s distance metric: three cell shapes rather than one, and three normalisations
///     that are derived from the metrics rather than tuned.
/// </summary>
/// <remarks>
///     <para>
///         <see href="https://github.com/Rikarin/Vixen/issues/1101">#1101</see>, found authoring
///         <c>Patterns/Cells</c>. A cellular pattern could only ever be round-ish, because
///         <c>Worley</c> measured <c>length(…)</c> and the node exposed nothing to change it — and
///         square cells and diamond cells are most of what a cellular pattern library is for.
///     </para>
///     <para>
///         ⚠ <b>The device assertion is a per-texel inequality, and it is the one thing a picture of
///         cells can be checked against without a golden.</b> Chebyshev ≤ euclidean ≤ manhattan holds
///         for <em>every</em> displacement by the definition of the three metrics; it survives taking
///         a minimum over the nine candidates (the k-th smallest of a pointwise-smaller sequence is
///         smaller), and it survives the <c>saturate</c>, so it holds channel by channel over the
///         whole image on both F1 and F2. ⚠ <b>What it needs is that the normalisation is divided
///         back out first</b>, since the three scales differ on purpose — which is exactly the thing
///         a test comparing the three pictures naively would get wrong and call a bug.
///     </para>
///     <para>
///         ⚠ <b>Ask what this says if the metric never reached the shader.</b> Three identical
///         pictures satisfy a chain of ≤ perfectly. So the inequality is paired with a strictness
///         check: the three must actually differ, over a good fraction of the image, or the ordering
///         is being satisfied by a uniform that never arrived.
///     </para>
/// </remarks>
public class TextureCellMetricTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>How many cells across the test image. Small enough that a cell is many texels wide.</summary>
    const float Scale = 6f;

    /// <summary>A <c>const val</c> or a <c>return</c> of a bare literal in the kernel.</summary>
    static readonly Regex Returned = new(@"return\s+(-?\d+(?:\.\d+)?)f\b");

    /// <summary>What <c>Noise.rvn</c>'s <c>Normalisation</c> hands back, by metric index.</summary>
    /// <returns>Three scales, in <see cref="TextureCellMetric" /> order.</returns>
    /// <remarks>
    ///     ⚠ <b>Read out of the kernel rather than written here</b>, so that the derivation below is a
    ///     comparison between the kernel and arithmetic rather than between two copies of the same
    ///     three numbers.
    /// </remarks>
    static float[] Normalisations() {
        var source = TextureKernels.Source("Noise");
        var at = source.IndexOf("func Normalisation()", StringComparison.Ordinal);

        Assert.True(at >= 0, "`Noise.rvn` has no `func Normalisation()` — the kernel moved under this test");

        var open = source.IndexOf('{', at);
        var close = source.IndexOf("\n    }", open, StringComparison.Ordinal);

        Assert.True(close > open, "`Normalisation`'s body has no closing brace at this file's indentation");

        float[] returned = [
            .. Returned
                .Matches(source[open..close])
                .Select(match => float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
        ];

        // The instrument. A body that parsed to nothing would make every comparison below a claim
        // about an empty sequence, which is what a vacuous pass looks like here.
        Assert.Equal(3, returned.Length);

        // The chain tests 1 then 2 and falls through to 0, so the last literal is euclidean's.
        return [returned[2], returned[0], returned[1]];
    }

    /// <summary>Each metric's normalisation is its own bound's share of euclidean's.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The claim the kernel makes is that these three are <em>derived</em>, and this is
    ///         what makes that checkable.</b> A candidate's offset has each component strictly inside
    ///         ±2 cells over a 3×3 search, so the bound is 2√2 under euclidean, 2 under chebyshev and
    ///         4 under manhattan. Euclidean has always reported in units of two cells — a half — so
    ///         each of the other two is that half times the ratio of the bounds, which is what keeps
    ///         every metric at the same fraction of its own headroom.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Euclidean is asserted <em>exactly</em>, and that is the compatibility half.</b>
    ///         Every worley already baked was scaled by a literal 0.5; a normalisation that computed
    ///         the same number to six places would move every existing picture by a hair, and a bake
    ///         is a file.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Each_metric_is_normalised_by_its_own_bound_on_the_search() {
        var scales = Normalisations();

        // The bound each metric can report over a 3×3 search whose candidate offsets are strictly
        // inside ±2 cells on each axis.
        const double Euclidean = 2d * 1.4142135623730951d;
        const double Chebyshev = 2d;
        const double Manhattan = 4d;

        Assert.Equal(0.5f, scales[(int)TextureCellMetric.Euclidean]);

        Assert.Equal(0.5d * Euclidean / Chebyshev, scales[(int)TextureCellMetric.Chebyshev], 6);
        Assert.Equal(0.5d * Euclidean / Manhattan, scales[(int)TextureCellMetric.Manhattan], 6);

        // ⚠ And the sentinel has to be above all three, or the first candidate does not always
        // replace it and F2 can come back as the sentinel itself — which is a white plateau on the
        // channel a cell border is computed from. It read 4, which is manhattan's bound exactly.
        Assert.Contains("const val WorleyFar: float = 8f", TextureKernels.Source("Noise"), StringComparison.Ordinal);
    }

    /// <summary>The raw distance a cell reports rises with the metric, at every texel.</summary>
    /// <param name="channel">0 for F1, 1 for F2.</param>
    /// <remarks>
    ///     ⚠ <b>Both channels, because they answer different questions and only one of them is what a
    ///     border is made of.</b> <c>Patterns/Cells</c> computes its joint as F2 − F1, so a metric
    ///     that changed F1 and left F2 alone would draw a plausible pattern of the wrong shape.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_cell_is_no_further_under_a_smaller_metric_than_under_a_larger_one(int channel) {
        var scales = Normalisations();

        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var chebyshev = Cells(device, TextureCellMetric.Chebyshev);
        var euclidean = Cells(device, TextureCellMetric.Euclidean);
        var manhattan = Cells(device, TextureCellMetric.Manhattan);

        var apart = 0;

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var near = Raw(chebyshev, x, y, channel, scales[(int)TextureCellMetric.Chebyshev]);
                var round = Raw(euclidean, x, y, channel, scales[(int)TextureCellMetric.Euclidean]);
                var far = Raw(manhattan, x, y, channel, scales[(int)TextureCellMetric.Manhattan]);

                // One 8-bit step, divided back out of the largest of the three scales — the whole of
                // the slack this needs, because the inequality itself is exact.
                const float Step = 1f / 255f / 0.35355339f;

                Assert.True(
                    near <= round + Step,
                    $"chebyshev reads {near} at ({x}, {y}) where euclidean reads {round}, and the "
                    + "chebyshev distance of a displacement is never the larger of the two"
                );

                Assert.True(
                    round <= far + Step,
                    $"euclidean reads {round} at ({x}, {y}) where manhattan reads {far}, and the "
                    + "euclidean distance of a displacement is never the larger of the two"
                );

                if (Math.Abs(near - far) > 4f * Step) {
                    apart++;
                }
            }
        }

        // ⚠ The instrument, and without it this whole file is green on three identical pictures: a
        // `metric` uniform that never reached the shader satisfies every ≤ above. A quarter of the
        // image is far below what the three actually differ over and far above what quantisation
        // could invent.
        Assert.True(
            apart > Side * Side / 4,
            $"only {apart} of {Side * Side} texels tell chebyshev from manhattan apart, so these are "
            + "the same picture three times and the ordering above proves nothing"
        );
    }

    /// <summary>One worley field, baked at one metric.</summary>
    static Bitmap Cells(VulkanDevice device, TextureCellMetric metric) {
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 41823,
            Images = [new(TextureFormat.Rgba8)],
            Ops = [TextureSources.Noise(0, TextureNoiseBasis.Worley, Scale, metric: metric)],
            Outputs = [0]
        };

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan);

        return bake.Read(0);
    }

    /// <summary>One texel's distance with its metric's normalisation divided back out.</summary>
    static float Raw(Bitmap picture, int x, int y, int channel, float normalisation) =>
        TextureKernelHarness.At(picture, x, y, channel) / 255f / normalisation;
}
