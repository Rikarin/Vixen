// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     A frame that drew, and the list of things it drew differently than it was asked to.
/// </summary>
/// <remarks>
///     <para>
///         <b>The state a survey found in roughly fifteen places and nothing could see.</b> A node
///         asked for an input it did not get takes a designed-in fallback, draws, and leaves every
///         counter healthy — so "the frame rendered and every number is fine" was compatible with a
///         sky smeared in one direction, cascades fitted to a frustum at the origin, and ambient
///         light combined at the wrong incidence. Each node knew; none of them could be asked.
///     </para>
///     <para>
///         What is pinned here is the seam rather than any one node: one name on
///         <see cref="SceneRenderer" />, one walk over <see cref="SceneRenderer.Nested" />, and the
///         two properties that make the answer worth reading — that it describes <em>this</em> frame,
///         and that a node type nobody had written when the walk was written is still in the list.
///     </para>
/// </remarks>
public class DegradationTests {
    /// <summary>A node the walk has never heard of, which is the point of putting it on the base.</summary>
    sealed class Fussy : SceneRenderer {
        public string? Reason { get; set; }

        protected internal override void Build(GraphicsCompositor compositor, CompositorFrame frame) =>
            Degrade(Reason);
    }

    static GraphicsCompositor Compositor() => new(new RenderSystem());

    /// <summary>A shadow node wired to a caster stage and nothing else, which is the state under test.</summary>
    static ShadowMapRenderer Shadows(GraphicsCompositor compositor) =>
        new() {
            Name = "Sun",
            CasterStage = compositor.System.AddStage(new("ShadowCaster")),
            Atlas = "ShadowAtlas",
            CascadeCount = 4,
            LightDirection = new(0f, -1f, 0f)
        };

    static List<(string Node, string Reason)> Read(GraphicsCompositor compositor) {
        List<(string, string)> found = [];
        var reported = compositor.Degradations(found);

        // The return and the collection have to agree, because a host that only wants "is anything
        // wrong" will read the number and never look in the list.
        Assert.Equal(found.Count, reported);
        return found;
    }

    /// <summary>The normal answer is nothing at all.</summary>
    /// <remarks>
    ///     Worth its own test because it is the one that must stay cheap: a host asking every frame
    ///     is asking a question whose answer is almost always empty, and a list that filled up with
    ///     healthy nodes would be a report nobody reads.
    /// </remarks>
    [Fact]
    public void A_frame_that_got_everything_it_asked_for_reports_nothing() {
        var compositor = Compositor();
        var node = new Fussy { Name = "Fine" };

        compositor.Game = node;
        node.Build(compositor, null!);

        Assert.Empty(Read(compositor));
    }

    /// <summary>One name, collected through a base class that knows no node types.</summary>
    [Fact]
    public void A_node_that_degraded_is_named_with_its_reason() {
        var compositor = Compositor();
        var node = new Fussy { Name = "Sky", Reason = "no View, so every pixel samples one direction" };

        compositor.Game = node;
        node.Build(compositor, null!);

        var (named, reason) = Assert.Single(Read(compositor));

        Assert.Equal("Sky", named);
        Assert.Equal("no View, so every pixel samples one direction", reason);
    }

    /// <summary>Down the tree, in order, without knowing what any of the nodes are.</summary>
    /// <remarks>
    ///     The whole argument for a shared seam rather than fifteen bespoke properties: the walk is
    ///     <see cref="SceneRenderer.Nested" />, which already existed for
    ///     <see cref="GraphicsCompositor.Apply" />, so a node added to the engine next year is in the
    ///     report with nothing rewritten.
    /// </remarks>
    [Fact]
    public void The_walk_reaches_nested_nodes_in_tree_order() {
        var compositor = Compositor();
        var first = new Fussy { Name = "First", Reason = "one" };
        var healthy = new Fussy { Name = "Healthy" };
        var second = new Fussy { Name = "Second", Reason = "two" };

        var tree = new SceneRendererSequence { Children = { first, healthy, second } };

        compositor.Game = tree;
        tree.Build(compositor, null!);

        Assert.Equal([("First", "one"), ("Second", "two")], Read(compositor));
    }

    /// <summary>A node that recovers stops being reported, because the claim is about this frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The property that separates a report from a scar.</b>
    ///     <c>TerrainSceneRenderer.PreviewReason</c> already said this about itself and it is what
    ///     makes the answer actionable: a developer reading the list is looking at the frame in front
    ///     of them, not at the worst frame since the process started. It is also the reason
    ///     <see cref="SceneRenderer.Degrade" /> has to be called on the healthy path too.
    /// </remarks>
    [Fact]
    public void A_node_that_recovers_leaves_the_report() {
        var compositor = Compositor();
        var node = new Fussy { Name = "Flaky", Reason = "the camera went away" };

        compositor.Game = node;
        node.Build(compositor, null!);

        Assert.Single(Read(compositor));

        node.Reason = null;
        node.Build(compositor, null!);

        Assert.Empty(Read(compositor));
    }

    /// <summary>
    ///     A node that is switched off is not reported, which is the opposite of
    ///     <see cref="GraphicsCompositor.Apply" />.
    /// </summary>
    /// <remarks>
    ///     An overlay has to reach a disabled node because it is what the node comes back to. A
    ///     degrade is a claim about a draw, and a node that is off did not draw — so its last enabled
    ///     frame's reason is exactly the stale answer the test above exists to prevent.
    /// </remarks>
    [Fact]
    public void A_disabled_node_is_not_reported() {
        var compositor = Compositor();
        var node = new Fussy { Name = "Off", Reason = "no View" };

        compositor.Game = node;
        node.Build(compositor, null!);

        Assert.Single(Read(compositor));

        node.Enabled = false;

        Assert.Empty(Read(compositor));
    }

    /// <summary>
    ///     The first converted site: a shadow node with no camera says so, and says what it did.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Verified against the old code before it was written.</b> Every assertion below was
    ///     unreachable: the node fitted four cascades to a fabricated frustum at its authored
    ///     <c>Eye</c>, filled the atlas with casters from somewhere the player was not, published the
    ///     matrices, and reported <c>Cascades.Length == 4</c> like any healthy frame. There was no
    ///     property, no counter and no log line that differed between this and a correct fit.
    /// </remarks>
    [Fact]
    public void A_shadow_node_with_no_camera_says_which_input_was_missing() {
        var compositor = Compositor();

        var shadows = Shadows(compositor);

        Assert.Null(shadows.Camera);
        compositor.Game = shadows;
        shadows.Collect(compositor);

        // The frame still has its four cascades, which is the entire difficulty: nothing about the
        // draw distinguishes this from a fitted one.
        Assert.Equal(4, shadows.Cascades.Length);

        var (node, reason) = Assert.Single(Read(compositor));

        Assert.Equal("Sun", node);
        Assert.Contains("no Camera", reason, StringComparison.Ordinal);

        // Both halves: the input that was missing, and what happened instead.
        Assert.Contains("fabricated frustum", reason, StringComparison.Ordinal);
    }

    /// <summary>A node with a camera but a source holding no sun names the other input.</summary>
    /// <remarks>
    ///     ⚠ <b>The branch that had no test, and it is the one sample 03 fired.</b> A source is wired
    ///     and answers null, which is not the same shape as no source at all: the host did its half,
    ///     and the scene is the thing with no directional light in it. The reason has to name the sun
    ///     rather than the camera, because a reader who checks the camera wiring on this line is
    ///     reading the wrong half of the frame.
    /// </remarks>
    [Fact]
    public void A_shadow_node_whose_source_holds_no_sun_says_so() {
        var compositor = Compositor();

        var view = new RenderView("Main") {
            Camera = new(new(0f, 2f, 0f), new(0f, 0f, 1f), new(0f, 1f, 0f), 1f, 1.777f, 0.1f, 500f)
        };

        var shadows = Shadows(compositor);

        shadows.Camera = view;
        shadows.Sun = new Empty();

        compositor.Game = shadows;
        shadows.Collect(compositor);

        var (node, reason) = Assert.Single(Read(compositor));

        Assert.Equal("Sun", node);
        Assert.Contains("no Sun", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("no Camera", reason, StringComparison.Ordinal);

        // Both halves, on Degrade's terms: the input, and what was fitted instead of it.
        Assert.Contains("authored", reason, StringComparison.Ordinal);
    }

    /// <summary>A source that answers null, which is a scene with no directional light in it.</summary>
    sealed class Empty : ISunSource {
        public RenderLight? Sun => null;
    }

    /// <summary>And a node given both of its inputs reports nothing.</summary>
    [Fact]
    public void A_shadow_node_given_a_camera_and_a_sun_reports_nothing() {
        var compositor = Compositor();

        var view = new RenderView("Main") {
            Camera = new(new(0f, 2f, 0f), new(0f, 0f, 1f), new(0f, 1f, 0f), 1f, 1.777f, 0.1f, 500f)
        };

        var shadows = Shadows(compositor);

        shadows.Camera = view;
        shadows.Sun = new Sunlight { Direction = Vector3.Normalize(new(0.3f, -1f, 0.2f)) };

        compositor.Game = shadows;
        shadows.Collect(compositor);

        Assert.Empty(Read(compositor));
    }

    /// <summary>A stand-in for whatever a document names as the frame's sun.</summary>
    sealed class Sunlight : ISunSource {
        public Vector3 Direction { get; init; }

        public RenderLight? Sun => new() { Kind = LightKind.Directional, Direction = Direction };
    }

    // --- The two passes every other node is made of -------------------------

    /// <summary>
    ///     ⚠ <b>The documented case.</b> <see cref="FullScreenRenderer" /> returned above its resolve
    ///     when <c>CompositorBuilder</c> had left <see cref="FullScreenRenderer.Device" /> and
    ///     <see cref="FullScreenRenderer.Modules" /> unset — so the effect system recorded no miss,
    ///     and the tonemap, the node that writes the swapchain, silently did not run.
    /// </summary>
    /// <remarks>
    ///     Both halves are asserted separately, because the half that matters to somebody staring at
    ///     a black frame is the second one: "no Modules" names an input, and "this pass was never
    ///     declared" is what tells them the target holds whatever the graph last aliased into it.
    /// </remarks>
    [Fact]
    public void A_full_screen_pass_with_no_modules_says_the_pass_was_never_declared() {
        using var fixture = new Passes();
        using var h = fixture.FullScreen(modules: false);

        fixture.Frame(h);

        var (node, reason) = Assert.Single(Read(h.Compositor));

        Assert.Equal("Tonemap", node);
        Assert.Contains("no Modules", reason, StringComparison.Ordinal);
        Assert.Contains("never declared", reason, StringComparison.Ordinal);
    }

    /// <summary>And the same node, wired, reports nothing at all.</summary>
    [Fact]
    public void A_full_screen_pass_that_was_given_everything_reports_nothing() {
        using var fixture = new Passes();
        using var h = fixture.FullScreen();

        fixture.Frame(h);

        Assert.Empty(Read(h.Compositor));
    }

    /// <summary>
    ///     A pass whose effect did not resolve names the shader, not just the fact of a miss.
    /// </summary>
    /// <remarks>
    ///     <c>EffectSystem.Misses</c> already collects the keys, and that is a different question: it
    ///     answers "what failed to compile", and this answers "which node in this document drew
    ///     nothing because of it". A frame with nine misses and one node that cared is the case where
    ///     the two differ.
    /// </remarks>
    [Fact]
    public void A_full_screen_pass_whose_effect_did_not_resolve_names_the_shader() {
        using var fixture = new Passes(compiles: false);
        using var h = fixture.FullScreen();

        fixture.Frame(h);

        var (_, reason) = Assert.Single(Read(h.Compositor));

        Assert.Contains("'Tonemap' did not resolve", reason, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The property that makes the answer worth reading: the same node, recovering.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>This is what the helper-returning-<c>string?</c> shape buys.</b> A guard that sets a
    ///     reason and returns is only half of it — the healthy path has to clear it, every frame, or
    ///     the report is a scar rather than a description of the frame in front of you. Routing the
    ///     whole body through one <c>Degrade(Declare(…))</c> makes <c>return null</c> the only way out
    ///     the bottom.
    /// </remarks>
    [Fact]
    public void A_full_screen_pass_that_is_handed_its_modules_leaves_the_report() {
        using var fixture = new Passes();
        using var h = fixture.FullScreen(modules: false);

        fixture.Frame(h);
        Assert.Single(Read(h.Compositor));

        h.Pass.Modules = fixture.Describer;
        fixture.Frame(h);

        Assert.Empty(Read(h.Compositor));
    }

    /// <summary>A dispatch that does not happen leaves its buffer holding the last value written.</summary>
    /// <remarks>
    ///     Which for <c>AutoExposureRenderer</c>'s chain is a plausible exposure rather than an
    ///     obviously wrong one — the shape of failure this engine is characteristically bad at seeing.
    /// </remarks>
    [Fact]
    public void A_compute_dispatch_with_no_pipelines_says_what_keeps_its_last_value() {
        using var fixture = new Passes();
        using var h = fixture.Compute(pipelines: false);

        fixture.Frame(h);

        var (node, reason) = Assert.Single(Read(h.Compositor));

        Assert.Equal("Reduce", node);
        Assert.Contains("no Pipelines", reason, StringComparison.Ordinal);
        Assert.Contains("last value written to it", reason, StringComparison.Ordinal);
    }

    /// <summary>An empty group count is a dispatch nobody would notice was missing.</summary>
    /// <remarks>
    ///     The reason quotes the extents, because a zero in one of three is the case: a chain that
    ///     halved to 1×1 and then to 0 dispatches nothing, and two of the three numbers look fine.
    /// </remarks>
    [Fact]
    public void A_compute_dispatch_with_an_empty_group_count_quotes_the_extents() {
        using var fixture = new Passes();
        using var h = fixture.Compute();

        h.Dispatch.Groups = new(8, 0, 1);
        fixture.Frame(h);

        var (_, reason) = Assert.Single(Read(h.Compositor));

        Assert.Contains("8×0×1", reason, StringComparison.Ordinal);
    }

    /// <summary>And a dispatch with a pipeline and real groups reports nothing.</summary>
    [Fact]
    public void A_compute_dispatch_that_was_given_everything_reports_nothing() {
        using var fixture = new Passes();
        using var h = fixture.Compute();

        fixture.Frame(h);

        Assert.Empty(Read(h.Compositor));
    }

    // --- The fixture for the two above --------------------------------------

    /// <summary>A device, an effect provider and the two node shapes every post chain is built of.</summary>
    /// <remarks>
    ///     Deliberately thinner than <c>PostProcessTests</c>'s: nothing here asserts about what was
    ///     drawn, only about what was said, so the pass needs to reach its declaration and no further.
    /// </remarks>
    sealed class Passes : IDisposable {
        readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
        readonly EffectSystem effects = new();
        readonly DescriptorAllocator allocator;

        public EffectPipelineDescriber Describer { get; }

        public Passes(bool compiles = true) {
            allocator = new(device);
            Describer = new(device);

            if (compiles) {
                effects.AddProvider(new Compiles());
            }
        }

        public Frames FullScreen(bool modules = true) {
            var pass = new FullScreenRenderer {
                Name = "Tonemap",
                ShaderName = "Tonemap",
                Modules = modules ? Describer : null,
                Device = device
            };

            pass.ColourTargets.Add("Display");
            pass.Descriptors.Allocator = allocator;

            var system = new RenderSystem();
            var compositor = new GraphicsCompositor(system) { FrameSize = new(64, 64), Game = pass };

            compositor.Imports["Display"] = Imported("Display");

            return new() { System = system, Compositor = compositor, Graph = new(device), Node = pass };
        }

        public Frames Compute(bool pipelines = true) {
            var dispatch = new ComputeRenderer {
                Name = "Reduce",
                ShaderName = "Reduce",
                Pipelines = pipelines ? new ComputePipelineCache(device) : null,
                Groups = new(8, 8, 1)
            };

            // A write, because the graph drops a pass that reads and writes nothing — and this test
            // is about a pass that reaches its declaration, not one the graph culled.
            dispatch.Writes.Add("Metered");
            dispatch.Descriptors.Allocator = allocator;

            var system = new RenderSystem();
            var compositor = new GraphicsCompositor(system) { FrameSize = new(64, 64), Game = dispatch };

            compositor.Imports["Metered"] = Imported("Metered", storage: true);

            return new() { System = system, Compositor = compositor, Graph = new(device), Node = dispatch };
        }

        public void Frame(Frames h) {
            var list = device.BeginCommandList();

            allocator.BeginFrame();
            h.Graph.Reset();
            h.Compositor.Build(h.Graph, effects, device);
            h.Graph.Execute(list);

            list.Finish();
            device.GraphicsQueue.Submit([list]);
        }

        ImportedTexture Imported(string name, bool storage = false) {
            var usage = TextureUsage.ColourTarget | TextureUsage.Sampled;

            var description = new TextureDescription(
                PixelFormat.Rgba16Float,
                64,
                64,
                storage ? usage | TextureUsage.Storage : usage,
                Name: name
            );

            var texture = device.CreateTexture(description);
            return new(texture, device.CreateTextureView(texture), description);
        }

        static Effect Compiled(EffectKey key) =>
            new() {
                Key = key,
                Stages = [
                    new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                    new(ShaderStage.Fragment, [5, 6, 7, 8], "main"),
                    new(ShaderStage.Compute, [9, 10, 11, 12], "main")
                ]
            };

        sealed class Compiles : IEffectProvider {
            public Effect? TryGet(EffectKey key) => Compiled(key);
        }

        public void Dispose() {
            allocator.Dispose();
            device.Dispose();
        }
    }

    /// <summary>One compositor, its graph, and the node under test.</summary>
    sealed class Frames : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required SceneRenderer Node { get; init; }

        public FullScreenRenderer Pass => (FullScreenRenderer)Node;

        public ComputeRenderer Dispatch => (ComputeRenderer)Node;

        public void Dispose() {
            (Node as IDisposable)?.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
    }
}
