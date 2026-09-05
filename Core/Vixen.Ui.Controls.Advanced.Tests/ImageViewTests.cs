// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     The pane doc 48 § B6 says nothing in the editor has: pan, cursor-anchored zoom, fit, a
///     chequerboard under the alpha, and an overlay in image space.
/// </summary>
public class ImageViewTests {
    static ImageView Hosted(AdvancedFixture fixture, int width = 400, int height = 200) {
        var view = fixture.Add<ImageView>();

        view.ImageWidth = width;
        view.ImageHeight = height;

        fixture.Update();

        return view;
    }

    static IEnumerable<DrawCommand> Rectangles(AdvancedFixture fixture) =>
        fixture.Document.Drawing.Commands.Where(static command => command.Kind == DrawCommandKind.Rectangle);

    /// <summary>The chequerboard's squares, told from the element's own background by their size.</summary>
    static IEnumerable<DrawCommand> Squares(AdvancedFixture fixture, ImageView view) =>
        Rectangles(fixture).Where(command => command.Width <= view.CheckerSize + 0.01f);

    [Fact]
    public void The_control_fills_the_box_it_is_given() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture);

        // ⚠ The failure this exists to catch is not an exception. A control that draws everything and
        // lays out nothing has a content size of zero, so without `flex-grow`, `flex-basis: 0px` and
        // the two `min-*: 0px` in `AdvancedTheme.vcss` it comes out as a sliver — which reads as
        // "the panel is blank" or "the interface is a strip down the left" and never as a defect in
        // this file.
        Assert.Equal(800f, view.Width, 3);
        Assert.Equal(600f, view.Height, 3);
    }

    [Fact]
    public void A_fit_shows_the_whole_image_centred() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture);

        Assert.True(view.Fit());

        // The tighter of the two axes wins, or the fit would push the image off one end.
        Assert.Equal(2f, view.Zoom, 3);

        var bounds = view.ImageBounds;

        Assert.Equal(0f, bounds.X, 3);
        Assert.Equal(800f, bounds.Width, 3);
        Assert.Equal(400f, bounds.Height, 3);

        // Centred on the axis with room to spare: 100 above and 100 below.
        Assert.Equal(100f, bounds.Y, 3);
        Assert.Equal(600f - 100f, bounds.Y + bounds.Height, 3);
    }

    [Fact]
    public void A_fit_with_nothing_to_fit_leaves_the_view_alone() {
        using var fixture = new AdvancedFixture();
        var view = fixture.Add<ImageView>();

        view.Zoom = 3f;

        // ⚠ A collapsed dock panel, a hidden tab and the frame before the first layout are all this.
        // A zoom of zero is a coordinate space with no inverse, so every later `ToImage` would answer
        // infinity — and the pan that put it there would look like the bug.
        Assert.False(view.Fit());
        Assert.Equal(3f, view.Zoom, 3);
    }

    [Fact]
    public void The_texel_under_the_pointer_stays_under_it_when_the_wheel_turns() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture);

        view.Zoom = 1f;
        view.Pan = new Vector2(10f, 20f);
        fixture.Update();

        // ⚠ Deliberately not the centre of the pane. The centre is the fixed point of a zoom about
        // the centre as well as of a zoom about the pointer, so an assertion made there would be true
        // of the behaviour this test exists to refuse.
        var before = view.ToImage(200f, 150f);

        fixture.WheelAt(200f, 150f, -120f);

        Assert.True(view.Zoom > 1f, "the wheel did not zoom at all");

        var after = view.ToImage(200f, 150f);

        Assert.Equal(before.X, after.X, 3);
        Assert.Equal(before.Y, after.Y, 3);

        // And the corner did move, which is what makes the assertion above say something.
        Assert.NotEqual(10f, view.Pan.X, 3);
    }

    [Fact]
    public void The_zoom_is_clamped_to_the_view_s_own_limits() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture);

        view.MinimumZoom = 0.5f;
        view.MaximumZoom = 4f;

        view.Zoom = 100f;
        Assert.Equal(4f, view.Zoom, 3);

        view.Zoom = 0.001f;
        Assert.Equal(0.5f, view.Zoom, 3);
    }

    [Fact]
    public void A_drag_pans_by_the_screen_delta_divided_by_the_zoom() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture);

        view.Zoom = 2f;
        view.Pan = default;
        fixture.Update();

        fixture.DragPoint(400f, 300f, 300f, 260f);

        // ⚠ Dragging left moves the *view* right over the image, so the pan grows. Divided by the
        // zoom, because the pan is in texels and the pointer moved in screen pixels — at 2× a
        // hundred-pixel drag is fifty texels.
        Assert.Equal(50f, view.Pan.X, 3);
        Assert.Equal(20f, view.Pan.Y, 3);
    }

    [Fact]
    public void The_chequerboard_covers_the_image_and_nothing_else() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture, 100, 50);

        view.Zoom = 1f;
        view.Pan = default;
        fixture.Update();

        var image = view.ImageBounds;

        Assert.Equal(new Rectangle(0f, 0f, 100f, 50f), image);

        // The light half is one rectangle over exactly the visible part of the image — so a
        // transparent texture reads as see-through and the pane around it does not.
        Assert.Contains(Rectangles(fixture), command => Same(command, image));

        foreach (var square in Squares(fixture, view)) {
            Assert.True(
                square.X >= image.X - 0.01f
                && square.Y >= image.Y - 0.01f
                && square.X + square.Width <= image.X + image.Width + 0.01f
                && square.Y + square.Height <= image.Y + image.Height + 0.01f,
                $"a chequer square at {square.X},{square.Y} is outside the image"
            );
        }

        Assert.NotEmpty(Squares(fixture, view));
    }

    [Fact]
    public void Turning_the_chequerboard_off_draws_none_of_it() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture, 100, 50);

        view.ShowCheckerboard = false;
        fixture.Update();

        Assert.Empty(Squares(fixture, view));
    }

    [Fact]
    public void The_chequerboard_costs_what_the_pane_costs_rather_than_what_the_image_costs() {
        using var fixture = new AdvancedFixture();

        var small = Hosted(fixture, 100, 50);
        small.Zoom = 1f;
        small.Pan = default;
        fixture.Update();

        var cheap = Squares(fixture, small).Count();

        var big = Hosted(fixture, 16384, 16384);
        big.Zoom = 1f;
        big.Pan = default;
        small.ShowCheckerboard = false;
        fixture.Update();

        var dear = Squares(fixture, big).Count();

        // ⚠ A work count rather than a duration, because a wall-clock budget calibrated on an idle
        // machine is this repository's largest flake source. An image-space chequerboard over a 16k
        // texture would be a hundred thousand rectangles a frame; a screen-space one clipped to the
        // pane is a few thousand however big the image is.
        Assert.True(dear < 4000, $"the chequerboard drew {dear} squares over a 16k image");
        Assert.True(cheap < dear, "a hundred-texel image should cost less than a sixteen-thousand one");
    }

    [Fact]
    public void An_overlay_segment_is_drawn_where_the_image_puts_it() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture, 100, 50);

        view.Zoom = 4f;
        view.Pan = new Vector2(5f, 10f);
        view.Overlay.Add(new ImageOverlaySegment(new Vector2(10f, 20f), new Vector2(60f, 30f)));
        fixture.Update();

        var stroke = fixture.Document.Drawing.Commands
            .Single(static command => command.Kind == DrawCommandKind.PathStroke);

        Assert.Equal(2, stroke.Length);

        var segments = fixture.Document.Drawing.Segments;
        var from = segments[stroke.Offset].P2;
        var to = segments[stroke.Offset + 1].P2;

        // The same arithmetic the image is drawn through, which is the whole point of the overlay
        // being in image space: (10, 20) with the view panned to (5, 10) at 4× is (20, 40).
        Assert.Equal(view.ToScreen(new Vector2(10f, 20f)).X, from.X, 3);
        Assert.Equal(20f, from.X, 3);
        Assert.Equal(40f, from.Y, 3);
        Assert.Equal(220f, to.X, 3);
        Assert.Equal(80f, to.Y, 3);
    }

    [Fact]
    public void An_overlay_that_is_empty_draws_no_path_at_all() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture, 100, 50);

        fixture.Update();

        Assert.DoesNotContain(
            fixture.Document.Drawing.Commands,
            static command => command.Kind == DrawCommandKind.PathStroke
        );

        view.Overlay.Add(new ImageOverlaySegment(default, new Vector2(1f, 1f)));
        fixture.Update();

        Assert.Contains(
            fixture.Document.Drawing.Commands,
            static command => command.Kind == DrawCommandKind.PathStroke
        );
    }

    [Fact]
    public void The_channel_and_colour_space_toggles_are_a_request_and_say_so_once() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture);

        var asked = 0;
        view.ViewChanged += _ => asked++;

        view.Channels = ImageChannels.Alpha;
        Assert.Equal(1, asked);

        // Writing the value it already has is not a request; a host that re-uploaded a texture for it
        // would re-upload on every restyle.
        view.Channels = ImageChannels.Alpha;
        Assert.Equal(1, asked);

        view.ColorSpace = ImageColorSpace.Linear;
        Assert.Equal(2, asked);

        Assert.Equal(new ImageViewRequest(ImageChannels.Alpha, ImageColorSpace.Linear), view.Requested);
    }

    [Fact]
    public void A_channel_the_host_has_not_answered_changes_nothing_about_the_picture() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture, 100, 50);

        view.Image = 12;
        fixture.Update();

        var plain = fixture.Document.Drawing.Commands.Single(static command => command.Kind == DrawCommandKind.Image);

        view.Channels = ImageChannels.Alpha;
        view.ColorSpace = ImageColorSpace.Linear;
        fixture.Update();

        var isolated = fixture.Document.Drawing.Commands.Single(static command => command.Kind == DrawCommandKind.Image);

        // ⚠ **Not a gap — the contract.** The draw list's image command carries a tint and a source
        // rectangle, and neither an alpha isolate nor a transfer function is a multiply. A control
        // that tinted red for `Red` and did nothing for `Alpha` would leave a reader unable to tell
        // which of the two they were looking at. The toggles reach the picture only through
        // `ViewChanged`, and a host answers by preparing a different texture.
        Assert.Equal(plain.Image, isolated.Image);
        Assert.Equal(plain.Color, isolated.Color);
        Assert.Equal(plain.Width, isolated.Width, 3);
    }

    [Fact]
    public void An_image_the_renderer_does_not_know_draws_no_image_command() {
        using var fixture = new AdvancedFixture();
        var view = Hosted(fixture, 100, 50);

        fixture.Update();

        // Zero is "nothing registered", and it draws the chequerboard and nothing over it — which is
        // what an empty layer should look like rather than a hole.
        Assert.DoesNotContain(
            fixture.Document.Drawing.Commands,
            static command => command.Kind == DrawCommandKind.Image
        );

        Assert.NotEmpty(Squares(fixture, view));
    }

    [Fact]
    public void A_view_with_no_extent_draws_nothing_of_its_own() {
        using var fixture = new AdvancedFixture();
        var view = fixture.Add<ImageView>();

        view.Image = 12;
        view.Overlay.Add(new ImageOverlaySegment(default, new Vector2(1f, 1f)));
        fixture.Update();

        // ⚠ The extent is the denominator of every coordinate here, so a handle without one is a
        // view with no coordinate space. Drawing the handle anyway would put an image of unknown
        // size at an unknown zoom, which is a picture nobody could reason about.
        Assert.Equal(default, view.ImageBounds);

        Assert.DoesNotContain(
            fixture.Document.Drawing.Commands,
            static command => command.Kind is DrawCommandKind.Image or DrawCommandKind.PathStroke
        );
    }

    static bool Same(DrawCommand command, Rectangle rectangle) =>
        MathF.Abs(command.X - rectangle.X) < 0.01f
        && MathF.Abs(command.Y - rectangle.Y) < 0.01f
        && MathF.Abs(command.Width - rectangle.Width) < 0.01f
        && MathF.Abs(command.Height - rectangle.Height) < 0.01f;
}
