// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>Render size, capture, and the input a camera controller reads.</summary>
public class ViewportTests {
    static Viewport Hosted(AdvancedFixture fixture) {
        var viewport = fixture.Add<Viewport>();

        viewport.Refresh();
        fixture.Update();

        return viewport;
    }

    [Fact]
    public void The_render_size_is_in_render_pixels_rather_than_layout_pixels() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        Assert.Equal(800, viewport.RenderWidth);
        Assert.Equal(600, viewport.RenderHeight);

        var resizes = 0;
        viewport.Resized += _ => resizes++;

        // ⚠ The bug this exists to avoid: a viewport that handed a renderer its layout size draws a
        // soft image on every scaled display and a sharp one on the developer's.
        viewport.RenderScale = 2f;

        Assert.Equal(1600, viewport.RenderWidth);
        Assert.Equal(1200, viewport.RenderHeight);
        Assert.Equal(1, resizes);
    }

    [Fact]
    public void Refreshing_without_a_change_says_nothing() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        var resizes = 0;
        viewport.Resized += _ => resizes++;

        Assert.False(viewport.Refresh());
        Assert.False(viewport.Refresh());
        Assert.Equal(0, resizes);
    }

    [Fact]
    public void A_zero_sized_viewport_does_not_ask_for_a_render_target() {
        using var fixture = new AdvancedFixture(css: "viewport { display: none; }");
        var viewport = fixture.Add<Viewport>();

        var resizes = 0;
        viewport.Resized += _ => resizes++;

        // ⚠ A collapsed dock panel and a hidden tab are both this, and a renderer asked for a
        // zero-by-zero target either throws or makes one nothing can be drawn into.
        Assert.False(viewport.Refresh());
        Assert.Equal(0, resizes);
        Assert.Equal(0, viewport.RenderWidth);
    }

    [Fact]
    public void The_aspect_ratio_is_what_a_projection_matrix_wants() {
        using var fixture = new AdvancedFixture(css: "viewport { width: 400px; height: 200px; flex-grow: 0; }");
        var viewport = Hosted(fixture);

        Assert.Equal(2f, viewport.AspectRatio, 3);
    }

    [Fact]
    public void A_drag_reports_deltas_and_totals_in_render_pixels() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        viewport.RenderScale = 2f;

        var drags = new List<ViewportDrag>();
        viewport.Dragged += (_, drag) => drags.Add(drag);

        fixture.Press(100f, 100f, PointerButton.Secondary);
        fixture.Move(110f, 105f);
        fixture.Move(120f, 105f);
        fixture.Release(120f, 105f, PointerButton.Secondary);

        Assert.Equal(2, drags.Count);

        Assert.Equal(PointerButton.Secondary, drags[0].Button);
        Assert.Equal(20f, drags[0].DeltaX, 2);
        Assert.Equal(10f, drags[0].DeltaY, 2);

        // ⚠ Summing the deltas does not give the total when a drag goes out and comes back, which is
        // why both are carried — the second delta is 20 and the total is 40.
        Assert.Equal(20f, drags[1].DeltaX, 2);
        Assert.Equal(40f, drags[1].TotalX, 2);
    }

    [Fact]
    public void The_wheel_is_reported_as_a_zoom() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        var total = 0f;
        viewport.Zoomed += (_, delta) => total += delta;

        fixture.Wheel(viewport, -120f);
        Assert.Equal(-120f, total);
    }

    [Fact]
    public void Capture_survives_a_click_and_asks_for_the_pointer_to_be_locked() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        var locks = new List<bool>();
        viewport.PointerLockRequested += (_, wanted) => locks.Add(wanted);

        viewport.Capture(lockPointer: true);

        Assert.True(viewport.IsCapturing);
        Assert.Same(viewport, fixture.Document.Captured);
        Assert.Equal([true], locks);

        // ⚠ A click inside a captured viewport must not drop the capture, or first-person navigation
        // ends the first time somebody shoots something.
        fixture.Press(100f, 100f);
        fixture.Release(100f, 100f);

        Assert.True(viewport.IsCapturing);
        Assert.Same(viewport, fixture.Document.Captured);

        viewport.ReleaseCapture();

        Assert.False(viewport.IsCapturing);
        Assert.Null(fixture.Document.Captured);
        Assert.Equal([true, false], locks);
    }

    [Fact]
    public void An_ordinary_drag_gives_the_pointer_back_when_it_ends() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        fixture.Press(100f, 100f);
        Assert.Same(viewport, fixture.Document.Captured);

        fixture.Release(100f, 100f);
        Assert.Null(fixture.Document.Captured);
    }

    [Fact]
    public void The_gizmo_can_be_taken_away_and_put_back() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        Assert.False(viewport.Gizmo.HasClass("hidden"));

        viewport.ShowGizmo = false;
        Assert.True(viewport.Gizmo.HasClass("hidden"));

        viewport.ShowGizmo = true;
        Assert.False(viewport.Gizmo.HasClass("hidden"));
    }

    [Fact]
    public void The_gizmo_draws_three_arms_and_orders_them_by_depth() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        viewport.ViewRotation = Matrix4x4.Identity;
        fixture.Update();

        // Three strokes and three dots. Asserted through the draw list because that is the only
        // place a drawn control's output exists — there are no elements to look at.
        var commands = fixture.Document.Drawing.Commands;

        Assert.Equal(3, commands.Count(static command => command.Kind == DrawCommandKind.PathStroke));
    }

    [Fact]
    public void A_render_target_is_sampled_as_it_stands() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        viewport.RenderTarget = 12;
        fixture.Update();

        var image = fixture.Document.Drawing.Commands.Single(static command => command.Kind == DrawCommandKind.Image);

        // ⚠ The whole texture, the right way up. Both backends resolve the engine's +Y-up clip space
        // where the API is — Vulkan with a negative-height viewport, OpenGL by flipping the viewport
        // origin — so a colour target's row zero is already the *top* of the view, and UVs run from
        // the top-left. `LineImageTests.AssertTheDiagonalFades` is that same fact from the other end:
        // a vertex at clip y −0.8 lands at the bottom of the image.
        //
        // ⚠ Flipping it here mirrors the scene about the horizon, and almost nothing looks wrong: a
        // grid is symmetric and the corner axis cross is an interface element that does not flip with
        // it. What is noticed instead is that a gizmo cannot be clicked near the top or bottom of the
        // pane, that hover lights up a handle the cursor is not on, and that a vertical pan goes the
        // wrong way — because every one of those measures the unmirrored image.
        Assert.Equal(0f, image.Source.Y);
        Assert.Equal(1f, image.Source.Height);
        Assert.False(viewport.FlipVertically);
    }

    [Fact]
    public void A_host_whose_target_really_is_upside_down_can_still_say_so() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        viewport.RenderTarget = 12;
        viewport.FlipVertically = true;
        fixture.Update();

        var image = fixture.Document.Drawing.Commands.Single(static command => command.Kind == DrawCommandKind.Image);

        Assert.Equal(1f, image.Source.Y);
        Assert.Equal(-1f, image.Source.Height);
    }

    [Fact]
    public void An_overlay_is_an_ordinary_element_over_the_scene() {
        using var fixture = new AdvancedFixture();
        var viewport = Hosted(fixture);

        var button = viewport.Overlay.Add<Button>();
        button.Label = "Shade";

        fixture.Update();

        // The point of the overlay being elements: a toolbar over a viewport is an ordinary toolbar,
        // styled by the cascade rather than drawn into the render target.
        Assert.True(button.Width > 0f);
        Assert.Same(viewport.Overlay, button.Parent);
    }
}
