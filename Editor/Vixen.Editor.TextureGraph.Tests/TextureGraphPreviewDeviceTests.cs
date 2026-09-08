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

    static byte Middle(Vixen.Core.Imaging.Bitmap picture) => At(picture, picture.Width / 2, picture.Height / 2);

    static byte At(Vixen.Core.Imaging.Bitmap picture, int x, int y) =>
        picture.Pixels[(((y * picture.Width) + x) * 4)];

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

    /// <summary>
    ///     ⚠ A graph that is <em>only</em> an imported picture is refused, not thrown out of the
    ///     plugin's frame and not baked for nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>What this looked like before <a href="https://github.com/Rikarin/Vixen/issues/1089">#1089</a>:</b>
    ///         <c>Rebuild</c> called the bare-handle <c>Evaluate</c> overload with no externals at
    ///         all, <c>ExternalViews</c> raised <c>ArgumentException</c> for the one nothing
    ///         supplied, and it left <c>Update</c> — which is a plugin's per-frame work, and which
    ///         <c>PluginHost.Update</c> answers by unloading the plugin. A <c>Source/Bitmap</c>
    ///         naming an imported image is an ordinary graph and reports nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refusal is counted and the bake is not, which is what separates "skipped" from
    ///         "drew something".</b> Asserting only that <c>Update</c> does not throw would be
    ///         satisfied by a source that had stopped rebuilding anything at all —
    ///         <see cref="A_preview_source_registers_one_picture_per_node" /> is the half that says
    ///         it still does.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every node here is downstream of the picture, which is why the whole graph is
    ///         still refused</b> — <see cref="A_node_beside_an_imported_picture_still_gets_its_own_swatch" />
    ///         is the case #1089's remainder is actually about. Zero bakes is the assertion: filling
    ///         the bitmap with a stand-in texel and dispatching over it would produce a bake whose
    ///         every picture is thrown away.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_graph_reading_an_imported_picture_is_refused_rather_than_thrown_out_of() {
        using var device = TextureKernelHarness.Open();

        NodeGraphModel graph = new();
        var picture = graph.Add("Source/Bitmap");
        var output = graph.Add("Output/Output");

        // A reference a host would resolve, and this source cannot: it holds a leased evaluator and
        // no asset database. Non-empty is all it takes — the node reports nothing and compiles.
        picture.SetText("Source", "Assets/Imported.png");
        graph.Connect(new(picture.Id, "Out"), new(output.Id, "Input"));

        Kept sink = new();

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

        Assert.False(previews.TryGet(graph, First(graph, picture.Id), Definition(Registry(), graph, picture.Id), out _));

        previews.Update();

        Assert.Equal(1, previews.Compilations);
        Assert.Equal(0, previews.Bakes);
        Assert.Equal(1, previews.Refusals);
        Assert.Equal(0, previews.Skipped);
        Assert.Empty(sink.Pictures);

        // ⚠ Refused, and the author is told rather than shown an empty canvas — #1092. This is the
        // branch that needed it most: no bake happened at all, so before this every node here drew
        // nothing, and nothing is also what a preview source that has crashed draws.
        Assert.True(
            previews.TryGet(graph, First(graph, picture.Id), Definition(Registry(), graph, picture.Id), out var after),
            "a refused graph left its nodes blank rather than saying the picture is the host's to supply"
        );

        Assert.True(after.Unavailable);
        Assert.Equal(0ul, after.Image);

    }

    /// <summary>
    ///     ⚠ A <c>Source/Gradient</c> emits an external too, so the blanket refusal blanked the
    ///     ordinary graph and not the exotic one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The half of <a href="https://github.com/Rikarin/Vixen/issues/1089">#1089</a>'s
    ///         remainder that is a regression rather than an improvement.</b> That issue names
    ///         <c>Text</c> and <c>Svg Path</c> as the externals whose bytes a compilation carries,
    ///         and neither of those is a node yet. The one that <em>is</em> shipped is
    ///         <c>TextureTables</c>: a gradient with no ramp asset, a curve with no curve asset and a
    ///         gradient map with neither each emit an external image the compiler bakes the strip for
    ///         and carries the bytes of. So the first answer to the crash — refuse any graph with an
    ///         external — took every swatch off three of the commonest nodes in the library, on a
    ///         graph containing no imported picture at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sweep is the assertion and "a picture appeared" is not.</b> A stand-in texel
    ///         and a ramp that never uploaded both produce a swatch; both produce a <em>flat</em>
    ///         one. Reading the two ends of the strip is what says the black-to-white table the
    ///         compiler baked reached the device, which is the whole of what this path does.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_gradients_baked_ramp_is_uploaded_rather_than_making_the_graph_unpreviewable() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        NodeGraphModel graph = new();
        var gradient = graph.Add("Source/Gradient");
        var output = graph.Add("Output/Output");

        graph.Connect(new(gradient.Id, "Out"), new(output.Id, "Input"));

        Kept sink = new();

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

        var registry = Registry();
        var node = First(graph, gradient.Id);
        var definition = Definition(registry, graph, gradient.Id);

        previews.TryGet(graph, node, definition, out _);
        previews.Update();

        // Nothing is owed: the strip is bytes the compilation carries, so the graph bakes whole.
        Assert.Equal(1, previews.Bakes);
        Assert.Equal(0, previews.Refusals);
        Assert.Equal(0, previews.Skipped);

        Assert.True(previews.TryGet(graph, node, definition, out var preview), $"{adapter}: no swatch on a gradient");

        var picture = sink.Pictures[preview.Image];
        var dark = At(picture, 2, picture.Height / 2);
        var light = At(picture, picture.Width - 3, picture.Height / 2);

        // ⚠ Both ends, not a difference: a ramp uploaded as zeros is flat black and a ramp that was
        // never uploaded at all cannot be told from one that was, without looking at what it says.
        Assert.True(dark < 40, $"{adapter}: the dark end of the strip is {dark}, so the ramp did not arrive");
        Assert.True(light > 215, $"{adapter}: the light end is {light}, so the ramp did not arrive");
    }

    /// <summary>
    ///     ⚠ A node beside an imported picture keeps its swatch; only what is computed from the
    ///     picture goes.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1089">#1089</a>'s remainder, and
    ///         the whole of it.</b> The crash's first answer dropped every swatch in the graph — on
    ///         the dozens of nodes upstream of the bitmap as much as on the bitmap. What is owed is
    ///         only the asset-backed picture, so it is filled with one black texel, the rest of the
    ///         graph bakes as it always did, and every node whose result is computed from that texel
    ///         is skipped rather than shown a plausible-looking lie.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Three assertions and each rules out a different wrong implementation.</b> The
    ///         uniform's own grey rules out the blanket refusal, which leaves it blank; the blend's
    ///         <em>absence</em> rules out drawing the stand-in, which would put a picture of black
    ///         under a node whose real answer is whatever the import is; and the bake count rules out
    ///         a source that has quietly stopped evaluating. ⚠ Asserting the counter alone would be
    ///         satisfied by a rebuild that skipped everything.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_node_beside_an_imported_picture_still_gets_its_own_swatch() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);

        NodeGraphModel graph = new();
        var uniform = graph.Add("Source/Uniform");
        var picture = graph.Add("Source/Bitmap");
        var blend = graph.Add("Colour/Blend");
        var output = graph.Add("Output/Output");

        uniform.SetValue("Colour", 0.25f, 0.25f, 0.25f, 1f);
        picture.SetText("Source", "Assets/Imported.png");

        graph.Connect(new(uniform.Id, "Out"), new(blend.Id, "Background"));
        graph.Connect(new(picture.Id, "Out"), new(blend.Id, "Foreground"));
        graph.Connect(new(blend.Id, "Out"), new(output.Id, "Input"));

        Kept sink = new();

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

        var registry = Registry();
        var clean = First(graph, uniform.Id);
        var cleanType = Definition(registry, graph, uniform.Id);

        previews.TryGet(graph, clean, cleanType, out _);
        previews.Update();

        Assert.Equal(1, previews.Bakes);
        Assert.Equal(0, previews.Refusals);

        // The node that has nothing to do with the import draws, which the blanket refusal is what
        // stopped.
        Assert.True(previews.TryGet(graph, clean, cleanType, out var preview), $"{adapter}: the uniform went blank");
        Assert.True(Middle(sink.Pictures[preview.Image]) is > 55 and < 75, $"{adapter}: the uniform's grey is wrong");

        // And the ones whose answer is the import's draw no picture, rather than drawing the stand-in.
        //
        // ⚠ **These two used to assert `false` and now assert something stronger** —
        // <a href="https://github.com/Rikarin/Vixen/issues/1092">#1092</a>. `false` leaves a gap, and
        // a gap under a node is exactly what this source looks like when it has stopped running
        // altogether: the answer now says *which* of the two states it is, and `Image == 0` is what
        // still rules out the black stand-in being drawn.
        Assert.True(
            previews.TryGet(graph, First(graph, picture.Id), Definition(registry, graph, picture.Id), out var missing),
            $"{adapter}: the bitmap gave no answer at all, so its swatch is missing rather than hatched"
        );

        Assert.True(missing.Unavailable, $"{adapter}: the bitmap answered as an ordinary swatch");
        Assert.Equal(0ul, missing.Image);

        Assert.True(
            previews.TryGet(graph, First(graph, blend.Id), Definition(registry, graph, blend.Id), out var tainted),
            $"{adapter}: the blend gave no answer, so the taint did not reach the node that reads the picture"
        );

        Assert.True(tainted.Unavailable, $"{adapter}: the blend drew a swatch of its own");
        Assert.Equal(0ul, tainted.Image);

        // Counted as well as shown: the counter is the instrument a test reads and the flag is what
        // the author sees, and neither replaces the other.
        Assert.True(previews.Skipped >= 2, $"{adapter}: {previews.Skipped} nodes were skipped");

        // ⚠ And the clean node is *not* hatched, which is the half that stops the flag being a
        // constant. A source that marked everything unavailable would satisfy both cases above.
        Assert.False(preview.Unavailable, $"{adapter}: the uniform was hatched, so every node is");
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

    /// <summary>⚠ A rebuild that could not draw leaves the graph dirty, which the remark claimed and the code did not.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>A refutation rather than a new feature, found wiring
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a>.</b> The no-device
    ///         branch said "a rebuild that cannot draw leaves the graph dirty" and nothing did that:
    ///         <see cref="TextureGraphPreviews.Update" /> takes the graph off the list <em>before</em>
    ///         calling the rebuild, and neither refusal put it back. So the graph was clean, the
    ///         device arriving invalidated nothing, and the swatches stayed blank until somebody
    ///         edited the graph.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is the state the editor <em>starts</em> in, not an exotic one.</b>
    ///         <c>EditorApplication</c> publishes its <c>IEditorGraphics</c> with a null
    ///         <c>Device</c> and acquires one when the window can present, and a session restore
    ///         opens its tabs before the first frame — so the ordinary path was a panel whose
    ///         previews appear only after the first keystroke, arriving through the commit that
    ///         closed the issue.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="TextureGraphPreviews.Pending" /> is the assertion and the bakes are
    ///         the consequence.</b> A test that only checked the second update's bake could be
    ///         satisfied by a source that re-compiled every frame; the pending count says the graph
    ///         was <em>remembered</em>, and the compilation count below says asking cost nothing
    ///         while there was nothing to draw on.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_rebuild_with_no_device_keeps_the_graph_pending() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);
        var (graph, source, _, _) = Contrasting();
        Kept sink = new();

        using Lease lease = new(device) { Device = null };
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

        var registry = Registry();
        var node = First(graph, source);
        var definition = Definition(registry, graph, source);

        previews.TryGet(graph, node, definition, out _);
        Assert.Equal(1, previews.Pending);

        previews.Update();

        Assert.Equal(1, previews.Refusals);
        Assert.Equal(0, previews.Bakes);

        // ⚠ The claim. Zero here is the whole defect: the graph has been forgotten and the device
        // arriving will not bring it back.
        Assert.Equal(1, previews.Pending);

        // And the refusal cost no compile, which is what asking the lease *first* buys — otherwise
        // every frame before the window is up compiles every open graph to a plan for nothing.
        Assert.Equal(0, previews.Compilations);

        lease.Device = device;
        previews.Update();

        Assert.Equal(1, previews.Bakes);
        Assert.True(
            previews.TryGet(graph, node, definition, out var preview),
            $"{adapter}: the device came back and the swatches did not"
        );

        Assert.True(Middle(sink.Pictures[preview.Image]) is > 55 and < 75, $"the picture is wrong on {adapter}");
    }

    /// <summary>⚠ <c>Drop</c> gives every picture back and asks for all of them again.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>What a device loss needs, and it is not <c>Dispose</c>.</b> The source outlives the
    ///         device because the canvas is still holding it as its <c>PreviewSource</c>; every
    ///         number it handed out names a texture the host made on the device that is going.
    ///         Disposing instead would leave the canvas drawing through a disposed object whose
    ///         <see cref="TextureGraphPreviews.Update" /> throws — from a plugin's per-frame work,
    ///         which <c>PluginHost.Update</c> answers by unloading the plugin.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both halves, and the second is the one that would have been left out.</b>
    ///         Releasing alone passes every "no stale handle" assertion and leaves the panel blank
    ///         for good; the graphs are marked dirty again, so the frame after the device comes back
    ///         has swatches on it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Drop_gives_every_picture_back_and_asks_for_them_again() {
        using var device = TextureKernelHarness.Open();
        var adapter = TextureKernelHarness.Adapter(device);
        var (graph, source, _, _) = Contrasting();
        Kept sink = new();

        using Lease lease = new(device);
        using TextureGraphPreviews previews = new(lease.Take, () => new(Registry()), sink);

        var registry = Registry();
        var node = First(graph, source);
        var definition = Definition(registry, graph, source);

        previews.TryGet(graph, node, definition, out _);
        previews.Update();

        var held = previews.Live;

        Assert.True(held > 0, $"{adapter}: nothing was registered, so this test is about a source that never ran");
        Assert.Equal(held, sink.Pictures.Count);

        previews.Drop();

        // The sink is what the host is: every picture given back, and nothing left naming a texture
        // on a device that is about to be destroyed.
        Assert.Equal(0, previews.Live);
        Assert.Empty(sink.Pictures);
        Assert.False(previews.TryGet(graph, node, definition, out _));

        // ⚠ And asked for again, which is what stops this being a panel that goes blank for good.
        Assert.Equal(1, previews.Pending);

        previews.Update();

        Assert.Equal(2, previews.Bakes);
        Assert.True(previews.TryGet(graph, node, definition, out var preview));
        Assert.True(Middle(sink.Pictures[preview.Image]) is > 55 and < 75, $"the picture is wrong on {adapter}");
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
