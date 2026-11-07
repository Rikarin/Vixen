// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Graphics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     How far a <c>Flood Fill</c> and a <c>Distance</c> reach: across an island of any width at the
///     node's defaults, past 2048 texels, and with a word when a budget ran out.
/// </summary>
public class TextureFloodReachDeviceTests(ITestOutputHelper output) {
    const int CellsPerSide = 4;

    /// <summary>
    ///     Rikarin/Patina#98's first claim: a cell of a Checker is wider than the default budget in
    ///     texels, and the box each texel reports has to be the whole cell all the same.
    /// </summary>
    /// <remarks>
    ///     <b>A closed form.</b> Scale 4 cuts the image into four cells a side, so the <c>Size</c>
    ///     output of every white cell is a quarter of the image whatever the resolution. ⚠ The
    ///     resolutions are the second claim: iterations that do not scale halve the reach every time
    ///     the graph doubles, which is why this runs at a bake above and below the base.
    /// </remarks>
    [Theory]
    [InlineData(1024, 0)]
    [InlineData(512, -1)]
    [InlineData(1024, 1)]
    public void The_defaults_settle_islands_far_wider_than_the_budget(int authored, int bake) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        var (compiler, plan, read) = Checker(authored, bake);

        Assert.Empty(plan.Validate());

        using var evaluator = new TexturePlanEvaluator(device);
        using var evaluated = evaluator.Evaluate(plan);

        var picture = evaluated.Read(read);
        var cell = picture.Width / CellsPerSide;
        var white = 0;

        for (var row = 0; row < CellsPerSide; row++) {
            for (var column = 0; column < CellsPerSide; column++) {
                var (x, y) = ((column * cell) + (cell / 2), (row * cell) + (cell / 2));

                if (TextureKernelHarness.At(picture, x, y, 2) == 0) {
                    continue;
                }

                white++;
                Assert.InRange(TextureKernelHarness.At(picture, x, y, 0), 62, 65);
                Assert.InRange(TextureKernelHarness.At(picture, x, y, 1), 62, 65);
            }
        }

        Assert.Equal(CellsPerSide * CellsPerSide / 2, white);
        Assert.Empty(compiler.Checks.Where(check => check.Failed(evaluated)));
    }

    /// <summary>
    ///     Rikarin/Patina#98 and Rikarin/Vixen#1529: two islands one texel apart, past where a half
    ///     float is exact on the integers, are two islands, and a graph at 4096 gets that from the node.
    /// </summary>
    /// <remarks>
    ///     <b>Long and thin so that a device run stays cheap</b>: what the ceiling was about is the
    ///     extent, so 4096×8 meets it at the cost of a 256×256 image. 4001 and 4003 are odd, and a half
    ///     rounds both to 4000 and 4004, so the old record could not have written these boxes at all.
    /// </remarks>
    [Fact]
    public void Two_islands_past_2048_keep_their_own_boxes() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int Width = 4096;
        const int Height = 8;

        var mask = Mask(Width, Height, (4001, 2), (4003, 2));
        var (texture, staging) = TextureKernelHarness.Upload(device, mask, Width, Height);

        try {
            const int Budget = 4;

            var images = ImmutableArray.CreateBuilder<TextureImage>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var pass = 0; pass < Budget; pass++) {
                images.Add(new(TextureFormat.Rgba32Float));
            }

            var read = images.Count;

            images.Add(new(TextureFormat.Rgba32Float));

            var plan = new TexturePlan {
                BaseWidth = Width,
                BaseHeight = Height,
                Images = images.ToImmutable(),
                Ops = TextureAnalysis.FloodFill(
                    read,
                    0,
                    [.. Enumerable.Range(1, Budget)],
                    Width,
                    Height,
                    TextureFloodOutput.BoundingBox
                ),
                Outputs = [read]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var boxes = MemoryMarshal.Cast<byte, float>(bake.ReadRaw(read).Texels);

            foreach (var x in (int[])[4001, 4003]) {
                var at = ((2 * Width) + x) * 4;

                Assert.Equal(x / (float)Width, boxes[at]);
                Assert.Equal(x / (float)Width, boxes[at + 2]);
            }
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>The node at 4096, which used to be refused against its mask.</summary>
    [Fact]
    public void A_graph_past_2048_has_a_flood_fill() {
        var (_, plan, read) = Checker(4096, 0);

        Assert.Empty(plan.Validate());
        Assert.Equal(4096, plan.SizeOf(read).X);
        Assert.All(
            plan.Ops.Where(static op => op.Kernel == "FloodBounds"),
            op => Assert.Equal(TextureFormat.Rgba32Float, plan.Images[op.Output].Format)
        );
    }

    /// <summary>
    ///     Rikarin/Vixen#1529's other half: a distance further than a half float can name, measured.
    /// </summary>
    /// <remarks>
    ///     <b>The closed form is the node's own</b>: from one lit texel at the left end of a long thin
    ///     image, <c>value × maxDistance × extent</c> is the distance in texels. 3001 is past the old
    ///     ceiling and odd, so an offset quantised to even texels misses it by one.
    /// </remarks>
    [Fact]
    public void A_distance_past_2048_texels_is_measured() {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        const int Width = 4096;
        const int Height = 8;

        var mask = Mask(Width, Height, (0, 2));
        var (texture, staging) = TextureKernelHarness.Upload(device, mask, Width, Height);

        try {
            var dispatches = TextureAnalysis.FloodDispatches(Width, Height);
            var images = ImmutableArray.CreateBuilder<TextureImage>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var pass = 0; pass < dispatches; pass++) {
                images.Add(new(TextureFormat.Rgba32Float));
            }

            var read = images.Count;

            images.Add(new(TextureFormat.Rgba32Float));

            var plan = new TexturePlan {
                BaseWidth = Width,
                BaseHeight = Height,
                Images = images.ToImmutable(),
                Ops = TextureAnalysis.Distance(
                    read,
                    0,
                    [.. Enumerable.Range(1, dispatches)],
                    Width,
                    Height,
                    TextureDistanceMode.Outside,
                    1f
                ),
                Outputs = [read]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            var field = MemoryMarshal.Cast<byte, float>(bake.ReadRaw(read).Texels);

            Assert.Equal(3001f, field[((2 * Width) + 3001) * 4] * Width, 0.01f);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>A budget that ran out says so against the node, and one that did not says nothing.</summary>
    /// <remarks>
    ///     <b>Both halves, because a check that cannot be false is worse than none.</b> The mask is
    ///     noise cut at a threshold, whose islands turn often enough that the fewest iterations a node
    ///     takes cannot settle it, and the most can.
    /// </remarks>
    [Theory]
    [InlineData(FloodFillBudget.Fewest, true)]
    [InlineData(FloodFillBudget.Most, false)]
    public void A_budget_that_ran_out_is_reported_against_the_node(FloodFillBudget budget, bool truncated) {
        using var device = TextureKernelHarness.Open();

        output.WriteLine($"adapter: {TextureKernelHarness.Adapter(device)}");

        NodeGraphModel graph = new();
        var noise = graph.Add("Source/Noise");
        var flood = graph.Add("Analysis/Flood Fill");
        var last = graph.Add("Output/Output");

        noise.SetValue("Scale", 48f);
        flood.SetValue("Iterations", (float)budget);
        flood.SetValue("Threshold", 0.5f);
        graph.Connect(new(noise.Id, "Out"), new(flood.Id, "Mask"));
        graph.Connect(new(flood.Id, "Out"), new(last.Id, "Input"));

        var compiler = Compiler(256, 0);
        var compilation = compiler.Compile(graph);

        Assert.True(compilation.Succeeded);

        var check = Assert.Single(compiler.Checks);

        Assert.Equal(flood.Id, check.Problem.Node);
        Assert.Equal("Iterations", check.Problem.Port);
        Assert.Equal(NodeSeverity.Warning, check.Problem.Severity);

        using var evaluator = new TexturePlanEvaluator(device);
        using var evaluated = evaluator.Evaluate(compilation.Value);

        Assert.Equal(truncated, check.Failed(evaluated));
    }

    /// <summary>The two budgets the report is held to, as the node's own ends of the range.</summary>
    public enum FloodFillBudget {
        /// <summary>The fewest iterations the node takes.</summary>
        Fewest = 4,

        /// <summary>The most it takes.</summary>
        Most = 256
    }

    static TextureGraphCompiler Compiler(int authored, int bake) {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return new(registry) { BaseWidth = authored, BaseHeight = authored, BakeLevelOffset = bake, Seed = 7 };
    }

    /// <summary>A Checker into a Flood Fill at its defaults, read as a size.</summary>
    static (TextureGraphCompiler Compiler, TexturePlan Plan, int Read) Checker(int authored, int bake) {
        NodeGraphModel graph = new();
        var checker = graph.Add("Source/Checker");
        var flood = graph.Add("Analysis/Flood Fill");
        var last = graph.Add("Output/Output");

        checker.SetValue("Scale X", CellsPerSide);
        checker.SetValue("Scale Y", CellsPerSide);
        flood.SetText("Kind", "Size");
        graph.Connect(new(checker.Id, "Out"), new(flood.Id, "Mask"));
        graph.Connect(new(flood.Id, "Out"), new(last.Id, "Input"));

        var compiler = Compiler(authored, bake);
        var compilation = compiler.Compile(graph);

        Assert.True(compilation.Succeeded, string.Join(' ', compilation.Diagnostics.Select(static d => d.Message)));

        return (compiler, compilation.Value, compiler.Outputs[0].Image);
    }

    /// <summary>A black image with some single white texels.</summary>
    static byte[] Mask(int width, int height, params (int X, int Y)[] lit) {
        var pixels = new byte[width * height * 4];

        for (var texel = 0; texel < width * height; texel++) {
            pixels[(texel * 4) + 3] = 255;
        }

        foreach (var (x, y) in lit) {
            pixels.AsSpan(((y * width) + x) * 4, 3).Fill(255);
        }

        return pixels;
    }
}
