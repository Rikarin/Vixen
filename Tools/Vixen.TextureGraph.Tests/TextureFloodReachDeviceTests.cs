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

    /// <summary>How near a fraction read back has to come: the kernel divides in 32 bits on the device.</summary>
    const float Close = 1e-5f;

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

    /// <summary>A band over the seam is one island with its true size when the plan wraps, and two when not.</summary>
    /// <remarks>Both modes are held, because a mesh map's graph clamps and an atlas's islands are its own.</remarks>
    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    public void A_band_over_the_seam_of_a_wrapping_axis_is_one_island(bool wrapX, bool wrapY, bool oneIsland) {
        using var device = TextureKernelHarness.Open();

        const int Width = 64;
        const int Height = 32;

        // Four texels each side of the seam, past the budget's reach were it counted in texels.
        (int, int)[] band = [.. Rectangle(60, 10, 8, 4, Width, Height)];

        var boxes = Flood(device, Width, Height, band, TextureFloodOutput.BoundingBox, wrapX, wrapY);
        var size = Flood(device, Width, Height, band, TextureFloodOutput.Size, wrapX, wrapY);
        var uv = Flood(device, Width, Height, band, TextureFloodOutput.LocalUv, wrapX, wrapY);

        if (!oneIsland) {
            Assert.Equal(4f / Width, size.At(62, 11, 0), Close);
            Assert.Equal(4f / Width, size.At(1, 11, 0), Close);
            Assert.NotEqual(boxes.At(62, 11, 0), boxes.At(1, 11, 0));

            return;
        }

        foreach (var (x, y) in band) {
            Assert.Equal(60f / Width, boxes.At(x, y, 0), Close);
            Assert.Equal(10f / Height, boxes.At(x, y, 1), Close);
            Assert.Equal(67f / Width, boxes.At(x, y, 2), Close);
            Assert.Equal(13f / Height, boxes.At(x, y, 3), Close);
            Assert.Equal(8f / Width, size.At(x, y, 0), Close);
        }

        Assert.Equal(0f, uv.At(60, 11, 0), Close);
        Assert.Equal(3f / 8f, uv.At(63, 11, 0), Close);
        Assert.Equal(4f / 8f, uv.At(0, 11, 0), Close);
        Assert.Equal(7f / 8f, uv.At(3, 11, 0), Close);
    }

    /// <summary>The same down the other axis, and at both at once, where a corner holds four pieces.</summary>
    [Fact]
    public void An_island_over_the_corner_of_a_torus_is_one_island() {
        using var device = TextureKernelHarness.Open();

        const int Width = 48;
        const int Height = 32;

        (int, int)[] patch = [.. Rectangle(44, 28, 10, 8, Width, Height)];

        var boxes = Flood(device, Width, Height, patch, TextureFloodOutput.BoundingBox, true, true);
        var uv = Flood(device, Width, Height, patch, TextureFloodOutput.LocalUv, true, true);

        foreach (var (x, y) in patch) {
            Assert.Equal(44f / Width, boxes.At(x, y, 0), Close);
            Assert.Equal(28f / Height, boxes.At(x, y, 1), Close);
            Assert.Equal(53f / Width, boxes.At(x, y, 2), Close);
            Assert.Equal(35f / Height, boxes.At(x, y, 3), Close);
        }

        Assert.Equal(9f / 10f, uv.At(5, 3, 0), Close);
        Assert.Equal(7f / 8f, uv.At(5, 3, 1), Close);

        var clamped = Flood(device, Width, Height, patch, TextureFloodOutput.BoundingBox, false, false);

        Assert.NotEqual(clamped.At(47, 31, 0), clamped.At(0, 0, 0));
    }

    /// <summary>A line of corners over the seam, both ways round, on sides that share a factor and on ones that do not.</summary>
    [Theory]
    [InlineData(32, 32, 1)]
    [InlineData(32, 32, -1)]
    [InlineData(24, 36, 1)]
    [InlineData(24, 36, -1)]
    [InlineData(17, 23, 1)]
    public void A_diagonal_island_over_the_corner_is_one_island_only_when_corners_join(int width, int height, int run) {
        using var device = TextureKernelHarness.Open();

        (int X, int Y)[] line = [
            .. Enumerable.Range(0, 8)
                .Select(at => (Wrap(width - 4 + (at * run), width), Wrap(height - 4 + at, height)))
        ];

        var joined = Flood(device, width, height, line, TextureFloodOutput.Size, true, true, true);
        var apart = Flood(device, width, height, line, TextureFloodOutput.Size, true, true);

        foreach (var (x, y) in line) {
            Assert.Equal(8f / width, joined.At(x, y, 0), Close);
            Assert.Equal(8f / height, joined.At(x, y, 1), Close);
            Assert.Equal(1f / width, apart.At(x, y, 0), Close);
        }
    }

    /// <summary>A ring round the whole image has no ends, and says so with the one box that can.</summary>
    [Fact]
    public void A_ring_round_the_image_is_every_texel_of_the_axis() {
        using var device = TextureKernelHarness.Open();

        const int Width = 32;
        const int Height = 16;

        (int, int)[] ring = [.. Rectangle(0, 5, Width, 2, Width, Height)];

        var boxes = Flood(device, Width, Height, ring, TextureFloodOutput.BoundingBox, true, false);

        foreach (var (x, y) in ring) {
            Assert.Equal(0f, boxes.At(x, y, 0), Close);
            Assert.Equal(31f / Width, boxes.At(x, y, 2), Close);
            Assert.Equal(5f / Height, boxes.At(x, y, 1), Close);
            Assert.Equal(6f / Height, boxes.At(x, y, 3), Close);
        }
    }

    /// <summary>A hook the seam closes takes more than one row, and the rounds still settle on one box.</summary>
    [Fact]
    public void A_shape_joined_only_over_the_seam_settles_on_one_box() {
        using var device = TextureKernelHarness.Open();

        const int Width = 32;
        const int Height = 32;

        // Two uprights, joined by one foot over the seam, with nothing in the middle of the image.
        (int, int)[] shape = [
            .. Rectangle(2, 4, 1, 12, Width, Height),
            .. Rectangle(28, 10, 1, 12, Width, Height),
            .. Rectangle(28, 12, 7, 1, Width, Height)
        ];

        var boxes = Flood(device, Width, Height, shape, TextureFloodOutput.BoundingBox, true, true);

        foreach (var (x, y) in shape) {
            Assert.Equal(28f / Width, boxes.At(x, y, 0), Close);
            Assert.Equal(34f / Width, boxes.At(x, y, 2), Close);
            Assert.Equal(4f / Height, boxes.At(x, y, 1), Close);
            Assert.Equal(21f / Height, boxes.At(x, y, 3), Close);
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

    static int Wrap(int value, int extent) => ((value % extent) + extent) % extent;

    /// <summary>The texels of a rectangle that may go over the edges, folded back onto the image.</summary>
    static IEnumerable<(int X, int Y)> Rectangle(int left, int top, int across, int down, int width, int height) =>
        from y in Enumerable.Range(top, down)
        from x in Enumerable.Range(left, across)
        select (Wrap(x, width), Wrap(y, height));

    /// <summary>One output of a flood over a mask, in a plan with this wrap.</summary>
    static Floats Flood(
        IGraphicsDevice device,
        int width,
        int height,
        (int X, int Y)[] lit,
        TextureFloodOutput kind,
        bool wrapX,
        bool wrapY,
        bool diagonal = false
    ) {
        const int Budget = 16;

        var (texture, staging) = TextureKernelHarness.Upload(device, Mask(width, height, lit), width, height);

        try {
            var images = ImmutableArray.CreateBuilder<TextureImage>();

            images.Add(new(TextureFormat.Rgba8, External: true));

            for (var pass = 0; pass < Budget + 1; pass++) {
                images.Add(new(TextureFormat.Rgba32Float));
            }

            var read = Budget + 1;
            var plan = new TexturePlan {
                BaseWidth = width,
                BaseHeight = height,
                WrapX = wrapX,
                WrapY = wrapY,
                Images = images.ToImmutable(),
                Ops = TextureAnalysis.FloodFill(
                    read,
                    0,
                    [.. Enumerable.Range(1, Budget)],
                    width,
                    height,
                    kind,
                    diagonal
                ),
                Outputs = [read]
            };

            Assert.Empty(plan.Validate());

            using var evaluator = new TexturePlanEvaluator(device);
            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureHandle> { [0] = texture });

            return new(MemoryMarshal.Cast<byte, float>(bake.ReadRaw(read).Texels).ToArray(), width);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>A read-back RGBA picture as floats.</summary>
    sealed record Floats(float[] Lanes, int Width) {
        public float At(int x, int y, int lane) => Lanes[(((y * Width) + x) * 4) + lane];
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
