// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics.Vulkan;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>One node, compiled from a real graph and baked on a device, over pictures the test spells.</summary>
/// <remarks>
///     <para>
///         <b>Through the node and the compiler, not around them</b>, so a node that passed its numbers to
///         the wrong uniform or read a port it never declared is as red as a kernel that computes the wrong
///         thing. Each picture enters through a <c>Source/Bitmap</c> with <c>Point</c> filtering at the
///         plan's own size, which is a copy texel for texel, so what the node reads is exactly the bytes
///         the test wrote.
///     </para>
///     <para>
///         The graph computes in 32-bit float and the answer is read back as floats, so an expected value
///         is compared to rounding rather than to the nearest of 256 steps.
///     </para>
/// </remarks>
static class TextureNodeBench {
    /// <summary>A picture wired to one image port.</summary>
    /// <param name="Port">The port.</param>
    /// <param name="Texels">RGBA8, row by row, at the plan's size. Ignored when <paramref name="Source" /> is set.</param>
    /// <param name="Grey">Whether the port measures, so the picture goes in as its red channel alone.</param>
    /// <param name="Source">
    ///     A node type drawing the picture instead, at its defaults, for a test that needs values finer than a
    ///     byte — a <c>Source/Gradient</c> sweep is exactly <c>(x + ½) / w</c>.
    /// </param>
    public sealed record Wire(string Port, byte[] Texels, bool Grey = false, string? Source = null);

    /// <summary>Bakes one node and reads back the image on one of its outputs.</summary>
    /// <param name="device">The device.</param>
    /// <param name="path">The node type, or a published compound.</param>
    /// <param name="width">The plan's width, and every picture's.</param>
    /// <param name="height">Its height.</param>
    /// <param name="wires">What goes into the image ports.</param>
    /// <param name="configure">Sets the node's numbers and texts.</param>
    /// <param name="seed">The graph's seed.</param>
    /// <param name="tiling">Whether the graph tiles, which is what a graph that says nothing does.</param>
    /// <returns>The output, as floats.</returns>
    public static Lanes Bake(
        VulkanDevice device,
        string path,
        int width,
        int height,
        Wire[] wires,
        Action<GraphNode>? configure = null,
        int seed = 7717,
        bool tiling = true
    ) {
        NodeGraphModel graph = new();

        graph.Settings[TextureGraphSettings.BitDepth] = "32";

        if (!tiling) {
            graph.Settings[TextureGraphSettings.TilingX] = "false";
            graph.Settings[TextureGraphSettings.TilingY] = "false";
        }

        var subject = graph.Add(path);

        configure?.Invoke(subject);

        for (var at = 0; at < wires.Length; at++) {
            var bitmap = graph.Add(wires[at].Source ?? "Source/Bitmap");

            if (wires[at].Source is null) {
                bitmap.SetText("Source", $"picture{at}");
                bitmap.SetText("Filter", "Point");
            }

            var fed = bitmap;

            if (wires[at].Grey) {
                fed = graph.Add("Colour/Grayscale");
                fed.SetValue("Weight R", 1f);
                fed.SetValue("Weight G", 0f);
                fed.SetValue("Weight B", 0f);
                graph.Connect(new(bitmap.Id, "Out"), new(fed.Id, "Input"));
            }

            graph.Connect(new(fed.Id, "Out"), new(subject.Id, wires[at].Port));
        }

        var sink = graph.Add("Output/Output");

        sink.SetText("Usage", "baseColor");
        graph.Connect(new(subject.Id, "Out"), new(sink.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var library = TextureCompoundLibrary.Publish(registry, folder: null, out var problems);

        Assert.Empty(problems);

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = width,
            BaseHeight = height,
            Seed = (uint)seed,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics.Where(diagnostic => diagnostic.Severity == NodeSeverity.Error));

        var plan = compilation.Value;

        Assert.Empty(plan.Validate());

        using TextureUploads uploads = new(device);

        foreach (var owed in TextureGraphExternals.Upload(uploads, plan, compiler.Externals)) {
            var index = int.Parse(owed.Asset["picture".Length..], System.Globalization.CultureInfo.InvariantCulture);

            uploads.Add(plan, owed.Image, width, height, wires[index].Texels);
        }

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        return new(bake.ReadRaw(compiler.Outputs.Single(output => output.Usage == "baseColor").Image));
    }

    /// <summary>An RGBA8 picture from a function of the texel.</summary>
    public static byte[] Picture(int width, int height, Func<int, int, (float R, float G, float B, float A)> texel) {
        var bytes = new byte[width * height * 4];

        for (var y = 0; y < height; y++) {
            for (var x = 0; x < width; x++) {
                var (r, g, b, a) = texel(x, y);
                var at = ((y * width) + x) * 4;

                bytes[at] = Byte(r);
                bytes[at + 1] = Byte(g);
                bytes[at + 2] = Byte(b);
                bytes[at + 3] = Byte(a);
            }
        }

        return bytes;
    }

    /// <summary>A grey picture, opaque.</summary>
    public static byte[] Grey(int width, int height, Func<int, int, float> value) =>
        Picture(width, height, (x, y) => (value(x, y), value(x, y), value(x, y), 1f));

    /// <summary>A whole-picture constant, opaque.</summary>
    public static byte[] Solid(int width, int height, float r, float g, float b, float a = 1f) =>
        Picture(width, height, (_, _) => (r, g, b, a));

    /// <summary>What an RGBA8 picture's byte reads as.</summary>
    public static float Unit(float value) => Byte(value) / 255f;

    static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);

    /// <summary>A baked image as floats.</summary>
    public sealed class Lanes {
        readonly float[] lanes;

        public Lanes(TextureRawImage raw) {
            ArgumentNullException.ThrowIfNull(raw);

            Width = raw.Width;
            Height = raw.Height;

            var texels = raw.Width * raw.Height;

            Channels = raw.Texels.Length == texels * 4 ? 1 : 4;
            lanes = new float[texels * Channels];

            for (var at = 0; at < lanes.Length; at++) {
                lanes[at] = BitConverter.ToSingle(raw.Texels.AsSpan(at * 4, 4));
            }
        }

        public int Width { get; }

        public int Height { get; }

        /// <summary>One for a grey image, four for a colour one.</summary>
        public int Channels { get; }

        /// <summary>One lane of one texel. A grey image answers its one lane for each colour lane and one for alpha.</summary>
        public float At(int x, int y, int lane) =>
            Channels == 1
                ? lane == 3 ? 1f : lanes[(y * Width) + x]
                : lanes[(((y * Width) + x) * 4) + lane];

        /// <summary>One texel's four lanes.</summary>
        public (float R, float G, float B, float A) Texel(int x, int y) => (At(x, y, 0), At(x, y, 1), At(x, y, 2), At(x, y, 3));
    }
}
