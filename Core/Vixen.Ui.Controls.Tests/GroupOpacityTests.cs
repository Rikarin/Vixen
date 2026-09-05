// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A translucent subtree, rasterised, and read back a pixel at a time.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The one property that separates a group from a multiplier is <i>self-overlap</i>, so
///         every test here is built out of two children that cover each other.</b> A half-opaque panel
///         containing one child is drawn identically by both models — which is why the collapse in
///         <see cref="DrawList.Collapse" /> is safe, and also why a test written on a single child
///         would pass against the bug it is supposed to catch.
///     </para>
///     <para>
///         ⚠ <b>Pixels asserted against arithmetic rather than against a committed picture.</b> The
///         numbers below are what CSS Compositing 1 § 3 says the answer is, worked out by hand from the
///         stated colours — so this cannot be made to pass by accepting a new screenshot, and a
///         disagreement is a claim about the model rather than about a file.
///     </para>
/// </remarks>
public class GroupOpacityTests {
    /// <summary>A harness whose documents composite, which is now also the default.</summary>
    /// <remarks>
    ///     ⚠ <b><s>Opted into per test rather than switched on globally, because the renderer that
    ///     ships cannot composite yet.</s> It ships and it composites, so this is no longer an opt-in
    ///     — and it is kept anyway.</b> See <see cref="DrawListBuilder.Compositing" />: the fear was
    ///     that switching it on globally would put the visual baselines, which render on the CPU where
    ///     compositing always worked, ahead of <c>Vixen.Ui.Renderer</c>, so the committed screenshots
    ///     would be pictures of something the editor does not draw. What closed that gap was
    ///     <c>UiRenderer.Compose</c> and the hosts calling it, not this line. It stays because these
    ///     tests are about the compositing model itself and would silently become tests of the
    ///     multiplier if the default ever moved back.
    /// </remarks>
    static UiTest Compositing(float width, float height) {
        var ui = UiTest.Create(width, height);
        ui.Document.Compositing = true;

        return ui;
    }

    /// <summary>Two opaque children, the second covering the first, inside a translucent parent.</summary>
    /// <remarks>
    ///     The parent paints nothing of its own, so what reaches the surface is the second child alone
    ///     wherever they overlap — and the first child alone where it does not.
    /// </remarks>
    static UiTest Stacked(string parentStyle, float alpha = 0.5f) {
        var ui = Compositing(40f, 40f);

        ui.Load(
            $$"""
            root { width: 40px; height: 40px; background-color: #000000; }
            .group { position: absolute; left: 0; top: 0; width: 40px; height: 40px; opacity: {{alpha}}; {{parentStyle}} }
            .lower { position: absolute; left: 0; top: 0; width: 40px; height: 20px; background-color: #ff0000; }
            .upper { position: absolute; left: 0; top: 0; width: 40px; height: 10px; background-color: #00ff00; }
            """
        );

        var group = ui.Create("div", null, "group", "group");
        ui.Create("div", group, "lower", "lower");
        ui.Create("div", group, "upper", "upper");
        ui.Frame();

        return ui;
    }

    /// <summary>Where two children overlap, only the top one shows — faded once, not twice.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole bug, stated as a number.</b> Fading each child separately draws the
    ///     green at 50% over a red that is itself at 50% over black, which leaves a visible red
    ///     component of about 64. Compositing the group draws opaque green over opaque red in a surface
    ///     of its own — the red is <i>covered</i> — and then fades the result once, so the red component
    ///     is zero and the green is half. A renderer that multiplied instead would show red bleeding
    ///     through a green rectangle that is supposed to be on top of it, which reads as a blend-state
    ///     fault rather than as a compositing model.
    /// </remarks>
    [Fact]
    public void An_overlapped_child_does_not_show_through_the_one_above_it() {
        using var ui = Stacked(string.Empty);

        var bitmap = ui.Capture();
        var offset = bitmap.Offset(20, 5);

        int red = bitmap.Pixels[offset];
        int green = bitmap.Pixels[offset + 1];

        Assert.True(red <= 2, $"the covered child showed through: red was {red}");
        Assert.InRange(green, 120, 136);
    }

    /// <summary>The part of the group that nothing covers is still faded.</summary>
    /// <remarks>
    ///     The complement of the test above, and it is what stops "composite the group" from being
    ///     satisfied by simply drawing the top child: below ten pixels only the red one is there, and it
    ///     has to arrive at half strength over black.
    /// </remarks>
    [Fact]
    public void The_uncovered_part_of_a_group_is_faded_by_the_group() {
        using var ui = Stacked(string.Empty);

        var bitmap = ui.Capture();
        var offset = bitmap.Offset(20, 15);

        Assert.InRange(bitmap.Pixels[offset], 120, 136);
        Assert.True(bitmap.Pixels[offset + 1] <= 2, "green reached a row it does not cover");
    }

    /// <summary>Two nested groups fade by the product, and each isolates separately.</summary>
    /// <remarks>
    ///     ⚠ <b>The case a single accumulated multiplier gets right and a badly threaded group gets
    ///     wrong in the other direction.</b> The outer group is a surface, the inner group is a surface
    ///     inside it, and the inner one's opacity must not <i>also</i> be pushed down onto its contents
    ///     — that would fade them twice and land at an eighth rather than a quarter.
    /// </remarks>
    [Fact]
    public void Nested_groups_multiply_rather_than_fading_twice() {
        var ui = Compositing(40f, 40f);

        ui.Load(
            """
            root { width: 40px; height: 40px; background-color: #000000; }
            .outer { position: absolute; left: 0; top: 0; width: 40px; height: 40px; opacity: 0.5; }
            .inner { position: absolute; left: 0; top: 0; width: 40px; height: 40px; opacity: 0.5; }
            .lower { position: absolute; left: 0; top: 0; width: 40px; height: 20px; background-color: #ff0000; }
            .upper { position: absolute; left: 0; top: 0; width: 40px; height: 10px; background-color: #00ff00; }
            """
        );

        var outer = ui.Create("div", null, "outer", "outer");
        var inner = ui.Create("div", outer, "inner", "inner");
        ui.Create("div", inner, "lower", "lower");
        ui.Create("div", inner, "upper", "upper");
        ui.Frame();

        using (ui) {
            var bitmap = ui.Capture();
            var offset = bitmap.Offset(20, 5);

            Assert.True(bitmap.Pixels[offset] <= 2, "the covered child showed through two groups");
            Assert.InRange(bitmap.Pixels[offset + 1], 56, 72);
        }
    }

    /// <summary>A group whose subtree is one command is faded in place and costs no surface.</summary>
    /// <remarks>
    ///     ⚠ <b>The claim the collapse rests on, checked as a picture <i>and</i> as an absence.</b> The
    ///     arithmetic is identical either way — see <see cref="DrawList.Collapse" /> — so the pixel
    ///     alone cannot tell the two apart, and asserting it alone would let the optimisation silently
    ///     stop happening. What says it happened is that the frame asked for no surfaces at all.
    /// </remarks>
    [Fact]
    public void A_single_command_group_is_collapsed_rather_than_composited() {
        var ui = Compositing(40f, 40f);

        ui.Load(
            """
            root { width: 40px; height: 40px; background-color: #000000; }
            .solo { position: absolute; left: 0; top: 0; width: 40px; height: 40px;
                    background-color: #ff0000; opacity: 0.5; }
            """
        );

        ui.Create("div", null, "solo", "solo");
        ui.Frame();

        using (ui) {
            Assert.Empty(ui.Geometry.Layers);
            Assert.DoesNotContain(ui.Document.Drawing.Commands, command => command.Kind == DrawCommandKind.LayerPush);

            var bitmap = ui.Capture();
            Assert.InRange(bitmap.Pixels[bitmap.Offset(20, 20)], 120, 136);
        }
    }

    /// <summary>A group that needs a surface says so, and says how big.</summary>
    /// <remarks>
    ///     ⚠ <b>The bounds are the ink and not the element's box, which is the difference a child
    ///     hanging outside its parent makes.</b> Opacity isolates without clipping — CSS Compositing 1
    ///     § 3 — so a surface sized to the parent would cut the overflow off, and the picture would lose
    ///     exactly the part that <c>overflow: visible</c> promises to keep. Asserted on the geometry
    ///     rather than on pixels because a surface one pixel short shows only where the overflow is.
    /// </remarks>
    [Fact]
    public void A_group_is_sized_to_what_it_drew_rather_than_to_its_element() {
        var ui = Compositing(60f, 60f);

        ui.Load(
            """
            root { width: 60px; height: 60px; background-color: #000000; }
            .group { position: absolute; left: 10px; top: 10px; width: 10px; height: 10px;
                     background-color: #ff0000; opacity: 0.5; }
            .spill { position: absolute; left: 0; top: 0; width: 40px; height: 40px;
                     background-color: #00ff00; }
            """
        );

        var group = ui.Create("div", null, "group", "group");
        ui.Create("div", group, "spill", "spill");
        ui.Frame();

        using (ui) {
            var layer = Assert.Single(ui.Geometry.Layers);

            // The element is ten by ten at (10, 10); the child reaches (50, 50) in document space.
            // ⚠ And each box's quad reaches a pixel further out than its box, so the hull does too —
            // `UiGeometryBuilder.BoxMargin`, which is where an antialiased edge's ramp lands. Erring
            // outwards is the direction that costs a surface pixel rather than a missing one.
            Assert.Equal(9f, layer.Bounds.X);
            Assert.Equal(9f, layer.Bounds.Y);
            Assert.Equal(42f, layer.Bounds.Width);
            Assert.Equal(42f, layer.Bounds.Height);

            // And the overflow is actually painted, at the group's opacity.
            var bitmap = ui.Capture();
            Assert.InRange(bitmap.Pixels[bitmap.Offset(45, 45) + 1], 120, 136);
        }
    }

    /// <summary>Every group's ranges nest, and the list is in pre-order.</summary>
    /// <remarks>
    ///     ⚠ <b>The invariant both renderers execute against, asserted where a change to the builder
    ///     would break it.</b> Each consumer walks the draws once with a stack and enters a group when
    ///     the draw index matches the next layer's <see cref="UiLayer.First" /> — which is only correct
    ///     if the list is in pre-order and the ranges nest rather than overlap. A builder that appended
    ///     at close time instead would produce post-order, and every nested group would be composited
    ///     into the wrong surface.
    /// </remarks>
    [Fact]
    public void The_layer_list_is_in_pre_order_and_its_ranges_nest() {
        var ui = Compositing(60f, 60f);

        ui.Load(
            """
            root { width: 60px; height: 60px; }
            .fade { opacity: 0.5; position: absolute; left: 0; top: 0; width: 60px; height: 60px; }
            .a { position: absolute; left: 0; top: 0; width: 30px; height: 30px; background-color: #ff0000; }
            .b { position: absolute; left: 10px; top: 10px; width: 30px; height: 30px; background-color: #00ff00; }
            """
        );

        var outer = ui.Create("div", null, "outer", "fade");
        ui.Create("div", outer, "a1", "a");
        var inner = ui.Create("div", outer, "inner", "fade");
        ui.Create("div", inner, "a2", "a");
        ui.Create("div", inner, "b2", "b");
        ui.Frame();

        using (ui) {
            var layers = ui.Geometry.Layers;
            Assert.Equal(2, layers.Count);

            for (var i = 1; i < layers.Count; i++) {
                Assert.True(layers[i - 1].First <= layers[i].First, "the layers are not in pre-order");
            }

            // The second is wholly inside the first, which is what "nest rather than overlap" means.
            Assert.True(layers[1].First >= layers[0].First);
            Assert.True(layers[1].First + layers[1].Count <= layers[0].First + layers[0].Count);

            // And no two groups share a surface number.
            Assert.NotEqual(layers[0].Image, layers[1].Image);
        }
    }

    /// <summary>A click lands on the element it looks like it landed on, inside a group.</summary>
    /// <remarks>
    ///     ⚠ <b>The invariant a compositing pass is most likely to break, and the one nothing else
    ///     would notice.</b> <c>UiDocument.Accumulate</c> is where the draw list, hit testing and arrow
    ///     navigation agree about where an element is; a group that moved its contents — into a surface
    ///     with its own origin, say — would draw them in one place and let them be clicked in another.
    ///     This passes because a group's surface is the size of the whole viewport and its contents keep
    ///     their document coordinates, so there is no translation for the two to disagree about. Pinned
    ///     rather than argued, because the argument stops being true the moment somebody sizes a surface
    ///     to its group to save memory.
    /// </remarks>
    [Fact]
    public void A_composited_subtree_is_still_clicked_where_it_is_drawn() {
        var ui = Compositing(60f, 60f);

        ui.Load(
            """
            root { width: 60px; height: 60px; }
            .group { position: absolute; left: 10px; top: 10px; width: 40px; height: 40px; opacity: 0.5;
                     background-color: #202020; }
            .hit { position: absolute; left: 5px; top: 5px; width: 20px; height: 20px;
                   background-color: #ff0000; }
            """
        );

        var group = ui.Create("div", null, "group", "group");
        var hit = ui.Create("div", group, "hit", "hit");
        ui.Frame();

        using (ui) {
            // The group is composited, so this is not the collapsed path.
            Assert.Single(ui.Geometry.Layers);

            // The child sits at (15, 15) through (35, 35) in document space.
            Assert.Same(hit, ui.MovePointer(20f, 20f));
            Assert.Same(group, ui.MovePointer(45f, 45f));

            // And its own painted pixel is where the hit test says it is.
            var bitmap = ui.Capture();
            Assert.True(bitmap.Pixels[bitmap.Offset(20, 20)] > 100, "the child is not drawn where it is hit");
        }
    }

    /// <summary>A clip outside a group still clips the composite.</summary>
    /// <remarks>
    ///     ⚠ <b>The composite is a draw like any other and has to carry the scissor that was in force
    ///     where the group <i>opened</i>.</b> A group inside <c>overflow: hidden</c> that composited
    ///     unclipped would paint the whole subtree over its container's neighbours — and it would do it
    ///     only for translucent subtrees, which is a bug that looks like a z-order fault. The clip is
    ///     taken at the push rather than at the pop because the two differ whenever the group's own
    ///     element also clips.
    /// </remarks>
    [Fact]
    public void A_group_inside_a_clip_is_composited_inside_it_too() {
        var ui = Compositing(60f, 60f);

        ui.Load(
            """
            root { width: 60px; height: 60px; background-color: #000000; }
            .window { position: absolute; left: 0; top: 0; width: 20px; height: 60px; overflow: hidden; }
            .group { position: absolute; left: 0; top: 0; width: 60px; height: 60px; opacity: 0.5; }
            .a { position: absolute; left: 0; top: 0; width: 60px; height: 30px; background-color: #ff0000; }
            .b { position: absolute; left: 0; top: 0; width: 60px; height: 15px; background-color: #00ff00; }
            """
        );

        var window = ui.Create("div", null, "window", "window");
        var group = ui.Create("div", window, "group", "group");
        ui.Create("div", group, "a", "a");
        ui.Create("div", group, "b", "b");
        ui.Frame();

        using (ui) {
            var layer = Assert.Single(ui.Geometry.Layers);

            // The ink is 60 wide, but the clip only lets 20 through.
            Assert.Equal(20f, layer.Bounds.Width);

            var bitmap = ui.Capture();

            // Inside the window the group paints; outside it, nothing does.
            Assert.InRange(bitmap.Pixels[bitmap.Offset(10, 5) + 1], 120, 136);
            Assert.True(bitmap.Pixels[bitmap.Offset(40, 5) + 1] <= 2, "the composite escaped its clip");
            Assert.True(bitmap.Pixels[bitmap.Offset(40, 20)] <= 2, "the composite escaped its clip");
        }
    }

    /// <summary>A group's surface number cannot be one a host handed out for a texture.</summary>
    /// <remarks>
    ///     ⚠ <b>Cheap to assert and expensive to discover.</b> A collision would draw a thumbnail or a
    ///     viewport where a faded panel belongs, on one frame in a million, depending on how many
    ///     images the host had registered — which is the kind of fault that gets filed as a driver bug.
    ///     Hosts count up from one; groups count down from the top.
    /// </remarks>
    [Fact]
    public void Group_surface_numbers_are_reserved_at_the_top_of_the_range() {
        Assert.Equal(ulong.MaxValue, UiGeometryBuilder.LayerImage(0));
        Assert.Equal(ulong.MaxValue - 1, UiGeometryBuilder.LayerImage(1));
        Assert.True(UiGeometryBuilder.LayerImage(4096) > long.MaxValue);
    }
}
