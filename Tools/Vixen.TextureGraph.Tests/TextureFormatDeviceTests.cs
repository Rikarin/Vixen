// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Runtime.InteropServices;
using Vixen.Core.Imaging;
using Vixen.Core.Imaging.BlockCompression;
using Vixen.Graphics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>The 32-bit formats and sizes that are not a power of two, across the whole library.</summary>
/// <remarks>
///     <para>
///         <b>A kernel has no per-format source to go wrong</b>: <c>TextureKernels.Variant</c> rewrites
///         one <c>[Format]</c>, every read is a <c>Load</c>, and the evaluator binds no samplers. What
///         could differ is a kernel leaning on a half's range or a node assuming a format, so the
///         oracle is the same graph at 16 bits: the two may differ by what a half rounds, not by more.
///     </para>
///     <para>
///         ⚠ <b>An extent like 101×67 is odd at every level</b>, which is where a floored halving and an
///         eight-texel group both leave a tail. Nothing in the plan refuses such a base, and this is
///         what says that an extent nothing refused also works.
///     </para>
/// </remarks>
public class TextureFormatDeviceTests(ITestOutputHelper output) {
    const int Side = 64;

    /// <summary>The largest move a half's rounding can excuse, in eight-bit levels of a finished picture.</summary>
    const int Rounding = 3;

    /// <summary>
    ///     Nodes that send a continuous input to a few values, and how many texels of one picture may land on
    ///     the other side of a step at 16 bits than at 32.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A step is where rounding is not small.</b> A noise texel at 0.50001 is white under a palette
    ///     of black and white at 32 bits, and rounds to exactly 0.5 at 16, where the tie goes to black: one
    ///     texel, 255 levels, and both bakes right. Patina #171's <c>Colour/Palette</c> drew that on its
    ///     first run, and <c>Colour/Threshold</c> and <c>Colour/Quantise</c> have the same edges and had
    ///     missed them by luck. So for these the rule is that a flip is rare — a texel in 256 — rather than
    ///     absent; a format that moved the step itself would flip a band, not a speck. Patina #176's
    ///     <c>Surface/Height Shadow</c> is a step too at its default hardness: a horizon a hair above the
    ///     light's elevation at one depth is a hair below it at the other.
    /// </remarks>
    static readonly HashSet<string> Stepped = ["Colour/Palette", "Colour/Quantise", "Colour/Threshold", "Surface/Height Shadow"];

    /// <summary>Every node a graph with nothing but wires can hold, one graph each.</summary>
    static IEnumerable<(string Path, NodeGraphModel Graph)> Library(NodeTypeRegistry registry) {
        foreach (var type in registry.Types.OrderBy(static type => type.Path, StringComparer.Ordinal)) {
            if (type.Path is "Source/Bitmap" or "Output/Output" || type.Path.Contains("Mesh", StringComparison.Ordinal)) {
                continue;
            }

            NodeGraphModel graph = new();
            var node = graph.Add(type.Path);

            if (type.Path == "Filters/Pixel Processor") {
                node.SetText("Expression", "a * 0.5");
            }

            foreach (var port in type.Ports) {
                if (port is { Direction: PortDirection.Input, Kind: PortKind.Image }) {
                    graph.Connect(new(graph.Add("Source/Noise").Id, "Out"), new(node.Id, port.Name));
                }
            }

            yield return (type.Path, graph);
        }
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    static TextureGraphCompiler Compiler(NodeTypeRegistry registry, int width, int height, int depth, NodeGraphModel graph) {
        TextureGraphSettings.Declare(graph, width, height, 7);
        graph.Settings[TextureGraphSettings.BitDepth] = depth.ToString(CultureInfo.InvariantCulture);

        return new(registry) { PreviewEveryNode = true };
    }

    /// <summary>Every lane of every output of a graph, as a number.</summary>
    static float[] Lanes(TextureBake bake, int image) {
        var raw = bake.ReadTexels(image);

        return bake.Plan.Images[image].Format switch {
            TextureFormat.R16Float or TextureFormat.Rgba16Float =>
                [.. MemoryMarshal.Cast<byte, Half>(raw).ToArray().Select(static lane => (float)lane)],
            _ => MemoryMarshal.Cast<byte, float>(raw).ToArray()
        };
    }

    [Fact]
    public void Every_node_draws_at_32_bits_what_it_draws_at_16() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var compared = 0;
        List<string> moved = [];

        foreach (var (path, graph) in Library(registry)) {
            var narrow = Compiler(registry, Side, Side, 16, graph);
            var compilation = narrow.Compile(graph);

            if (compilation.HasErrors || compilation.Value.Ops.Length == 0 || narrow.Externals.Any(static external => external.Asset.Length > 0)) {
                continue;
            }

            var wide = Compiler(registry, Side, Side, 32, graph);
            var plan = wide.Compile(graph).Value;

            Assert.All(
                plan.Ops.Select(op => plan.Images[op.Output].Format),
                format => Assert.True(format is TextureFormat.R32Float or TextureFormat.Rgba32Float or TextureFormat.Rgba8, $"{path} wrote {format}.")
            );

            using var narrowUploads = new TextureUploads(device);
            using var wideUploads = new TextureUploads(device);

            Assert.Empty(TextureGraphExternals.Upload(narrowUploads, compilation.Value, narrow.Externals));
            Assert.Empty(TextureGraphExternals.Upload(wideUploads, plan, wide.Externals));

            using var low = evaluator.Evaluate(compilation.Value, narrowUploads.Externals);
            using var high = evaluator.Evaluate(plan, wideUploads.Externals);

            foreach (var image in plan.Outputs) {
                if (plan.Images[image].External) {
                    continue;
                }

                var expected = low.Read(image);
                var actual = high.Read(image);

                Assert.Equal(expected.Width, actual.Width);

                var largest = Enumerable.Range(0, expected.Pixels.Length)
                    .Max(at => Math.Abs(expected.Pixels[at] - actual.Pixels[at]));

                var flipped = Enumerable.Range(0, expected.Pixels.Length)
                    .Count(at => Math.Abs(expected.Pixels[at] - actual.Pixels[at]) > Rounding);

                if (Stepped.Contains(path) ? flipped * 256 > expected.Pixels.Length : largest > Rounding) {
                    moved.Add($"{path} image {image}: {largest} levels");
                }

                compared++;
            }
        }

        output.WriteLine($"{TextureKernelHarness.Adapter(device)}: {compared} images compared; moved: {string.Join("; ", moved)}");

        Assert.True(compared > 60, $"Only {compared} images were compared.");
        Assert.Empty(moved);
    }

    /// <summary>
    ///     What a float export reads. The bytes are the device's: they come with the format and the
    ///     extent that say what they mean, they carry detail an eight-bit read cannot, and they go
    ///     straight into the block compressor and a mip chain.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void A_raw_read_is_the_image_in_its_own_format(int depth) {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        NodeGraphModel graph = new();

        graph.Add("Source/Noise");

        var compiler = Compiler(registry, 101, 67, depth, graph);
        var plan = compiler.Compile(graph).Value;

        using var uploads = new TextureUploads(device);

        Assert.Empty(TextureGraphExternals.Upload(uploads, plan, compiler.Externals));

        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var image = plan.Outputs.First(output => !plan.Images[output].External);
        var raw = bake.ReadRaw(image);

        Assert.Equal((101, 67), (raw.Width, raw.Height));
        Assert.Equal(TextureFormats.Pixel(plan.Images[image].Format), raw.Format);
        Assert.Equal(bake.ReadTexels(image), raw.Texels);
        Assert.Equal(raw.Width * raw.Height * raw.Format.BlockSize(), raw.Texels.Length);

        // More than eight bits' worth: a value no byte could have held is a value Read would round.
        var lanes = Lanes(bake, image);

        Assert.Contains(lanes, lane => MathF.Abs((lane * 255f) - MathF.Round(lane * 255f)) > 0.01f);

        var texture = raw.ToTexture(levelCount: 0);

        MipChain.Generate(texture);

        Assert.Equal(7, texture.LevelCount);
        Assert.True(raw.Texels.AsSpan().SequenceEqual(texture.Level(0)));
        Assert.Equal(PixelFormat.Bc7RgbaUNorm, BlockCompressor.Encode(texture, PixelFormat.Bc7RgbaUNorm, new() { Quality = BlockQuality.Fast }).Format);
        Assert.Equal(PixelFormat.Bc6HRgbUFloat, BlockCompressor.Encode(texture, PixelFormat.Bc6HRgbUFloat, new() { Quality = BlockQuality.Fast }).Format);

        output.WriteLine($"{TextureKernelHarness.Adapter(device)}: read {raw.Format} at {raw.Width}x{raw.Height}");
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void Every_node_draws_at_a_size_that_is_not_a_power_of_two(int depth) {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var drawn = 0;

        foreach (var (path, graph) in Library(registry)) {
            var compiler = Compiler(registry, 101, 67, depth, graph);
            var compilation = compiler.Compile(graph);

            if (compilation.HasErrors || compilation.Value.Ops.Length == 0 || compiler.Externals.Any(static external => external.Asset.Length > 0)) {
                continue;
            }

            using var uploads = new TextureUploads(device);

            Assert.Empty(TextureGraphExternals.Upload(uploads, compilation.Value, compiler.Externals));

            using var bake = evaluator.Evaluate(compilation.Value, uploads.Externals);

            foreach (var image in compilation.Value.Outputs) {
                if (compilation.Value.Images[image].External) {
                    continue;
                }

                var size = compilation.Value.SizeOf(image);

                Assert.Equal(size.X * size.Y * TextureFormats.BytesPerTexel(compilation.Value.Images[image].Format), bake.ReadTexels(image).Length);
                Assert.DoesNotContain(Lanes(bake, image), static lane => float.IsNaN(lane));

                drawn++;
            }
        }

        output.WriteLine($"{TextureKernelHarness.Adapter(device)}: {drawn} images at 101×67 and {depth} bits.");

        Assert.True(drawn > 60, $"Only {drawn} images were drawn.");
    }

    /// <summary>⚠ The tail: an image no whole number of groups covers is still written to its last texel.</summary>
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void A_flat_colour_reaches_the_last_texel_of_every_level_of_an_odd_image(int depth) {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        NodeGraphModel graph = new();
        var uniform = graph.Add("Source/Uniform");
        var blur = graph.Add("Filters/Blur");
        var levels = graph.Add("Colour/Levels");

        uniform.SetValue("Colour", 0.5f, 0.5f, 0.5f, 1f);
        graph.Connect(new(uniform.Id, "Out"), new(blur.Id, "Input"));
        graph.Connect(new(blur.Id, "Out"), new(levels.Id, "Input"));

        var compiler = Compiler(registry, 101, 67, depth, graph);
        var plan = compiler.Compile(graph).Value;

        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureExternal>());

        foreach (var image in plan.Outputs) {
            var size = plan.SizeOf(image);
            var lanes = Lanes(bake, image);
            var stride = lanes.Length / (size.X * size.Y);

            for (var lane = 0; lane < lanes.Length; lane += stride) {
                Assert.InRange(lanes[lane], 0.49f, 0.51f);
            }
        }
    }

    /// <summary>⚠ What the format is for: a difference a half rounds away is still there at 32 bits.</summary>
    [Fact]
    public void Thirty_two_bits_keep_a_step_a_half_rounds_away() {
        const float Step = 1f / (1 << 13);

        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        var values = new Dictionary<int, float>();

        foreach (var depth in new[] { 16, 32 }) {
            NodeGraphModel graph = new();
            var uniform = graph.Add("Source/Uniform");

            uniform.SetValue("Colour", 0.5f + Step, 0.5f + Step, 0.5f + Step, 1f);

            var compiler = Compiler(registry, 8, 8, depth, graph);
            var plan = compiler.Compile(graph).Value;

            using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureExternal>());

            values[depth] = Lanes(bake, plan.Outputs[0])[0];
        }

        Assert.Equal(0.5f, values[16]);
        Assert.Equal(0.5f + Step, values[32]);
    }

    /// <summary>Formats are in a result's key: one graph at two depths shares no result and no kernel variant.</summary>
    [Fact]
    public void A_graph_at_two_depths_shares_no_result() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        NodeGraphModel graph = new();

        graph.Add("Source/Noise");

        var narrow = Compiler(registry, 16, 16, 16, graph).Compile(graph).Value;
        var wide = Compiler(registry, 16, 16, 32, graph).Compile(graph).Value;

        using (evaluator.EvaluateIncremental(narrow, new Dictionary<int, TextureExternal>())) { }

        using var bake = evaluator.EvaluateIncremental(wide, new Dictionary<int, TextureExternal>());

        Assert.Equal(wide.Ops.Length, bake.Ran.Length);
        Assert.Equal(0, bake.Reused);
    }

    [Theory]
    [InlineData(TextureFormat.R32Float, 4)]
    [InlineData(TextureFormat.Rgba32Float, 16)]
    public void A_32_bit_image_is_a_picture_like_any_other(TextureFormat format, int stride) {
        Assert.Equal(stride, TextureFormats.BytesPerTexel(format));
        Assert.True(TextureFormats.IsStorable(format));

        var raw = new float[stride / 4];

        raw[0] = 0.5f;

        Bitmap picture = TexturePixels.ToBitmap(MemoryMarshal.AsBytes(raw.AsSpan()), 1, 1, format);

        Assert.Equal(128, picture.Pixels[0]);
    }

    [Fact]
    public void A_bit_depth_that_is_neither_is_said_and_ignored() {
        NodeGraphModel graph = new();

        graph.Settings[TextureGraphSettings.BitDepth] = "24";

        Assert.Equal(16, TextureGraphSettings.BitDepthOf(graph, out var problem));
        Assert.Contains("24", problem, StringComparison.Ordinal);

        graph.Add("Source/Noise");

        var compilation = new TextureGraphCompiler(Registry()) { PreviewEveryNode = true }.Compile(graph);

        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Id == TextureDiagnostics.GraphSettingIgnored);
    }

    /// <summary>A CPU op is the one thing that reads and writes bytes by format itself.</summary>
    [Fact]
    public void Normal_to_height_writes_32_bits_at_32_bits() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var registry = Registry();
        NodeGraphModel graph = new();
        var normal = graph.Add("Surface/Normal to Height");
        var height = graph.Add("Surface/Height to Normal");

        graph.Connect(new(graph.Add("Source/Noise").Id, "Out"), new(height.Id, "Height"));
        graph.Connect(new(height.Id, "Out"), new(normal.Id, "Normal"));

        var compiler = Compiler(registry, Side, Side, 32, graph);
        var compilation = compiler.Compile(graph);

        Assert.False(compilation.HasErrors, string.Join("; ", compilation.Diagnostics));

        using var bake = evaluator.Evaluate(compilation.Value, new Dictionary<int, TextureExternal>());

        Assert.Contains(compilation.Value.Ops, static op => op.Cpu is not null);
        Assert.All(compilation.Value.Outputs, image => Assert.DoesNotContain(Lanes(bake, image), static lane => float.IsNaN(lane)));
    }

    /// <summary>Read through a batch, which offsets by each format's own stride.</summary>
    [Fact]
    public void Images_of_both_depths_read_together() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var plan = new TexturePlan {
            BaseWidth = 9,
            BaseHeight = 5,
            Images = [new(TextureFormat.R32Float), new(TextureFormat.Rgba32Float), new(TextureFormat.R16Float)],
            Ops = [
                TextureSources.Uniform(0, 0.25f),
                TextureSources.Uniform(1, 0.5f),
                TextureSources.Uniform(2, 0.75f)
            ],
            Outputs = [0, 1, 2]
        };

        using var bake = evaluator.Evaluate(plan, new Dictionary<int, TextureExternal>());

        var pictures = bake.Read([2, 1, 0]);

        Assert.Equal([191, 128, 64], pictures.Select(static picture => (int)picture.Pixels[0]));
    }
}
