// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>
///     Doc 48 § M4's per-node previews, on a real adapter — and the refutation of the claim that they
///     needed a device-side path split out of <c>Evaluate</c>.
/// </summary>
/// <remarks>
///     ⚠ <b>Every test here opens a real device and skips loudly without one</b>, because a preview
///     that came back from the Null device would be a black image compared with a black image. The
///     adapter is named into the failures.
/// </remarks>
public class TextureGraphPreviewDeviceTests {
    /// <summary>A sink that keeps the pictures, so a test can look at what a node produced.</summary>
    sealed class Kept : ITexturePreviewImages {
        readonly Dictionary<ulong, Vixen.Core.Imaging.Bitmap> pictures = [];

        ulong next = 1;

        public IReadOnlyDictionary<ulong, Vixen.Core.Imaging.Bitmap> Pictures => pictures;

        public ulong Register(Vixen.Core.Imaging.Bitmap picture, ulong existing) {
            var image = existing == 0 ? next++ : existing;

            pictures[image] = picture;

            return image;
        }

        public void Release(ulong image) => pictures.Remove(image);
    }

    static NodeTypeRegistry Registry() {
        NodeTypeRegistry registry = new();

        NodeTypes.Register(registry);

        return registry;
    }

    /// <summary>Three stages whose pictures are three different greys: 0.25, 0.75 and 0.5.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Chosen so that "each node's own picture" is a claim a test can make.</b> A chain
    ///         of noise nodes would give pictures that all look like noise, and a preview showing the
    ///         wrong one would be indistinguishable from one showing the right one. Three flat greys
    ///         are told apart by one byte.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three stages and not two, because two do not alias.</b>
    ///         <c>TexturePoolSchedule</c> takes an op's output slot <em>before</em> giving its inputs
    ///         back — deliberately, so an op whose input dies on the same dispatch does not read what
    ///         it is writing — so a two-op chain uses two slots and nothing is ever reused. The third
    ///         op is what makes the first image's texture come back round, which is the whole
    ///         mechanism <see cref="Without_pinning_an_intermediate_reads_back_as_a_later_nodes_picture" />
    ///         is about.
    ///     </para>
    /// </remarks>
    static (NodeGraphModel Graph, NodeId Source, NodeId Shaped, NodeId Flattened) Contrasting() {
        NodeGraphModel graph = new();
        var uniform = graph.Add("Source/Uniform");
        var inverted = graph.Add("Colour/Levels");
        var halved = graph.Add("Colour/Levels");
        var output = graph.Add("Output/Output");

        uniform.SetValue("Colour", 0.25f, 0.25f, 0.25f, 1f);

        // Inverted by swapping the output range: 0.25 in, 0.75 out.
        inverted.SetValue("Output Black", 1f);
        inverted.SetValue("Output White", 0f);

        // And then flattened to 0.5 whatever arrives, which is a third distinguishable grey.
        halved.SetValue("Output Black", 0.5f);
        halved.SetValue("Output White", 0.5f);

        graph.Connect(new(uniform.Id, "Out"), new(inverted.Id, "Input"));
        graph.Connect(new(inverted.Id, "Out"), new(halved.Id, "Input"));
        graph.Connect(new(halved.Id, "Out"), new(output.Id, "Input"));

        return (graph, uniform.Id, inverted.Id, halved.Id);
    }

    static byte Middle(Vixen.Core.Imaging.Bitmap picture) {
        var pixels = picture.Pixels;
        var offset = (((picture.Height / 2) * picture.Width) + (picture.Width / 2)) * 4;

        return pixels[offset];
    }

    /// <summary>
    ///     ⚠ One ordinary <c>Evaluate</c> holds every node's picture, so no split was needed.
    /// </summary>
    /// <remarks>
    ///     <b>The claim under test, from batch 4, was that per-node previews needed a device-side
    ///     path split out of <c>Evaluate</c>.</b> They do not. What made an intermediate unreadable
    ///     was the pool, and which images the pool may reuse is the <em>plan's</em> decision — so a
    ///     plan that keeps every node's image is read back node by node from an evaluator nobody
    ///     changed.
    /// </remarks>
    [Fact]
    public void Every_nodes_own_picture_comes_out_of_one_unmodified_evaluation() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);
        var (graph, source, shaped, flattened) = Contrasting();

        TextureGraphCompiler compiler = new(Registry()) {
            BaseWidth = 32,
            BaseHeight = 32,
            PreviewEveryNode = true
        };

        var plan = compiler.Compile(graph).Value;

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan);

        var images = compiler.NodeImages.ToDictionary(written => written.Node, written => written.Image);

        var uniform = Middle(bake.Read(images[source]));
        var inverted = Middle(bake.Read(images[shaped]));
        var halved = Middle(bake.Read(images[flattened]));

        // 0.25, 0.75 and 0.5: two intermediates and a result, each its own picture, out of one bake.
        Assert.True(uniform is > 55 and < 75, $"the uniform's own picture is {uniform} on {adapter}");
        Assert.True(inverted is > 180 and < 200, $"the inverted picture is {inverted} on {adapter}");
        Assert.True(halved is > 118 and < 138, $"the flattened picture is {halved} on {adapter}");
    }

    /// <summary>
    ///     ⚠ Without the pinning the same read is a picture of the wrong node — which is what makes
    ///     the test above mean something.
    /// </summary>
    /// <remarks>
    ///     <b>The sabotage, as a test rather than as an experiment.</b> Compile the same graph with
    ///     <c>PreviewEveryNode</c> off and the uniform's image is not kept; the pool hands its texture
    ///     to the Levels node's output, and reading it back gives the <em>Levels</em> node's picture.
    ///     It does not throw, it does not look empty, and it is 0.75 where 0.25 was asked for.
    /// </remarks>
    [Fact]
    public void Without_pinning_an_intermediate_reads_back_as_a_later_nodes_picture() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);
        var (graph, source, _, flattened) = Contrasting();

        TextureGraphCompiler compiler = new(Registry()) { BaseWidth = 32, BaseHeight = 32 };
        var plan = compiler.Compile(graph).Value;

        using var evaluator = new TexturePlanEvaluator(device);
        using var bake = evaluator.Evaluate(plan);

        var images = compiler.NodeImages.ToDictionary(written => written.Node, written => written.Image);

        // The pool gave the first and the third image one texture — that is the whole point of it.
        Assert.Equal(bake.Schedule.SlotOf[images[source]], bake.Schedule.SlotOf[images[flattened]]);

        var uniform = Middle(bake.Read(images[source]));

        // So asking for the uniform's picture answers with the flattened one: 0.5, not 0.25. It does
        // not throw and it does not look empty.
        Assert.True(uniform is > 118 and < 138, $"the aliased read is {uniform} on {adapter}, not 0.5 grey");
    }

    /// <summary>The preview source answers with a picture per node, through the sink.</summary>
    [Fact]
    public void A_preview_source_registers_one_picture_per_node() {
        using var device = TextureKernelHarness.Open();
        var (graph, source, shaped, _) = Contrasting();
        Kept sink = new();

        using TextureGraphPreviews previews = new(device, () => new(Registry()), sink);

        var registry = Registry();

        // Nothing before an Update: TryGet is called from a draw and never evaluates.
        Assert.False(previews.TryGet(graph, First(graph, source), Definition(registry, graph, source), out _));
        Assert.Equal(1, previews.Pending);

        previews.Update();

        Assert.Equal(1, previews.Compilations);
        Assert.Equal(1, previews.Bakes);
        Assert.Equal(0, previews.Refusals);

        Assert.True(previews.TryGet(graph, First(graph, source), Definition(registry, graph, source), out var first));
        Assert.True(previews.TryGet(graph, First(graph, shaped), Definition(registry, graph, shaped), out var second));

        Assert.NotEqual(0ul, first.Image);
        Assert.NotEqual(first.Image, second.Image);

        // And the two pictures are the two nodes', which is the assertion a handle count cannot make.
        Assert.True(Middle(sink.Pictures[first.Image]) is > 55 and < 75);
        Assert.True(Middle(sink.Pictures[second.Image]) is > 180 and < 200);
    }

    /// <summary>An edit invalidates the graph, and the next update draws the new numbers.</summary>
    [Fact]
    public void An_edit_is_what_makes_a_preview_stale() {
        using var device = TextureKernelHarness.Open();
        var (graph, source, _, _) = Contrasting();
        Kept sink = new();

        using TextureGraphPreviews previews = new(device, () => new(Registry()), sink);

        var registry = Registry();
        var node = First(graph, source);
        var definition = Definition(registry, graph, source);

        previews.TryGet(graph, node, definition, out _);
        previews.Update();

        Assert.Equal(1, previews.Bakes);
        Assert.Equal(0, previews.Pending);

        // ⚠ An update with nothing dirty bakes nothing. Without this the counter below would be
        // satisfied by a source that re-baked every frame, which is the defect the two tiers exist
        // to prevent.
        previews.Update();
        Assert.Equal(1, previews.Bakes);

        node.SetValue("Colour", 0.75f, 0.75f, 0.75f, 1f);
        graph.Touch();

        Assert.Equal(1, previews.Pending);
        previews.Update();
        Assert.Equal(2, previews.Bakes);

        previews.TryGet(graph, node, definition, out var preview);
        Assert.True(Middle(sink.Pictures[preview.Image]) > 180);
    }

    /// <summary>A graph that does not compile keeps the picture it had and is counted.</summary>
    [Fact]
    public void A_graph_that_does_not_compile_keeps_its_last_picture() {
        using var device = TextureKernelHarness.Open();
        var (graph, source, _, _) = Contrasting();
        Kept sink = new();

        using TextureGraphPreviews previews = new(device, () => new(Registry()), sink);

        var registry = Registry();
        var node = First(graph, source);
        var definition = Definition(registry, graph, source);

        previews.TryGet(graph, node, definition, out _);
        previews.Update();

        var before = previews.TryGet(graph, node, definition, out var first) ? first.Image : 0ul;

        // A Pixel Processor whose expression does not compile, which is what half of every edit
        // looks like while it is being typed.
        var broken = graph.Add("Filters/Pixel Processor");

        broken.SetText("Expression", "not_a_name");
        graph.Connect(new(node.Id, "Out"), new(broken.Id, "A"));
        graph.Touch();

        previews.Update();

        Assert.Equal(1, previews.Refusals);
        Assert.True(previews.TryGet(graph, node, definition, out var after));
        Assert.Equal(before, after.Image);
    }

    static GraphNode First(NodeGraphModel graph, NodeId id) {
        Assert.True(graph.TryGet(id, out var node));

        return node!;
    }

    static NodeTypeDefinition Definition(NodeTypeRegistry registry, NodeGraphModel graph, NodeId id) {
        Assert.True(registry.TryGet(First(graph, id).Type, out var definition));

        return definition!;
    }
}
