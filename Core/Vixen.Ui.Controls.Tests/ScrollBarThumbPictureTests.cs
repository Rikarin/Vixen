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
///         did not see a thumb that was not there. The thumb's colour is sampled from the top-of-scroll
///         picture, in the middle of the first thumb-length of the track, and the end-of-scroll picture
///         is searched for it down the bar's centre column. Where it has to be is closed form:
///         <c>ScrollBar</c>'s own rule, a 24 px floor over a proportional length.
///     </para>
/// </remarks>
public class ScrollBarThumbPictureTests {
    const string Css = """
        #view  { width: 738px; height: 132px; }
        .block { flex-shrink: 0; height: 858px; }
        .wide  { width: 1488px; }
        .narrow { width: 600px; }
        """;

    /// <summary>With only a vertical bar, the whole thumb is in the last thumb-length of the track.</summary>
    [Fact]
    public void Scrolled_to_the_end_the_whole_thumb_is_at_the_end_of_the_track() {
        var (found, length, end) = ThumbAtTheEnd("narrow");

        Assert.True(found.Count >= length - 2, $"{found.Count} of a {length} px thumb found at the end of the track.");
        Assert.True(found[0] >= end - length - 1, $"the thumb starts at {found[0]}, above {end - length}.");
    }

    /// <summary>With both bars, the thumb is still at the end, under the horizontal bar's overlay.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the whole thumb, and that is recorded rather than blessed.</b> Both bars run to the
    ///     view's edges (<c>scrollbar.vertical { bottom: 0 }</c>), so the horizontal track, drawn
    ///     second, covers the last 10 px of the vertical one: at the end of the scroll 14 of the
    ///     24 px thumb show. What this pins is that some of it is there and none of it is anywhere
    ///     else, which is the question the console capture asked.
    /// </remarks>
    [Fact]
    public void Scrolled_to_the_end_with_both_bars_the_thumb_is_still_at_the_end_of_the_track() {
        var (found, length, end) = ThumbAtTheEnd("wide");

        Assert.True(found.Count >= length / 3, $"{found.Count} of a {length} px thumb found at the end of the track.");
        Assert.True(found[0] >= end - length - 1, $"the thumb starts at {found[0]}, above {end - length}.");
    }

    static (List<int> Found, int Length, int End) ThumbAtTheEnd(string width) {
        using var ui = ControlHarness.Open(760f, 150f, Css);

        var view = ui.Add<ScrollView>("view");
        view.Content.Add("div", null, "block", width);

        ui.Frame();
        ui.Frame();

        Assert.True(view.MaximumTop > 0f, "the content does not overflow the port, so there is no thumb to find");

        var bar = view.VerticalBar;
        var top = ui.Capture();

        view.ScrollTo(view.MaximumTop, 0f);
        ui.Frame();
        ui.Frame();

        Assert.Equal(view.MaximumTop, view.ScrollTop, 3);

        var bottom = ui.Capture();

        // Written only when somebody asks, as `ScrollingPanelPictureTests` does, so the numbers below
        // can be checked against the picture they were read from.
        if (Environment.GetEnvironmentVariable("VIXEN_PANEL_CAPTURE") is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, $"plain-scroll-view-{width}-top.png"), top);
            PngCodec.Save(Path.Combine(directory, $"plain-scroll-view-{width}-bottom.png"), bottom);
        }

        var length = (int)MathF.Floor(MathF.Max(MathF.Min(bar.Height, 24f), bar.Height * bar.ViewportSize / bar.ContentSize));
        var x = (int)MathF.Floor(bar.AbsoluteLeft + bar.Width / 2f);
        var start = (int)MathF.Ceiling(bar.AbsoluteTop);
        var end = (int)MathF.Floor(bar.AbsoluteTop + bar.Height);

        var thumb = Pixel(top, x, start + length / 2);

        // The instrument: the sampled colour is the thumb's only if the thumb has left that place.
        Assert.False(
            thumb.SequenceEqual(Pixel(bottom, x, start + length / 2)),
            "the top of the track looks the same at both ends of the scroll, so the sampled colour is not the thumb's"
        );

        var found = new List<int>();

        for (var y = start; y < end; y++) {
            if (Pixel(bottom, x, y).SequenceEqual(thumb)) {
                found.Add(y);
            }
        }

        Assert.NotEmpty(found);

        return (found, length, end);
    }

    static byte[] Pixel(Bitmap picture, int x, int y) => picture.Pixels.AsSpan(((y * picture.Width) + x) * 4, 4).ToArray();
}
