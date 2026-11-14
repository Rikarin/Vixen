// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>
///     <see cref="TextureHistograms" />: a host's histogram of any image a plan computes, each lane against counts made
///     on the CPU — Patina #172's 2D view.
/// </summary>
/// <remarks>
///     A byte is a bin, as in <see cref="TextureHistogramDeviceTests" />: <c>floor(256 · b / 255)</c> is <c>b</c> for
///     every byte but 255, which shares the top bin with 254. Every lane holds a different picture, so a count that read
///     the wrong lane would count another histogram.
/// </remarks>
public class TextureHostHistogramDeviceTests(ITestOutputHelper output) {
    static readonly TextureHistogramLane[] Lanes = [
        TextureHistogramLane.Red,
        TextureHistogramLane.Green,
        TextureHistogramLane.Blue,
        TextureHistogramLane.Alpha,
        TextureHistogramLane.Luma
    ];

    static int Byte(int x, int y, int lane) =>
        lane switch {
            0 => x * x % 256,
            1 => (x * 7 + y) % 128,
            2 => 255 - y * 3 % 200,
            _ => (x * y + 11) % 256
        };

    [Theory]
    [InlineData(256, 256)]
    [InlineData(100, 37)]
    [InlineData(64, 512)]
    [InlineData(1024, 64)]
    public void Each_lane_is_counted_into_its_own_shares(int width, int height) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var picture = Picture(
            width,
            height,
            (x, y) => (Byte(x, y, 0) / 255f, Byte(x, y, 1) / 255f, Byte(x, y, 2) / 255f, Byte(x, y, 3) / 255f)
        );

        var (plan, image, uploads) = Compiled(device, picture, width, height);

        using (uploads) {
            var histogram = TextureHistograms.Append(plan, image, Lanes);

            Assert.Empty(histogram.Plan.Validate());
            Assert.Equal(histogram.Tables, histogram.Plan.Outputs);

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.EvaluateIncremental(histogram.Plan, uploads.Externals);

            double texels = width * height;

            for (var lane = 0; lane < 4; lane++) {
                var expected = new double[TextureHistograms.Bins];

                for (var y = 0; y < height; y++) {
                    for (var x = 0; x < width; x++) {
                        expected[Math.Min(Byte(x, y, lane), 255)] += 1 / texels;
                    }
                }

                var shares = TextureHistograms.Read(bake, histogram.Tables[lane]);

                for (var bin = 0; bin < TextureHistograms.Bins; bin++) {
                    Assert.True(
                        Math.Abs(expected[bin] - shares[bin]) < 1e-6,
                        $"Lane {lane}, bin {bin}: {shares[bin]} where the count is {expected[bin]}"
                    );
                }
            }

            AssertLuma(TextureHistograms.Read(bake, histogram.Tables[4]), width, height);
        }
    }

    /// <summary>A grey is shown on all three colour channels with an alpha of one, so that is what is counted.</summary>
    [Fact]
    public void A_grey_counts_its_one_channel_as_every_colour_and_alpha_as_one() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int Side = 128;
        var picture = Picture(Side, Side, (x, y) => (Byte(x, y, 0) / 255f, 0.5f, 0f, 0.25f));
        var (plan, image, uploads) = Compiled(device, picture, Side, Side, true);

        using (uploads) {
            Assert.Equal(TextureFormat.R32Float, plan.Images[image].Format);

            var histogram = TextureHistograms.Append(plan, image, Lanes);

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.EvaluateIncremental(histogram.Plan, uploads.Externals);

            var red = TextureHistograms.Read(bake, histogram.Tables[0]);

            Assert.True(red.Max() < 0.5f, "The instrument: a red all in one bin would match a constant.");

            foreach (var lane in (int[])[1, 2, 4]) {
                Assert.Equal(red, TextureHistograms.Read(bake, histogram.Tables[lane]));
            }

            Assert.Equal(1f, TextureHistograms.Read(bake, histogram.Tables[3])[255]);
        }
    }

    [Fact]
    public void An_image_no_op_writes_is_refused() {
        var plan = new TexturePlan {
            BaseWidth = 64, BaseHeight = 64, Images = [new(TextureFormat.Rgba8, 0, External: true)], Ops = []
        };

        Assert.Throws<ArgumentException>(() => TextureHistograms.Append(plan, 0, Lanes));
    }

    /// <summary>
    ///     Luma against the CPU in double, but for texels whose luma is within rounding of a bin's edge, where a float
    ///     may land either side: those are counted as either, and nothing else may differ.
    /// </summary>
    static void AssertLuma(float[] shares, int width, int height) {
        var expected = new double[TextureHistograms.Bins];
        var edge = 0d;
        double texels = width * height;

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var luma = 256 * (0.2126 * Byte(x, y, 0) + 0.7152 * Byte(x, y, 1) + 0.0722 * Byte(x, y, 2)) / 255;

                expected[Math.Clamp((int)Math.Floor(luma), 0, 255)] += 1 / texels;
                edge += Math.Abs(luma - Math.Round(luma)) < 1e-3 ? 1 / texels : 0;
            }
        }

        var apart = expected.Zip(shares, static (count, share) => Math.Abs(count - share)).Sum();

        Assert.True(apart <= 2 * edge + 1e-5, $"Luma is {apart} apart where {edge} sits on an edge");
        Assert.Equal(1f, shares.Sum(), 1e-4f);
    }

    /// <summary>A picture through <c>Source/Bitmap</c>, and a grey of its red if asked, into an Output at 32 bits.</summary>
    static (TexturePlan Plan, int Image, TextureUploads Uploads) Compiled(
        Vixen.Graphics.Vulkan.VulkanDevice device,
        byte[] picture,
        int width,
        int height,
        bool grey = false
    ) {
        NodeGraphModel graph = new();

        graph.Settings[TextureGraphSettings.BitDepth] = "32";

        var bitmap = graph.Add("Source/Bitmap");

        bitmap.SetText("Source", "picture0");
        bitmap.SetText("Filter", "Point");

        var sink = graph.Add("Output/Output");
        var shown = bitmap;

        if (grey) {
            shown = graph.Add("Colour/Grayscale");
            shown.SetValue("Weight R", 1f);
            shown.SetValue("Weight G", 0f);
            shown.SetValue("Weight B", 0f);
            graph.Connect(new(bitmap.Id, "Out"), new(shown.Id, "Input"));
        }

        sink.SetText("Usage", grey ? "height" : "baseColor");
        graph.Connect(new(shown.Id, "Out"), new(sink.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphCompiler compiler = new(registry) { BaseWidth = width, BaseHeight = height };
        var plan = compiler.Compile(graph).Value;
        var uploads = new TextureUploads(device);

        foreach (var owed in TextureGraphExternals.Upload(uploads, plan, compiler.Externals)) {
            uploads.Add(plan, owed.Image, width, height, picture);
        }

        return (plan, compiler.Outputs.Single().Image, uploads);
    }
}
