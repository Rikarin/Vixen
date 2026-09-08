// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Editor.TextureGraph;
using Vixen.Graphics;
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

    /// <summary>The one evaluator for a device, and a count of who asked for it.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a host lends, in miniature</b> — <c>TexturingModule.Evaluator</c> is the real
    ///         one and <c>LentEvaluator</c> in <c>Vixen.Editor.Texturing.Tests</c> is the same idea
    ///         one assembly over. This assembly cannot reach either, so it has its own.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two counters, for <c>PreviewLeaseTests</c>' reason.</b> <see cref="Built" />
    ///         catches a lender that quietly builds one per call and cannot catch the opposite: a
    ///         consumer that stopped asking and built its own leaves <see cref="Built" /> at one and
    ///         every assertion about it green. <see cref="Asks" /> is the half that sees that, and it
    ///         is expected exactly rather than as a floor.
    ///     </para>
    /// </remarks>
    sealed class Lease : IDisposable {
        readonly IGraphicsDevice held;

        TexturePlanEvaluator? evaluator;

        /// <summary>Lends for one device.</summary>
        /// <param name="device">The device.</param>
        public Lease(IGraphicsDevice device) {
            held = device;
            Device = device;
        }

        /// <summary>How many evaluators this lender has made.</summary>
        public int Built { get; private set; }

        /// <summary>How many times one was asked for.</summary>
        public int Asks { get; private set; }

        /// <summary>The device this lender lends for, or null once the host has lost it.</summary>
        /// <remarks>
        ///     ⚠ <b>Settable, because a source that cached the device could not be told apart from
        ///     one that re-reads it.</b> Both answer the same evaluator for as long as the device
        ///     lives; the difference only shows on the frame after it dies.
        /// </remarks>
        public IGraphicsDevice? Device { get; set; }

        /// <summary>Hands out the one evaluator for the device the host currently has.</summary>
        /// <returns>The evaluator, which the caller does not own, or null when there is no device.</returns>
        public TexturePlanEvaluator? Take() {
            Asks++;

            if (Device is null) {
                return null;
            }

            if (evaluator is not null) {
                return evaluator;
            }

            Built++;

            return evaluator = new(held);
        }

        /// <inheritdoc />
        public void Dispose() => evaluator?.Dispose();
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

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

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

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

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

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

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

    /// <summary>⚠ The source owns no evaluator: it asks the lease, once per rebuild.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The property that makes <a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a>
    ///         wireable, and it was false.</b> This type built its own <c>TexturePlanEvaluator</c>
    ///         in its constructor, so constructing one in <c>TexturingModule</c> beside the
    ///         evaluator both existing panes share would have made a session hold two — a second
    ///         pipeline and shader module per kernel and output format, arriving through the commit
    ///         that closed the issue. That is exactly the cost <a
    ///         href="https://github.com/Rikarin/Vixen/issues/820">#820</a> and <a
    ///         href="https://github.com/Rikarin/Vixen/issues/988">#988</a> established the lease to
    ///         prevent, and no test in either assembly could have seen it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both counters, and the asks are exact.</b> <c>Built</c> alone is satisfied by a
    ///         source that asked once on the way in and cached; <c>Asks</c> alone is satisfied by a
    ///         lender that builds a fresh evaluator every time. Two rebuilds is the smallest script
    ///         where "asks every time" and "asked once" differ — <c>PreviewLeaseTests</c> makes the
    ///         same argument for the two panes one assembly over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And nothing is asked before the first <c>Update</c>.</b> <c>TryGet</c> runs from
    ///         a draw and must never touch the device; a source that took its evaluator in the
    ///         constructor would show a 1 here on a line that has evaluated nothing.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_preview_source_takes_its_evaluator_from_the_lease_on_every_rebuild() {
        using var device = TextureKernelHarness.Open();
        var (graph, source, _, _) = Contrasting();
        Kept sink = new();

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

        var registry = Registry();
        var node = First(graph, source);
        var definition = Definition(registry, graph, source);

        previews.TryGet(graph, node, definition, out _);

        // The instrument, before the claim: a draw has happened and nothing has been evaluated, so a
        // source that took an evaluator on the way in is already visible here.
        Assert.Equal(0, lease.Asks);
        Assert.Equal(0, lease.Built);

        previews.Update();

        Assert.Equal(1, previews.Bakes);
        Assert.Equal(1, lease.Asks);

        node.SetValue("Colour", 0.75f, 0.75f, 0.75f, 1f);
        graph.Touch();
        previews.Update();

        // ⚠ The half that separates "asked once, and kept it" from "asks every time".
        Assert.Equal(2, previews.Bakes);
        Assert.Equal(2, lease.Asks);

        // And the lender made one, which is the whole point of lending.
        Assert.Equal(1, lease.Built);

        // ⚠ The half a cached device hides. The host loses its device; the source must ask, be told
        // there is none, and draw nothing — rather than hand the lease a device that is gone, which
        // is the one question a lease cannot answer safely. Under the old shape the device was a
        // constructor argument in a field, so this line asked for the *dead* device's evaluator and
        // every count above stayed exactly as green as it is now.
        lease.Device = null;

        node.SetValue("Colour", 0.25f, 0.25f, 0.25f, 1f);
        graph.Touch();
        previews.Update();

        Assert.Equal(3, lease.Asks);
        Assert.Equal(2, previews.Bakes);
        Assert.Equal(1, lease.Built);
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
