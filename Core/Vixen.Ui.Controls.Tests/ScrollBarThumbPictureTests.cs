// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Where the thumb is drawn at the far end of the scroll, measured in pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Written to settle a question a capture raised, and it settled it the other way.</b>
///         The editor's console detail pane — a <c>ScrollView</c> under a tag of its own — showed its
///         vertical thumb at the top of the scroll and none at the end, on the Vulkan device and the
///         software rasteriser alike, and the suspicion was either the tag (#1327) or the thumb's
///         geometry. This is the same geometry on a plain <c>scroll-view</c>: the pane's 738×132 port
///         over its 1488×858 content. The thumb is at the end of the track here, so the geometry is
///         not why it vanished; the pane's bottom 28 px were behind the dock panel's edge (#1390).
///     </para>
///     <para>
///         ⚠ <b>Measured, because looking was how it was missed.</b> A reviewer read the capture and
///         did not see a thumb that was not there. The thumb's colour is sampled from the start-of-scroll
///         picture, in the middle of the first thumb-length of the track, and the end-of-scroll picture
///         is searched for it along the bar's centre line. Where it has to be is closed form:
///         <c>ScrollBar</c>'s own rule, a 24 px floor over a proportional length, over the track the
///         other bar leaves — which is measured off the other bar's box, not read back from the
///         control.
///     </para>
///     <para>
///         ⚠ <b>With both bars shown the corner is the other bar's, and before #1401 it was not.</b>
///         Both bars run to the view's edges, so the horizontal track, painted second, covered the
///         last 10 px of the vertical one: at the end of the scroll 13 of the 24 px thumb showed, at
///         y 109–121 over a horizontal track at 122–131. The both-bars fact used to assert a third of
///         a thumb and say so; the both-bars facts now ask for the whole of it, clear of the other bar.
///     </para>
/// </remarks>
public class ScrollBarThumbPictureTests {
    const string Css = """
        #view  { width: 738px; height: 132px; }
        .block { flex-shrink: 0; height: 858px; }
        .short { height: 100px; }
        .wide  { width: 1488px; }
        .narrow { width: 600px; }
        """;

    /// <summary>With only a vertical bar, the whole thumb is in the last thumb-length of the track.</summary>
    [Fact]
    public void Scrolled_to_the_end_the_whole_thumb_is_at_the_end_of_the_track() {
        var (found, length, end) = ThumbAtTheEnd("narrow", vertical: true);

        Assert.True(found.Count >= length - 2, $"{found.Count} of a {length} px thumb found at the end of the track.");
        Assert.True(found[0] >= end - length - 1, $"the thumb starts at {found[0]}, above {end - length}.");
    }

    /// <summary>With both bars, the whole vertical thumb is at the end, above the horizontal bar.</summary>
    [Fact]
    public void Scrolled_to_the_end_with_both_bars_the_whole_thumb_is_above_the_horizontal_bar() {
        var (found, length, end) = ThumbAtTheEnd("wide", vertical: true);

        Assert.True(
            found.Count >= length - 2,
            $"{found.Count} of a {length} px thumb found at the end of the track, at {found[0]}–{found[^1]} over a "
            + $"horizontal bar that starts at {end}: the other bar covers the rest (#1401)."
        );
        Assert.True(found[0] >= end - length - 1, $"the thumb starts at {found[0]}, above {end - length}.");
        Assert.True(found[^1] < end, $"the thumb runs to {found[^1]}, into the horizontal bar at {end}.");
    }

    /// <summary>And across: the whole horizontal thumb is at the right end, left of the vertical bar.</summary>
    [Fact]
    public void Scrolled_to_the_right_with_both_bars_the_whole_thumb_is_left_of_the_vertical_bar() {
        var (found, length, end) = ThumbAtTheEnd("wide", vertical: false);

        Assert.True(
            found.Count >= length - 2,
            $"{found.Count} of a {length} px thumb found at the end of the track, at {found[0]}–{found[^1]} beside a "
            + $"vertical bar that starts at {end}."
        );
        Assert.True(found[0] >= end - length - 1, $"the thumb starts at {found[0]}, left of {end - length}.");
        Assert.True(found[^1] < end, $"the thumb runs to {found[^1]}, under the vertical bar at {end}.");
    }

    /// <summary>With only a horizontal bar there is no corner, and the thumb runs to the view's edge.</summary>
    /// <remarks>The corner is kept only for a bar that is shown; one kept for nothing is track the thumb never reaches.</remarks>
    [Fact]
    public void Scrolled_to_the_right_with_only_a_horizontal_bar_the_thumb_reaches_the_edge() {
        var (found, length, end) = ThumbAtTheEnd("wide short", vertical: false);

        Assert.True(found.Count >= length - 2, $"{found.Count} of a {length} px thumb found at the end of the track.");
        Assert.True(found[^1] >= end - 2, $"the thumb stops at {found[^1]}, short of the view's edge at {end}.");
    }

    /// <summary>A press in the corner both bars leave is neither bar's, so it scrolls nothing.</summary>
    [Fact]
    public void A_press_in_the_corner_scrolls_neither_way() {
        using var ui = ControlHarness.Open(760f, 150f, Css);

        var view = ui.Add<ScrollView>("view");
        view.Content.Add("div", null, "block", "wide");

        ui.Frame();
        ui.Frame();

        Assert.True(view.MaximumTop > 0f && view.MaximumLeft > 0f, "the content does not overflow both ways");

        var bar = view.HorizontalBar;
        ui.MovePointer(bar.AbsoluteLeft + bar.Width - 3f, bar.AbsoluteTop + (bar.Height / 2f));
        ui.PressPointer();
        ui.ReleasePointer();
        ui.Frame();

        Assert.Equal(0f, view.ScrollLeft);
        Assert.Equal(0f, view.ScrollTop);

        // The instrument: the same press just left of the corner is on the track, and jumps.
        ui.MovePointer(bar.AbsoluteLeft + bar.Width - view.VerticalBar.Width - 3f, bar.AbsoluteTop + (bar.Height / 2f));
        ui.PressPointer();
        ui.ReleasePointer();
        ui.Frame();

        Assert.True(view.ScrollLeft > 0f, "a press on the end of the horizontal track did not scroll it either");
    }

    /// <summary>Scrolls to the end on one axis and finds the thumb along that bar's centre line.</summary>
    /// <returns>The positions the thumb was found at, its closed-form length, and where its track ends.</returns>
    static (List<int> Found, int Length, int End) ThumbAtTheEnd(string classes, bool vertical) {
        using var ui = ControlHarness.Open(760f, 150f, Css);

        var view = ui.Add<ScrollView>("view");
        view.Content.Add("div", null, ["block", .. classes.Split(' ')]);

        ui.Frame();
        ui.Frame();

        Assert.True(
            vertical ? view.MaximumTop > 0f : view.MaximumLeft > 0f,
            "the content does not overflow the port, so there is no thumb to find"
        );

        var bar = vertical ? view.VerticalBar : view.HorizontalBar;
        var other = vertical ? view.HorizontalBar : view.VerticalBar;
        var shown = vertical ? view.MaximumLeft > 0f : view.MaximumTop > 0f;

        var top = ui.Capture();

        view.ScrollTo(vertical ? view.MaximumTop : 0f, vertical ? 0f : view.MaximumLeft);
        ui.Frame();
        ui.Frame();

        Assert.Equal(vertical ? view.MaximumTop : view.MaximumLeft, vertical ? view.ScrollTop : view.ScrollLeft, 3);

        var bottom = ui.Capture();

        // Written only when somebody asks, as `ScrollingPanelPictureTests` does, so the numbers below
        // can be checked against the picture they were read from.
        if (Environment.GetEnvironmentVariable("VIXEN_PANEL_CAPTURE") is { Length: > 0 } directory) {
            var name = $"plain-scroll-view-{classes.Replace(' ', '-')}-{(vertical ? "vertical" : "horizontal")}";

            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, $"{name}-start.png"), top);
            PngCodec.Save(Path.Combine(directory, $"{name}-end.png"), bottom);
        }

        // Along the bar and across it, in the bar's own axis.
        var start = (int)MathF.Ceiling(vertical ? bar.AbsoluteTop : bar.AbsoluteLeft);
        var across = (int)MathF.Floor(vertical ? bar.AbsoluteLeft + (bar.Width / 2f) : bar.AbsoluteTop + (bar.Height / 2f));
        var limit = (int)MathF.Floor(vertical ? bar.AbsoluteTop + bar.Height : bar.AbsoluteLeft + bar.Width);

        // ⚠ Where the track ends is where the other bar's box begins when that bar is shown, and the
        // view's edge when it is not — measured off the boxes, so that the oracle is not the control's
        // own arithmetic read back.
        var end = shown ? (int)MathF.Floor(vertical ? other.AbsoluteTop : other.AbsoluteLeft) : limit;

        var track = end - start;
        var proportion = vertical ? view.Height / view.Content.Height : view.Width / view.Content.Width;
        var length = (int)MathF.Floor(MathF.Max(MathF.Min(track, 24f), track * proportion));

        byte[] At(Bitmap picture, int along) => vertical ? Pixel(picture, across, along) : Pixel(picture, along, across);

        var thumb = At(top, start + (length / 2));

        // The instrument: the sampled colour is the thumb's only if the thumb has left that place.
        Assert.False(
            thumb.SequenceEqual(At(bottom, start + (length / 2))),
            "the start of the track looks the same at both ends of the scroll, so the sampled colour is not the thumb's"
        );

        var found = new List<int>();

        for (var along = start; along < limit; along++) {
            if (At(bottom, along).SequenceEqual(thumb)) {
                found.Add(along);
            }
        }

        Assert.NotEmpty(found);

        return (found, length, end);
    }

    static byte[] Pixel(Bitmap picture, int x, int y) => picture.Pixels.AsSpan(((y * picture.Width) + x) * 4, 4).ToArray();
}
