// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.NodeGraph;
using Vixen.TextureGraph;
using Vixen.TextureGraph.Drawing;
using Xunit;

namespace Tests;

/// <summary>
///     Patina #139's two nodes end to end: a graph compiled, its drawing filled by <see cref="TextureDrawings" /> and the
///     plan baked on a device, against closed forms.
/// </summary>
/// <remarks>
///     <para>
///         <b>Through the compiler, the fill and the evaluator, not around them</b>, so a node that wrote the wrong
///         field into its reference, a fill that drew at the wrong size or a <c>Bitmap</c> op that resampled what it
///         should have copied is as red as a rasteriser that filled the wrong shape.
///     </para>
///     <para>
///         The oracle is ink: letters set at twice the size cover four times the texels, a bake at twice the
///         resolution covers four times the texels, and a rectangle over half the frame covers half the picture.
///     </para>
/// </remarks>
public class TextureDrawingDeviceTests(ITestOutputHelper output) {
    const int Side = 128;

    /// <summary>A half-red, half-empty frame.</summary>
    const string HalfRed =
        """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect width="50" height="100" fill="#ff0000"/></svg>""";

    sealed record Baked(float[] Lanes, int Width, int Height, int Channels) {
        public float At(int x, int y, int lane) => Lanes[(y * Width + x) * Channels + lane];

        public float Ink => Enumerable.Range(0, Width * Height).Sum(at => Lanes[at * Channels]);
    }

    static Baked Bake(
        string path,
        Action<GraphNode> configure,
        Func<string, byte[]> read,
        int bakeLevel = 0,
        bool tiling = true
    ) {
        using var device = TextureKernelHarness.Open();

        NodeGraphModel graph = new();

        graph.Settings[TextureGraphSettings.BitDepth] = "32";

        if (!tiling) {
            graph.Settings[TextureGraphSettings.TilingX] = "false";
            graph.Settings[TextureGraphSettings.TilingY] = "false";
        }

        var subject = graph.Add(path);

        configure(subject);

        var sink = graph.Add("Output/Output");

        sink.SetText("Usage", "baseColor");
        graph.Connect(new(subject.Id, "Out"), new(sink.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        TextureGraphCompiler compiler =
            new(registry) { BaseWidth = Side, BaseHeight = Side, BakeLevelOffset = bakeLevel };
        var compilation = compiler.Compile(graph);

        Assert.Empty(compilation.Diagnostics.Where(static diagnostic => diagnostic.Severity == NodeSeverity.Error));

        var plan = compilation.Value;

        using TextureUploads uploads = new(device);

        foreach (var owed in TextureGraphExternals.Upload(uploads, plan, compiler.Externals)) {
            Assert.True(TextureDrawings.Draws(owed.Asset), $"'{owed.Asset}' is owed and is not a drawing");
            TextureDrawings.Fill(uploads, plan, owed, read);
        }

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan, uploads.Externals);

        var raw = bake.ReadRaw(compiler.Outputs.Single().Image);
        var lanes = new float[raw.Texels.Length / 4];

        for (var at = 0; at < lanes.Length; at++) {
            lanes[at] = BitConverter.ToSingle(raw.Texels.AsSpan(at * 4, 4));
        }

        return new(lanes, raw.Width, raw.Height, lanes.Length / (raw.Width * raw.Height));
    }

    static byte[] NoFile(string name) => throw new FileNotFoundException($"no file '{name}' in this test");

    static byte[] OpenSans() {
        using var stream =
            typeof(TextureDrawingDeviceTests).Assembly.GetManifestResourceStream("Fonts.OpenSans-Regular.ttf");

        Assert.NotNull(stream);

        using MemoryStream bytes = new();

        stream.CopyTo(bytes);

        return bytes.ToArray();
    }

    [Fact]
    public void Letters_twice_the_size_cover_four_times_the_texels() {
        var small = Bake(
            "Source/Text",
            static node => {
                node.SetText("Text", "HEH");
                node.SetValue("Size", 0.2f);
            },
            NoFile
        );
        var large = Bake(
            "Source/Text",
            static node => {
                node.SetText("Text", "HEH");
                node.SetValue("Size", 0.4f);
            },
            NoFile
        );

        output.WriteLine($"ink: {small.Ink} at 0.2, {large.Ink} at 0.4");

        // The instrument first: no ink would make the ratio a division by nothing.
        Assert.True(small.Ink > 100f, $"'HEH' at a fifth of the height drew {small.Ink} texels of ink");
        Assert.Equal(4f, large.Ink / small.Ink, 0.12f);
    }

    [Fact]
    public void A_bake_at_twice_the_resolution_sets_the_same_words_at_twice_the_texels() {
        static void Words(GraphNode node) => node.SetText("Text", "Patina");

        var authored = Bake("Source/Text", Words, NoFile);
        var doubled = Bake("Source/Text", Words, NoFile, -1);

        Assert.Equal(Side * 2, doubled.Width);
        Assert.Equal(4f, doubled.Ink / authored.Ink, 0.12f);
    }

    [Fact]
    public void Lines_stack_down_the_picture_and_a_named_font_is_read_through_the_host() {
        List<string> asked = [];

        byte[] Read(string name) {
            asked.Add(name);

            return OpenSans();
        }

        var two = Bake(
            "Source/Text",
            static node => {
                node.SetText("Text", "I\nI");
                node.SetText("Font", "fonts/OpenSans.ttf");
                node.SetValue("Line Spacing", 1.5f);
            },
            Read
        );

        Assert.Equal(["fonts/OpenSans.ttf"], asked);

        // Two strokes of an I, one above the other, centred on the middle row: ink in both halves, none across it.
        static float Band(Baked picture, int from, int to) =>
            Enumerable.Range(from, to - from).Sum(y => Enumerable.Range(0, Side).Sum(x => picture.At(x, y, 0)));

        Assert.True(
            Band(two, 0, Side / 2) > 20f && Band(two, Side / 2, Side) > 20f,
            $"the halves hold {Band(two, 0, Side / 2)} and {Band(two, Side / 2, Side)}"
        );
        Assert.Equal(Band(two, 0, Side / 2), Band(two, Side / 2, Side), Band(two, 0, Side) * 0.05f);
        Assert.Equal(0f, Enumerable.Range(0, Side).Sum(x => two.At(x, Side / 2, 0)), 0.5f);
    }

    [Fact]
    public void A_word_across_the_seam_comes_back_in_at_the_far_edge_only_when_the_graph_tiles() {
        static void Edge(GraphNode node) {
            node.SetText("Text", "MMMM");
            node.SetValue("Position X", 1f);
        }

        var tiling = Bake("Source/Text", Edge, NoFile);
        var clamped = Bake("Source/Text", Edge, NoFile, tiling: false);

        static float Columns(Baked picture, int from, int to) =>
            Enumerable.Range(0, Side).Sum(y => Enumerable.Range(from, to - from).Sum(x => picture.At(x, y, 0)));

        Assert.True(Columns(tiling, 0, 16) > 20f, "the word's right half is not at the left edge of a tiling graph");
        Assert.Equal(0f, Columns(clamped, 0, 16), 0.5f);
        Assert.Equal(tiling.Ink, clamped.Ink * 2f, tiling.Ink * 0.08f);
    }

    [Fact]
    public void A_vector_draws_its_colours_decoded_to_linear_and_its_coverage_as_a_mask() {
        var colour = Bake(
            "Source/Vector",
            static node => node.SetText("File", "logo.svg"),
            static _ => Encoding.UTF8.GetBytes(HalfRed)
        );
        var mask = Bake(
            "Source/Vector",
            static node => {
                node.SetText("File", "logo.svg");
                node.SetText("Draw", "Coverage");
            },
            static _ => Encoding.UTF8.GetBytes(HalfRed)
        );

        Assert.Equal(4, colour.Channels);
        Assert.Equal(1f, colour.At(10, 64, 0), 0.01f);
        Assert.Equal(0f, colour.At(10, 64, 1), 0.01f);
        Assert.Equal(1f, colour.At(10, 64, 3), 0.01f);
        Assert.Equal(0f, colour.At(100, 64, 3), 0.01f);

        Assert.Equal(1, mask.Channels);
        Assert.Equal(Side * Side / 2f, mask.Ink, 2f);
    }

    [Fact]
    public void A_vector_with_no_file_is_refused_by_the_compiler_naming_the_node() {
        NodeGraphModel graph = new();
        var vector = graph.Add("Source/Vector");
        var sink = graph.Add("Output/Output");

        sink.SetText("Usage", "baseColor");
        graph.Connect(new(vector.Id, "Out"), new(sink.Id, "Input"));

        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        var compilation = new TextureGraphCompiler(registry) { BaseWidth = Side, BaseHeight = Side }.Compile(graph);

        Assert.Contains(
            compilation.Diagnostics,
            diagnostic => diagnostic.Id == "TG0002" && diagnostic.Node == vector.Id
        );
    }

    [Fact]
    public void A_file_that_is_no_font_is_refused_with_a_sentence() {
        var failure = Assert.ThrowsAny<Exception>(static () => Bake(
                "Source/Text",
                static node => node.SetText("Font", "notes.txt"),
                static _ => Encoding.UTF8.GetBytes("not a font")
            )
        );

        Assert.IsType<InvalidDataException>(failure);
        Assert.Contains("notes.txt", failure.Message, StringComparison.Ordinal);
    }
}
