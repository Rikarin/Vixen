// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Whether a scroll bar's thumb can be told from the surface it sits on, in either palette — issue #1414.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>#594 again, on the scroll bar.</b> The light palette's <c>--thumb-color</c> and
///         <c>--surface</c> are both <c>#ffffff</c>; the slider was given a ring off
///         <c>--thumb-border-color</c> for it and the scroll bar never read that token. A list
///         scrolled to the top drew a white thumb over most of its bar, indistinguishable from the
///         popup's white surface, and the only grey was the stub of uncovered track at the bottom —
///         which reads as a small thumb parked at the wrong end.
///     </para>
///     <para>
///         ⚠ <b>The oracle is <see cref="SliderThumbContrastTests" />' and so is its arithmetic</b>:
///         WCAG 1.4.11's 3:1 for the boundary of a control somebody has to find, over colours that
///         are already linear in the picture, so the luminance weights the channels and stops. The
///         geometry is the reported one — a 277 px port over 321 px of content, the thumb covering
///         the top 86% of the bar — on a white surface.
///     </para>
///     <para>
///         ⚠ <b>Where the thumb ends is the question, so that is where it is measured.</b> The
///         centre column of the bar, from the thumb's middle down to the end of the track, has to
///         pass through a pixel at 3:1 against the surface; the same walk down a column of bare
///         surface beside the bar must not, or this would be measuring the frame. The white thumb and
///         the <c>#e3e6eb</c> track are 1.00:1 and 1.26:1, so without a ring the walk finds nothing.
///     </para>
/// </remarks>
public class ScrollBarThumbContrastTests {
    /// <summary>What WCAG asks of the boundary of a control somebody has to operate.</summary>
    const double Required = 3d;

    const string Css = """
        root   { background-color: var(--surface); }
        #view  { width: 300px; height: 277px; background-color: var(--surface); }
        .block { flex-shrink: 0; height: 321px; width: 200px; }
        """;

    /// <summary>The light palette, which was the broken one.</summary>
    [Fact]
    public void In_the_light_palette_the_end_of_the_thumb_stands_out_from_the_surface() {
        var (peak, beside) = EndOfTheThumb(dark: false, "light");

        Assert.True(beside < Required, $"bare surface beside the bar peaks at {beside:0.00}:1, so this is not measuring a thumb");
        Assert.True(
            peak >= Required,
            $"from the middle of the thumb to the end of the track the bar peaks at {peak:0.00}:1 against the surface: "
            + "nothing marks where the thumb ends (#1414)."
        );
    }

    /// <summary>The dark palette, which was never broken, and must not become it.</summary>
    [Fact]
    public void In_the_dark_palette_the_thumb_still_stands_out_from_the_surface() {
        var (peak, beside) = EndOfTheThumb(dark: true, "dark");

        Assert.True(beside < Required, $"bare surface beside the bar peaks at {beside:0.00}:1, so this is not measuring a thumb");
        Assert.True(peak >= Required, $"the bar peaks at {peak:0.00}:1 against the surface.");
    }

    /// <summary>A fill and then a ring, both the thumb's size, and nothing when the token is transparent.</summary>
    [Fact]
    public void The_thumb_is_a_fill_and_a_ring_and_a_transparent_token_draws_no_ring() {
        var (ui, view) = Open(dark: false);
        using var _ = ui;
        var bar = view.VerticalBar;

        var commands = ThumbCommands(ui, bar);
        var fill = commands.FindIndex(static command => command.Kind == DrawCommandKind.Rectangle);
        var ring = commands.FindIndex(static command => command.Kind == DrawCommandKind.Border);

        Assert.True(fill >= 0, "no thumb fill in the draw list");
        Assert.True(ring > fill, "the ring is missing or drawn under the fill");
        Assert.True(commands[ring].Thickness > 0f, "a ring of no width is a command that paints nothing");

        ui.Load("root { --thumb-border-color: transparent; }");
        ui.Frame();

        Assert.DoesNotContain(ThumbCommands(ui, bar), static command => command.Kind == DrawCommandKind.Border);
    }

    static (UiTest Ui, ScrollView View) Open(bool dark) {
        var ui = ControlHarness.Open(320f, 300f, Css);

        if (dark) {
            ui.Document.Root.AddClass("dark");
        }

        var view = ui.Add<ScrollView>("view");
        view.Content.Add("div", null, "block");

        ui.Frame();
        ui.Frame();

        return (ui, view);
    }

    /// <summary>The draw commands whose box is the thumb's: the bar's width, and longer than the track is left.</summary>
    static List<DrawCommand> ThumbCommands(UiTest ui, ScrollBar bar) =>
        [
            .. ui.Document.Drawing.Commands.Where(command =>
                MathF.Abs(command.X - bar.AbsoluteLeft) < 0.01f
                && MathF.Abs(command.Width - bar.Width) < 0.01f
                && command.Height < bar.Height - 1f
                && command.Height > bar.Height / 2f
            )
        ];

    /// <summary>The best contrast against the surface down the bar's centre, from the thumb's middle to the track's end, and beside the bar.</summary>
    static (double Peak, double Beside) EndOfTheThumb(bool dark, string name) {
        var (ui, view) = Open(dark);
        using var _ = ui;

        var bar = view.VerticalBar;

        Assert.True(view.MaximumTop > 0f, "the content does not overflow, so there is no thumb");
        Assert.Equal(0f, view.ScrollTop);

        var surface = ui.ColorOf(ui.Document.Root, "--surface") ?? throw new InvalidOperationException("no surface");
        var picture = ui.Capture();

        if (Environment.GetEnvironmentVariable("VIXEN_PANEL_CAPTURE") is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
            PngCodec.Save(Path.Combine(directory, $"scroll-thumb-contrast-{name}.png"), picture);
        }

        // The thumb is 277/321 of the bar at the top of the scroll, so its middle is well inside it
        // and the walk down from there crosses its end and the uncovered track below.
        var from = (int)MathF.Ceiling(bar.AbsoluteTop + (bar.Height * 0.25f));
        var to = (int)MathF.Floor(bar.AbsoluteTop + bar.Height);
        var centre = (int)MathF.Floor(bar.AbsoluteLeft + (bar.Width / 2f));
        var outside = (int)MathF.Floor(bar.AbsoluteLeft) - 20;

        return (Peak(picture, centre, from, to, surface), Peak(picture, outside, from, to, surface));
    }

    static double Peak(Bitmap picture, int x, int from, int to, Color4 ground) {
        var peak = 1d;

        for (var y = from; y < to; y++) {
            var offset = picture.Offset(x, y);
            var pixel = new Color4(
                picture.Pixels[offset] / 255f,
                picture.Pixels[offset + 1] / 255f,
                picture.Pixels[offset + 2] / 255f,
                1f
            );

            peak = Math.Max(peak, Contrast(pixel, ground));
        }

        return peak;
    }

    /// <summary>Relative luminance of a colour that is already linear.</summary>
    static double Luminance(Color4 color) => (0.2126d * color.R) + (0.7152d * color.G) + (0.0722d * color.B);

    static double Contrast(Color4 left, Color4 right) {
        var a = Luminance(left);
        var b = Luminance(right);

        return (Math.Max(a, b) + 0.05d) / (Math.Min(a, b) + 0.05d);
    }
}
