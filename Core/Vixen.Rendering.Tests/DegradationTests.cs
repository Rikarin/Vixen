// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
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
}
