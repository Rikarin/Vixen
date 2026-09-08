// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Rendering;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The strip that offers what <see cref="ImageView" />'s two toggles were asking for.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here reads the <em>draw command</em> and not the control's property back
///     — <a href="https://github.com/Rikarin/Vixen/issues/1012">#1012</a>'s own instruction.</b> A
///     binding that sets the property and a binding that reaches the picture are the same assertion
///     only until somebody stops passing <c>view:</c> at the <c>DrawImage</c> call, and the property
///     read-back would stay green through that whole regression. Reading
///     <c>DrawCommand.View</c> costs one more line and is the thing that is actually true.
/// </remarks>
public class ImageViewBarTests {
    static (ImageViewBar Bar, ImageView View) Hosted(AdvancedFixture fixture) {
        var view = fixture.Add<ImageView>();

        view.SetStyle("width", "100px");
        view.SetStyle("height", "50px");
        view.Image = 12;
        view.ImageWidth = 8;
        view.ImageHeight = 8;

        var bar = fixture.Add<ImageViewBar>();

        bar.View = view;
        fixture.Update();

        return (bar, view);
    }

    static UiImageView Drawn(AdvancedFixture fixture) =>
        fixture.Document.Drawing.Commands.Single(static command => command.Kind == DrawCommandKind.Image).View;

    static Segment Named(SegmentedControl strip, string value) =>
        strip.Segments.Single(segment => string.Equals(segment.Value, value, StringComparison.Ordinal));

    static SegmentedControl Strip(ImageViewBar bar, string className) =>
        bar.Children.OfType<SegmentedControl>().Single(strip => strip.HasClass(className));

    /// <summary>Clicking a channel segment changes the channel the image is drawn with.</summary>
    /// <remarks>
    ///     ⚠ <b>Green rather than red, because red is the first isolate and an off-by-one that always
    ///     picked the first non-default answer would pass on it.</b> <c>UiImageChannel</c>'s members
    ///     are ordered <c>All, Red, Green, Blue, Alpha</c> and <c>ImageChannels</c>' are
    ///     <c>Rgb, Red, Green, Blue, Alpha</c>, so the two happen to line up — which is exactly the
    ///     coincidence a test asserting the second member could not tell from a translation.
    /// </remarks>
    [Fact]
    public void Choosing_a_channel_reaches_the_draw_command() {
        using var fixture = new AdvancedFixture();
        var (bar, _) = Hosted(fixture);

        Assert.True(Drawn(fixture).IsIdentity);

        fixture.Click(Named(Strip(bar, "image-view-channels"), nameof(ImageChannels.Green)));
        fixture.Update();

        Assert.Equal(new UiImageView(UiImageChannel.Green, false), Drawn(fixture));
    }

    /// <summary>Choosing "Linear" makes the image draw as stored.</summary>
    /// <remarks>
    ///     The other half of the strip, and the two are independent: this leaves the channel alone,
    ///     which a translation that packed both answers into one field would not.
    /// </remarks>
    [Fact]
    public void Choosing_a_colour_space_reaches_the_draw_command() {
        using var fixture = new AdvancedFixture();
        var (bar, _) = Hosted(fixture);

        fixture.Click(Named(Strip(bar, "image-view-space"), nameof(ImageColorSpace.Linear)));
        fixture.Update();

        Assert.Equal(new UiImageView(UiImageChannel.All, true), Drawn(fixture));
    }

    /// <summary>Both strips answer at once, which is why they are two controls.</summary>
    /// <remarks>
    ///     "The alpha, as stored" is the request a texturing tool is actually asked for — is this
    ///     mask's coverage what I painted, or am I looking at it through a curve — and a single
    ///     seven-way picker cannot express it.
    /// </remarks>
    [Fact]
    public void The_two_strips_answer_independently() {
        using var fixture = new AdvancedFixture();
        var (bar, _) = Hosted(fixture);

        fixture.Click(Named(Strip(bar, "image-view-channels"), nameof(ImageChannels.Alpha)));
        fixture.Click(Named(Strip(bar, "image-view-space"), nameof(ImageColorSpace.Linear)));
        fixture.Update();

        Assert.Equal(new UiImageView(UiImageChannel.Alpha, true), Drawn(fixture));
    }

    /// <summary>A strip attached to a view that already holds an answer shows that answer.</summary>
    /// <remarks>
    ///     ⚠ <b>The "caller passes the default" failure, one level up.</b> A viewer restored from a
    ///     saved layout holds what the author last chose; a strip that pushed its own first segment
    ///     on attachment would discard it, and the picture would change the moment a panel added the
    ///     control — a regression whose cause is in the panel that adopted the strip rather than in
    ///     anything the author did.
    /// </remarks>
    [Fact]
    public void Attaching_to_a_view_adopts_what_it_already_holds() {
        using var fixture = new AdvancedFixture();
        var view = fixture.Add<ImageView>();

        view.SetStyle("width", "100px");
        view.SetStyle("height", "50px");
        view.Image = 12;
        view.ImageWidth = 8;
        view.ImageHeight = 8;
        view.Channels = ImageChannels.Blue;
        view.ColorSpace = ImageColorSpace.Linear;

        var bar = fixture.Add<ImageViewBar>();

        bar.View = view;
        fixture.Update();

        Assert.Equal(nameof(ImageChannels.Blue), Strip(bar, "image-view-channels").Value);
        Assert.Equal(nameof(ImageColorSpace.Linear), Strip(bar, "image-view-space").Value);
        Assert.Equal(new UiImageView(UiImageChannel.Blue, true), Drawn(fixture));
    }

    /// <summary>A strip driving nothing is inert rather than a crash.</summary>
    /// <remarks>
    ///     The state a strip is in for one frame between being added and being handed a view, which
    ///     is every panel that builds its controls before it loads its document.
    /// </remarks>
    [Fact]
    public void A_strip_with_no_view_takes_a_click_and_does_nothing() {
        using var fixture = new AdvancedFixture();
        var bar = fixture.Add<ImageViewBar>();

        fixture.Update();
        fixture.Click(Named(Strip(bar, "image-view-channels"), nameof(ImageChannels.Alpha)));
        fixture.Update();

        Assert.Null(bar.View);
        Assert.DoesNotContain(fixture.Document.Drawing.Commands, static c => c.Kind == DrawCommandKind.Image);
    }
}
