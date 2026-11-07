// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using Vixen.Core.Imaging;
using Vixen.Graphics;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>Whether every node and every shipped compound tiles when its graph does.</summary>
/// <remarks>
///     <para>
///         <b>The subjects are read off the registry</b>, atomic nodes and published compounds alike, so
///         a new node or a new <c>.vxtexgraph</c> is in this roll call by existing and there is no list
///         to forget it from. One that cannot tile says so where it is written:
///         <see cref="DoesNotTileAttribute" /> on a node class, <c>doesNotTile</c> in a compound's
///         settings. A mark has to be true, so a marked subject that turns out to tile is as red as an
///         unmarked one that does not.
///     </para>
///     <para>
///         <b>Two questions, because neither alone is enough.</b> The seam compares the step across
///         the wrap with the largest step inside the picture, on the picture itself, which any
///         subject can be asked. It cannot see a clamp whose effect is smaller than the picture's
///         own contrast, so a subject with an image input also answers a harder one: rolling the
///         input must roll the output. A filter that wraps satisfies that to rounding, wherever
///         in the image it looks; one that clamps anywhere does not. That is the property that
///         makes a <c>1 0 1 0</c> row keep its period through a blur.
///     </para>
///     <para>
///         ⚠ <b>The stimulus is arithmetic, so it cannot be why something fails.</b> Whole numbers of
///         sine cycles through a Pixel Processor repeat exactly and go through no kernel this class
///         examines, and they are smooth, so a one-texel step inside the picture is small and a
///         seam is not. Everything bakes at 32 bits for the same reason: at 16, a stimulus rolled
///         by a whole number of texels differs from the original by half-float rounding, and a
///         node that takes a slope reports that as drift.
///     </para>
///     <para>
///         ⚠ <b>What the seam cannot see, named.</b> A compound with no image input answers only
///         the seam, and a high-contrast compound hides a small one: with every read clamped
///         six of the eight grunges went red at a bar of 1.5 and seven at 1.2, and
///         <c>Grunge Leaks</c> stayed green at both. <c>Grunge Scratches</c> passed it while
///         rotated by an angle no lattice survives, which is why that compound stopped being one.
///         The bar is 1.2 because everything that tiles measured at most 1.0: the seam is one more
///         column of the picture, and cannot be its largest step by much.
///     </para>
///     <para>
///         <c>VIXEN_TILING_PICTURES</c> names a folder for a two-by-two repeat of every subject and
///         the table this prints, which is what a person looks at for a seam.
///     </para>
/// </remarks>
public class TextureTilingConformanceDeviceTests(ITestOutputHelper output) {
    /// <summary>How wide and tall every subject bakes.</summary>
    const int Side = 512;

    /// <summary>How far the second bake's stimulus is rolled along x, in texels.</summary>
    const int RollX = 171;

    /// <summary>How far along y.</summary>
    const int RollY = 219;

    /// <summary>A whole turn, as a Raven literal.</summary>
    const string Turn = "6.2831853f";

    /// <summary>The usages a subject's outputs are written under, one per output port.</summary>
    static readonly string[] Usages = ["baseColor", "height", "normal", "roughness", "metalness"];

    /// <summary>Where the subject's first stimulus is written, to see whether the subject moved it.</summary>
    const string Original = "mask";

    /// <summary>One sine with a whole number of cycles across the image, so it is exactly periodic.</summary>
    static string Wave(int cyclesX, int cyclesY, string phase) =>
        $"sin({Turn} * ({cyclesX}f * (uv.x + x) + {cyclesY}f * (uv.y + y)) + {phase})";

    /// <summary>A smooth field. Smooth, so that a one-texel step inside it is small and a seam is not.</summary>
    static string Field(string phase) =>
        $"(0.5f + 0.2f * {Wave(3, 2, phase)} + 0.2f * {Wave(5, -3, phase + " + 1.7f")})";

    static string Grey => $"float4({Field("0.3f")}, 0f, 0f, 1f)";

    static string Colour => $"float4({Field("0.3f")}, {Field("1.9f")}, {Field("4.1f")}, 1f)";

    static string Mask => $"float4(step(0.5f, {Field("0.3f")}), 0f, 0f, 1f)";

    /// <summary>A tangent-space normal, encoded, leaning about a periodic slope.</summary>
    static string Normal =>
        $"float4(0.5f + 0.2f * {Wave(1, 1, "0.3f")}, 0.5f + 0.2f * {Wave(1, 2, "1.1f")}, 0.9f, 1f)";

    /// <summary>What wires into one image port, when it is not <see cref="Grey" />.</summary>
    static string StimulusFor(string path, string port) =>
        (path, port) switch {
            ("Analysis/Distance", _) or ("Analysis/Flood Fill", _) => Mask,
            ("Surface/Bevel", _) or ("Surface/Metal Reflectance", _) => Mask,
            ("Filters/Vector Warp", "Vectors") or ("Analysis/Colour Select", _) => Colour,
            ("Surface/Normal Combine", _) => Normal,
            ("Surface/Curvature", _) or ("Surface/Normal Transform", _) or ("Surface/Normal to Height", _) => Normal,
            _ => Grey
        };

    /// <summary>
    ///     What is owed, not impossible: these should tile and do not yet, and each entry goes red the
    ///     day it does.
    /// </summary>
    static readonly Dictionary<string, string> Owed = new() {
        ["Analysis/Flood Fill"] = "An island across the wrap is two islands until the bounds propagation goes "
            + "round, which belongs with moving it to a jump flood: Rikarin/Patina#98."
    };

    /// <summary>Subjects that draw one value by design, so that a flat picture is not a failure.</summary>
    static readonly HashSet<string> Flat = ["Source/Uniform", "Colour/Metal Reflectance"];

    /// <summary>Whether a stimulus goes in as one channel. Most image ports measure, and refuse a colour.</summary>
    static bool IsGrey(string stimulus) => stimulus == Grey || stimulus == Mask;

    /// <summary>Numbers that make a subject do something, where its default leaves the picture alone.</summary>
    static (string Port, float Value)[] Exercised(string path) =>
        path switch {
            "Filters/Warp" or "Filters/Directional Warp" or "Filters/Vector Warp" => [("Intensity", 24f)],
            "Analysis/Colour Select" => [("Tolerance", 0.7f)],
            "Filters/Blur HQ" => [("Sigma", 6f)],
            "Filters/Directional Blur" => [("Length", 16f), ("Angle", 0.6f)],
            "Filters/Sharpen" => [("Radius", 6f)],
            "Filters/Non-Uniform Blur" => [("Max Radius", 12f)],
            "Surface/Curvature" => [("Radius", 4f)],
            "Source/Shape" => [("Scale", 0.5f), ("Centre X", 0.95f), ("Centre Y", 0.05f)],
            "Space/Transform 2D" => [("Offset X", 0.37f), ("Offset Y", 0.21f)],
            _ => []
        };

    /// <summary>
    ///     Subjects whose picture depends on where a texel is as well as on what surrounds it, so
    ///     rolling the input cannot be expected to roll the output.
    /// </summary>
    static readonly HashSet<string> PositionDependent = [
        "Placement/Splatter",
        "Placement/Tile Sampler",
        "Patterns/Tile Random",
        "Space/Mirror",
        "Space/Crop",
        "Space/Tile",
        "Space/Transform 2D",
        "Utility/Make It Tile",
        "Utility/Safe Transform"
    ];

    /// <summary>Everything the registry declares and everything that ships.</summary>
    static IEnumerable<(string Path, bool Compound)> Subjects(NodeTypeRegistry registry, NodeTypeRegistry atomic) =>
        registry.Types.Select(type => (type.Path, !atomic.Types.Any(known => known.Path == type.Path)))
            .OrderBy(subject => subject.Item2)
            .ThenBy(subject => subject.Path, StringComparer.Ordinal);

    /// <summary>The roll call.</summary>
    [Fact]
    public void Every_node_and_compound_tiles_or_says_why_it_cannot() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        using var evaluator = new TexturePlanEvaluator(device);

        var atomic = Registry();
        var registry = Registry();
        var library = TextureCompoundLibrary.Publish(registry, folder: null, out var problems);

        Assert.Empty(problems);

        var pictures = Environment.GetEnvironmentVariable("VIXEN_TILING_PICTURES");

        if (pictures is not null) {
            Directory.CreateDirectory(pictures);
        }

        var report = new StringBuilder();
        var wrong = new List<string>();
        var marks = Marks();
        var taken = 0;
        var marked = 0;

        foreach (var (path, _) in Subjects(registry, atomic)) {
            taken++;

            var mark = library.TryGet(path, out var compound)
                ? compound.SettingOf(TextureGraphSettings.DoesNotTile) is { Length: > 0 } written ? written : null
                : marks.GetValueOrDefault(path);

            mark ??= Owed.GetValueOrDefault(path);

            var outcome = Examine(device, evaluator, library, registry, path, pictures, mark is not null);

            marked += mark is null ? 0 : 1;
            report.Append(mark is null ? outcome.Line : $"{outcome.Line.TrimEnd()}  [marked: {mark}]\n");

            if (mark is null && outcome.Broken) {
                wrong.Add($"{path} does not tile, and nothing says it cannot.");
            }

            if (mark is not null && !outcome.Broken) {
                wrong.Add($"{path} is marked as not tiling, and it tiles: {mark}");
            }
        }

        output.WriteLine(report.ToString());

        if (pictures is not null) {
            File.WriteAllText(Path.Combine(pictures, "roll-call.txt"), report.ToString());
        }

        // ⚠ The instrument. Every assertion above is inside the loop, so a registry that came back
        // empty would leave this green over no work at all. This number is the one here that does
        // not come from the registry, and only ever grows.
        Assert.True(taken >= 84, $"only {taken} subjects were examined, and eighty-four exist.");
        Assert.True(marked < taken / 2, $"{marked} of {taken} are marked, which is a roll call of excuses.");
        Assert.True(wrong.Count == 0, $"{adapter}, {wrong.Count} of {taken}:\n{string.Join("\n", wrong)}");
    }

    /// <summary>
    ///     A <c>0 1 0 1</c> row through a blur of radius one is two thirds at the first texel when it
    ///     wraps and a third when it clamps, and a third at the second either way.
    /// </summary>
    /// <remarks>Both modes are held, because clamp has to survive for a graph reading an atlas.</remarks>
    [Theory]
    [InlineData(true, 170)]
    [InlineData(false, 85)]
    public void A_blur_reads_the_far_edge_exactly_when_the_plan_wraps(bool wrap, byte expected) {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var (texture, staging) = TextureKernelHarness.Upload(
            device,
            TextureKernelHarness.Columns(TextureKernelHarness.Side),
            TextureKernelHarness.Side,
            TextureKernelHarness.Side
        );

        try {
            var plan = new TexturePlan {
                BaseWidth = TextureKernelHarness.Side,
                BaseHeight = TextureKernelHarness.Side,
                WrapX = wrap,
                WrapY = wrap,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba16Float)],
                Ops = [
                    new() {
                        Kernel = "Blur",
                        Output = 1,
                        Inputs = [0],
                        Parameters = [new("radius", 1f), new("stepX", 1f), new("stepY", 0f)]
                    }
                ],
                Outputs = [1]
            };

            using var bake = evaluator.Evaluate(
                plan,
                TextureKernelHarness.Externals(0, texture, TextureKernelHarness.Side, TextureKernelHarness.Side)
            );

            var picture = bake.Read(1);

            Assert.InRange(TextureKernelHarness.At(picture, 0, 0, 0), (byte)(expected - 1), (byte)(expected + 1));

            // ⚠ Inside the image the two agree, so the address is all that differs.
            Assert.InRange(TextureKernelHarness.At(picture, 1, 0, 0), (byte)84, (byte)86);
        } finally {
            device.Destroy(staging);
            device.Destroy(texture);
        }
    }

    /// <summary>A graph tiles unless it says otherwise or reads a mesh map, and one axis can differ.</summary>
    [Theory]
    [InlineData(null, null, false, true, true)]
    [InlineData("false", null, false, false, true)]
    [InlineData(null, "false", false, true, false)]
    [InlineData("false", "false", false, false, false)]
    [InlineData(null, null, true, false, false)]
    [InlineData("true", "true", true, true, true)]
    public void A_plan_wraps_as_its_graph_declares(string? x, string? y, bool atlas, bool wrapX, bool wrapY) {
        NodeGraphModel graph = new();
        var source = graph.Add(atlas ? "Source/Mesh Map" : "Source/Noise");
        var sink = graph.Add("Output/Output");

        graph.Connect(new(source.Id, "Out"), new(sink.Id, "Input"));

        if (x is not null) {
            graph.Settings[TextureGraphSettings.TilingX] = x;
        }

        if (y is not null) {
            graph.Settings[TextureGraphSettings.TilingY] = y;
        }

        var plan = new TextureGraphCompiler(Registry()) { BaseWidth = 64, BaseHeight = 64 }.Compile(graph).Value;

        Assert.Equal(wrapX, plan.WrapX);
        Assert.Equal(wrapY, plan.WrapY);
    }

    /// <summary>A declaration that is neither word is said, not taken as either.</summary>
    [Fact]
    public void A_tiling_setting_that_is_neither_word_is_reported() {
        NodeGraphModel graph = new();

        graph.Settings[TextureGraphSettings.TilingX] = "yes";

        Assert.Null(TextureGraphSettings.TilingOf(graph, TextureGraphSettings.TilingX, out var problem));
        Assert.Contains("neither true nor false", problem, StringComparison.Ordinal);
    }

    /// <summary>
    ///     ⚠ A wrapping read reaches the far side of the image, so an op that takes one cannot be
    ///     repaired in place from a rectangle clipped to the near side.
    /// </summary>
    [Fact]
    public void A_wrapping_neighbour_read_gives_up_its_reach_and_a_pointwise_one_keeps_it() {
        TexturePlan Plan(bool wrap, TextureOp op) =>
            new() {
                BaseWidth = 64,
                BaseHeight = 64,
                WrapX = wrap,
                Images = [new(TextureFormat.Rgba8, External: true), new(TextureFormat.Rgba16Float)],
                Ops = [op],
                Outputs = [1]
            };

        // `Bitmap` is the one kernel with a reach of its own: a bilinear read one texel across.
        Dictionary<int, TextureExternal> externals = new() {
            [0] = new(default, TextureKernelHarness.SourceUsage, new(64, 64))
        };

        int? Reach(TexturePlan plan) => TexturePlanEvaluator.Reach(plan, externals, plan.Ops[0]);

        var filtered = TextureSources.Bitmap(1, 0, srgb: false);
        var pointwise = new TextureOp {
            Kernel = "Invert",
            Output = 1,
            Inputs = [0],
            Parameters = [new("invertR", 1f), new("invertG", 0f), new("invertB", 0f), new("invertA", 0f)]
        };

        Assert.Equal(1, Reach(Plan(false, filtered)));
        Assert.Null(Reach(Plan(true, filtered)));
        Assert.Equal(0, Reach(Plan(false, pointwise)));
        Assert.Equal(0, Reach(Plan(true, pointwise)));
    }

    /// <summary>Every node class that says it cannot tile, by the path it is registered under.</summary>
    static Dictionary<string, string> Marks() {
        Dictionary<string, string> marks = [];

        foreach (var type in typeof(TextureGraphCompiler).Assembly.GetTypes()) {
            if (type.GetCustomAttribute<DoesNotTileAttribute>() is { } mark) {
                marks[type.GetCustomAttribute<NodeAttribute>()!.Path] = mark.Reason;
            }
        }

        return marks;
    }

    sealed record Outcome(string Line, bool Broken);

    static Outcome Examine(
        IGraphicsDevice device,
        TexturePlanEvaluator evaluator,
        ISubGraphSource library,
        NodeTypeRegistry registry,
        string path,
        string? pictures,
        bool marked
    ) {
        var type = registry.Types.Single(candidate => candidate.Path == path);
        var outputs = type.Ports
            .Where(port => port is { Direction: PortDirection.Output, Kind: PortKind.Image })
            .Select(port => port.Name)
            .ToArray();

        if (outputs.Length == 0) {
            // The one type with nothing to look at. Anything else without an output is a subject this
            // class has lost.
            return new($"{path,-36} no image output\n", path != "Output/Output");
        }

        var inputs = type.Ports
            .Where(port => port is { Direction: PortDirection.Input, Kind: PortKind.Image })
            .Select(port => port.Name)
            .ToArray();

        try {
            var first = Bake(device, evaluator, library, registry, path, inputs, outputs, 0, 0, pictures);

            // A mesh map's reader clamps, which is right for it. Anything unmarked has to have been
            // measured in the mode this class is about, or a graph that quietly stopped tiling
            // would still pass on a picture that happens to meet at its edges.
            if (!marked && first.Problem is null && !first.Wrapped) {
                return new($"{path,-36} DID NOT WRAP: a graph that says nothing compiled to a clamp.\n", true);
            }

            if (first.Problem is { } problem) {
                return new($"{path,-36} NOT BAKED: {problem}\n", true);
            }

            var rolled = inputs.Length == 0 || PositionDependent.Contains(path)
                ? null
                : Bake(device, evaluator, library, registry, path, inputs, outputs, RollX, RollY, null);

            var inert = false;

            var line = new StringBuilder();
            var broken = false;

            foreach (var name in outputs) {
                var picture = first.Pictures[name];
                var (seamX, stepX) = picture.Seam(horizontal: true);
                var (seamY, stepY) = picture.Seam(horizontal: false);
                var seamed = seamX > (1.2f * stepX) + 0.001f || seamY > (1.2f * stepY) + 0.001f;
                var moved = rolled is null ? float.NaN : picture.Rolled(rolled.Pictures[name], RollX, RollY);
                var drifted = moved > 0.002f;
                var changed = first.Stimulus is { } stimulus ? picture.Difference(stimulus) : float.NaN;

                // A flat picture tiles whatever the kernel did, and so does a filter left at numbers
                // that make it a copy of its input: neither has been examined.
                inert |= (picture.Contrast() < 0.01f && !Flat.Contains(path))
                    || (changed < 0.0005f && path.StartsWith("Filters/", StringComparison.Ordinal)
                        && path != "Filters/Pixel Processor");

                broken |= seamed || drifted;

                line.Append(
                    CultureInfo.InvariantCulture,
                    $"{(seamed || drifted ? "SEAM" : "ok  ")} {path,-36} {name,-6} x {seamX:F4}/{stepX:F4} y {seamY:F4}/{stepY:F4} roll {moved:F4} contrast {picture.Contrast():F3} changed {changed:F4}\n"
                );
            }

            return inert ? new($"{path,-36} INERT: nothing here was examined.\n{line}", true) : new(line.ToString(), broken);
        } catch (Exception exception) {
            return new($"{path,-36} THREW: {exception.GetType().Name}: {exception.Message}\n", true);
        }
    }

    sealed record Baked(
        Dictionary<string, Picture> Pictures,
        string? Problem,
        Picture? Stimulus = null,
        bool Wrapped = false
    );

    static Baked Bake(
        IGraphicsDevice device,
        TexturePlanEvaluator evaluator,
        ISubGraphSource library,
        NodeTypeRegistry registry,
        string path,
        string[] inputs,
        string[] outputs,
        int rollX,
        int rollY,
        string? pictures
    ) {
        NodeGraphModel graph = new();

        // ⚠ No tiling setting. What a graph gets by saying nothing is what is being held here.
        graph.Settings[TextureGraphSettings.BitDepth] = "32";

        var subject = graph.Add(path);

        if (path == "Source/Bitmap") {
            subject.SetText("Source", "stimulus");
        }

        foreach (var (port, value) in Exercised(path)) {
            subject.SetValue(port, value);
        }

        foreach (var port in inputs) {
            var expression = StimulusFor(path, port);
            var stimulus = graph.Add("Filters/Pixel Processor");

            stimulus.SetText("Expression", expression);
            stimulus.SetValue("X", rollX / (float)Side);
            stimulus.SetValue("Y", rollY / (float)Side);

            var fed = stimulus;

            if (IsGrey(expression)) {
                fed = graph.Add("Colour/Grayscale");
                fed.SetValue("Weight R", 1f);
                fed.SetValue("Weight G", 0f);
                fed.SetValue("Weight B", 0f);
                graph.Connect(new(stimulus.Id, "Out"), new(fed.Id, "Input"));
            }

            graph.Connect(new(fed.Id, "Out"), new(subject.Id, port));

            if (port == inputs[0]) {
                var sink = graph.Add("Output/Output");

                sink.SetText("Usage", Original);
                graph.Connect(new(fed.Id, "Out"), new(sink.Id, "Input"));
            }
        }

        for (var at = 0; at < outputs.Length; at++) {
            var sink = graph.Add("Output/Output");

            sink.SetText("Usage", Usages[at]);
            graph.Connect(new(subject.Id, outputs[at]), new(sink.Id, "Input"));
        }

        TextureGraphCompiler compiler = new(registry) {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = 7717,
            SubGraphSource = library
        };

        var compilation = compiler.Compile(graph);
        var refused = compilation.Diagnostics.Where(diagnostic => diagnostic.Severity == NodeSeverity.Error).ToArray();

        if (refused.Length > 0) {
            return new([], string.Join(" | ", refused.Select(diagnostic => diagnostic.Message)));
        }

        var plan = compilation.Value;

        using TextureUploads uploads = new(device);

        foreach (var owed in TextureGraphExternals.Upload(uploads, plan, compiler.Externals)) {
            // A bitmap is whatever was imported, and here that tiles. Anything else is a mesh map, and
            // an atlas has no reason to.
            uploads.Add(
                plan,
                owed.Image,
                Side / 2,
                Side / 2,
                path == "Source/Bitmap" ? Periodic(Side / 2) : TextureKernelHarness.Ramp(Side / 2)
            );
        }

        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        Dictionary<string, Picture> read = [];

        for (var at = 0; at < outputs.Length; at++) {
            var image = compiler.Outputs.Single(written => written.Usage == Usages[at]).Image;

            read[outputs[at]] = new(bake.ReadRaw(image));

            if (pictures is not null) {
                var file = $"{path} {outputs[at]}".Replace('/', '-').Replace(' ', '-');

                PngCodec.Save(Path.Combine(pictures, $"{file}.png"), Tiled(bake.Read(image)));
            }
        }

        var original = compiler.Outputs.Where(written => written.Usage == Original).ToArray();

        return new(read, null, original.Length == 1 ? new(bake.ReadRaw(original[0].Image)) : null, plan.WrapX && plan.WrapY);
    }

    /// <summary>A two-by-two repeat of a picture, which is what a person looks at for a seam.</summary>
    static Bitmap Tiled(Bitmap once) {
        var pixels = new byte[once.Pixels.Length * 4];
        Bitmap twice = new(once.Width * 2, once.Height * 2, pixels);

        for (var y = 0; y < twice.Height; y++) {
            once.Pixels.AsSpan(once.Offset(0, y % once.Height), once.Width * 4)
                .CopyTo(pixels.AsSpan(twice.Offset(0, y)));

            once.Pixels.AsSpan(once.Offset(0, y % once.Height), once.Width * 4)
                .CopyTo(pixels.AsSpan(twice.Offset(once.Width, y)));
        }

        return twice;
    }

    /// <summary>An RGBA8 picture that repeats exactly.</summary>
    static byte[] Periodic(int side) {
        var texels = new byte[side * side * 4];

        for (var y = 0; y < side; y++) {
            for (var x = 0; x < side; x++) {
                var u = (x + 0.5) / side;
                var v = (y + 0.5) / side;
                var at = ((y * side) + x) * 4;

                texels[at] = (byte)(127 + (120 * Math.Sin(Math.Tau * (u + v))));
                texels[at + 1] = (byte)(127 + (120 * Math.Sin(Math.Tau * ((2 * u) - v))));
                texels[at + 2] = (byte)(127 + (120 * Math.Cos(Math.Tau * (u + (2 * v)))));
                texels[at + 3] = 255;
            }
        }

        return texels;
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>One read-back image as floats.</summary>
    sealed class Picture {
        readonly float[] lanes;

        public Picture(TextureRawImage raw) {
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

        public int Channels { get; }

        float At(int x, int y, int lane) => lanes[(((y * Width) + x) * Channels) + lane];

        /// <summary>The worst colour lane's step across the wrap, and its largest ordinary step.</summary>
        public (float Seam, float Step) Seam(bool horizontal) {
            var along = horizontal ? Width : Height;
            var across = horizontal ? Height : Width;
            var worst = (Seam: 0f, Step: 0f);

            for (var lane = 0; lane < Math.Min(Channels, 3); lane++) {
                float Mean(int from, int to) {
                    var sum = 0f;

                    for (var line = 0; line < across; line++) {
                        sum += MathF.Abs(
                            horizontal
                                ? At(from, line, lane) - At(to, line, lane)
                                : At(line, from, lane) - At(line, to, lane)
                        );
                    }

                    return sum / across;
                }

                var seam = Mean(along - 1, 0);
                var step = 0f;

                for (var at = 0; at + 1 < along; at++) {
                    step = MathF.Max(step, Mean(at, at + 1));
                }

                if (seam > worst.Seam) {
                    worst = (seam, step);
                }
            }

            return worst;
        }

        /// <summary>The largest difference between this picture rolled and a bake of the rolled stimulus.</summary>
        public float Rolled(Picture other, int rollX, int rollY) {
            var worst = 0f;

            for (var lane = 0; lane < Math.Min(Channels, 3); lane++) {
                for (var y = 0; y < Height; y++) {
                    for (var x = 0; x < Width; x++) {
                        worst = MathF.Max(
                            worst,
                            MathF.Abs(other.At(x, y, lane) - At((x + rollX) % Width, (y + rollY) % Height, lane))
                        );
                    }
                }
            }

            return worst;
        }

        /// <summary>The mean difference from another picture's first lane.</summary>
        public float Difference(Picture other) {
            var sum = 0f;

            for (var y = 0; y < Height; y++) {
                for (var x = 0; x < Width; x++) {
                    sum += MathF.Abs(At(x, y, 0) - other.At(x, y, 0));
                }
            }

            return sum / (Width * Height);
        }

        /// <summary>How far the picture's values spread, so a flat fill is visible in the roll call.</summary>
        public float Contrast() {
            var low = float.MaxValue;
            var high = float.MinValue;

            for (var y = 0; y < Height; y++) {
                for (var x = 0; x < Width; x++) {
                    low = MathF.Min(low, At(x, y, 0));
                    high = MathF.Max(high, At(x, y, 0));
                }
            }

            return high - low;
        }
    }
}
