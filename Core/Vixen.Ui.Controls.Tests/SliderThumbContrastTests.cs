// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>Whether a slider's thumb can be found at all, in either palette — issue #594.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The defect this replaces was invisible to every assertion in the project, and the
///         reason is that the only measure of a thumb was how much light it emitted.</b>
///         <c>ControlHarness.InkIn</c> answers "did the thumb move" by summing brightness in a
///         rectangle, and a white thumb on a white surface adds none — so the tests that used it
///         were measuring the track's fill and passing. The light palette drew
///         <c>--thumb-color: #ffffff</c> on <c>--surface: #ffffff</c> with no stroke, which means a
///         slider at its minimum had no visible thumb whatsoever.
///     </para>
///     <para>
///         ⚠ <b>So the oracle here is contrast and not ink</b>, and it is stated the way the
///         requirement is stated: WCAG 1.4.11 asks 3:1 of the boundary of a control a user has to
///         find and operate. That is a number a frame either meets or does not, it needs no
///         committed picture to compare against, and it is false in exactly the case the bug
///         produced — a thumb the same colour as its ground scores 1.00.
///     </para>
///     <para>
///         ⚠ <b>The colours in a <c>DrawCommand</c> are already linear</b> — <c>StyleValueParser</c>
///         decodes sRGB once as the cascade parses, because everything downstream of it blends in
///         linear. WCAG's own formula begins by doing that decode, so applying it here would apply
///         it twice and inflate every ratio: <c>#7a7f88</c> would read as 12:1 rather than 4:1 and
///         a genuinely invisible ring would pass. The luminance below therefore weights the
///         channels and stops.
///     </para>
///     <para>
///         <b>Both halves are asserted, because either alone is satisfiable by a broken slider.</b>
///         The draw list says a ring of a sufficient colour was <i>asked for</i>; the picture says
///         it survived rasterisation — a stroke of zero width, or one drawn outside the box, emits
///         a perfectly contrasty command and paints nothing.
///     </para>
/// </remarks>
public class SliderThumbContrastTests {
    /// <summary>What WCAG asks of the boundary of a control somebody has to operate.</summary>
    const double Required = 3d;

    /// <summary>The size the theme gives a thumb, which is what identifies it in the draw list.</summary>
    const float ThumbSize = 14f;

    const string Css = """
        root   { flex-direction: column; align-items: flex-start;
                 background-color: var(--surface); padding: 10px; }
        slider { width: 180px; height: 24px; }
        """;

    /// <summary>A lone slider parked at its minimum, in one palette or the other.</summary>
    /// <remarks>
    ///     ⚠ <b>At the minimum deliberately.</b> Anywhere else the thumb overlaps
    ///     <c>--fill-color</c>, which is the accent blue, and the overlap alone would carry the
    ///     contrast — the frame would pass while the case the bug is actually about, a slider nobody
    ///     has moved yet, still drew nothing findable.
    /// </remarks>
    static (UiTest Ui, Slider Slider) Parked(bool dark) {
        var ui = ControlHarness.Open(220f, 44f, Css);

        if (dark) {
            ui.Document.Root.AddClass("dark");
        }

        var slider = ui.Add<Slider>("amount");
        slider.Value = slider.Minimum;

        ui.Frame();
        return (ui, slider);
    }

    /// <summary>Relative luminance of a colour the cascade has already decoded.</summary>
    static double Luminance(Color4 color) => (0.2126d * color.R) + (0.7152d * color.G) + (0.0722d * color.B);

    /// <summary>The WCAG ratio between two colours, whichever way round they are.</summary>
    static double Contrast(Color4 left, Color4 right) {
        var a = Luminance(left);
        var b = Luminance(right);

        return (Math.Max(a, b) + 0.05d) / (Math.Min(a, b) + 0.05d);
    }

    /// <summary>Every command that draws the thumb, which is the only box of its size in the frame.</summary>
    /// <remarks>
    ///     Selected by size rather than by order, so that adding a command before or after the thumb
    ///     does not silently move which one this measures. The fixture holds one slider and nothing
    ///     else square, so a 14×14 box is the thumb and nothing else is.
    /// </remarks>
    static List<DrawCommand> ThumbCommands(UiTest ui) =>
        [
            .. ui.Document.Drawing.Commands.Where(static command =>
                MathF.Abs(command.Width - ThumbSize) < 0.01f && MathF.Abs(command.Height - ThumbSize) < 0.01f
            )
        ];

    /// <summary>The best any part of the thumb manages against a colour behind it.</summary>
    static double Findability(UiTest ui, Color4 ground) =>
        ThumbCommands(ui).Max(command => Contrast(command.Color, ground));

    /// <summary>The one that was red before the ring landed.</summary>
    /// <remarks>
    ///     Two grounds, because at the minimum the thumb straddles both of them: its left half is
    ///     over bare <c>--surface</c> — the rail is inset by half a thumb — and its right half is
    ///     over the unfilled <c>--track-color</c>. Passing against one and failing against the other
    ///     is a thumb with an invisible side.
    /// </remarks>
    [Fact]
    public void A_thumb_at_the_minimum_stands_out_from_both_the_surface_and_the_track() {
        var (ui, slider) = Parked(dark: false);

        using (ui) {
            var surface = ui.ColorOf(ui.Document.Root, "--surface") ?? throw new InvalidOperationException("no surface");
            var track = ui.ColorOf(slider, "--track-color") ?? throw new InvalidOperationException("no track");

            Assert.True(
                Findability(ui, surface) >= Required,
                $"the thumb is {Findability(ui, surface):0.00}:1 against the surface it sits on"
            );

            Assert.True(
                Findability(ui, track) >= Required,
                $"the thumb is {Findability(ui, track):0.00}:1 against the track it sits on"
            );
        }
    }

    /// <summary>The dark palette, which was never the broken one and must not become it.</summary>
    /// <remarks>
    ///     ⚠ <b>Green before the fix as well, and that is the point of writing it down.</b>
    ///     <c>#e8eaed</c> on <c>#1b1d21</c> is already 14:1, so this half of the assertion never
    ///     failed and is here as the guard on a change that touches both palettes at once — a ring
    ///     colour chosen for the light theme and pasted into the dark one could easily have hidden
    ///     the thumb in the theme that was working.
    /// </remarks>
    [Fact]
    public void The_dark_palette_keeps_its_thumb_findable_too() {
        var (ui, slider) = Parked(dark: true);

        using (ui) {
            var surface = ui.ColorOf(ui.Document.Root, "--surface") ?? throw new InvalidOperationException("no surface");
            var track = ui.ColorOf(slider, "--track-color") ?? throw new InvalidOperationException("no track");

            Assert.True(Findability(ui, surface) >= Required, $"{Findability(ui, surface):0.00}:1 against the surface");
            Assert.True(Findability(ui, track) >= Required, $"{Findability(ui, track):0.00}:1 against the track");
        }
    }

    /// <summary>A fill and a ring, drawn in that order so the ring outlines the disc.</summary>
    [Fact]
    public void The_thumb_is_a_filled_disc_and_a_ring_around_it() {
        var (ui, _) = Parked(dark: false);

        using (ui) {
            var commands = ThumbCommands(ui);

            Assert.Equal(1, commands.Count(static command => command.Kind == DrawCommandKind.Rectangle));

            var ring = Assert.Single(commands, static command => command.Kind == DrawCommandKind.Border);

            Assert.True(ring.Thickness > 0f, "a ring of no width is a command that paints nothing");
            Assert.Equal(ThumbSize * 0.5f, ring.Radius, 3);
            Assert.True(commands.IndexOf(ring) > commands.FindIndex(static c => c.Kind == DrawCommandKind.Rectangle));
        }
    }

    /// <summary>And the ring reaches the pixels, which the draw list cannot promise.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second window is the control, and without it this test cannot fail.</b> "Some
    ///         pixel in this rectangle beats the surface" is satisfied by any dark thing that wanders
    ///         into it. So the identical measurement is taken over a thumb-sized square of bare track
    ///         at the far end of the rail, where the answer must be <i>no</i>: <c>--track-color</c>
    ///         is 1.3:1 against the surface, and a fixture that called that findable would be
    ///         measuring the frame rather than the thumb.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A peak and a count, because a one-pixel ring is mostly antialiasing.</b> Of the
    ///         ~44 pixels the ring touches, four reach its colour and the rest are partial coverage
    ///         blended back towards white — the strongest of them measures 3.8:1 and a half-covered
    ///         one measures 2.9:1. Requiring most of the ring to clear 3:1 would be requiring the
    ///         rasteriser not to antialias; requiring one pixel to would accept a dot. So the peak
    ///         carries the ratio and the count carries the shape.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_ring_survives_rasterisation_and_the_bare_track_does_not() {
        var (ui, slider) = Parked(dark: false);

        using (ui) {
            var surface = ui.ColorOf(ui.Document.Root, "--surface") ?? throw new InvalidOperationException("no surface");
            var bounds = slider.Bounds;
            var top = (int)MathF.Round(bounds.Y + ((bounds.Height - ThumbSize) * 0.5f));
            var far = (int)MathF.Round(bounds.X + bounds.Width - ThumbSize);

            var (thumbPeak, thumbInked) = Window(ui, (int)MathF.Round(bounds.X), top, surface);
            var (trackPeak, _) = Window(ui, far, top, surface);

            Assert.True(thumbPeak >= Required, $"the drawn thumb peaks at {thumbPeak:0.00}:1 against the surface");
            Assert.True(thumbInked > 20, $"only {thumbInked} pixels of the thumb are marked at all, which is a dot");
            Assert.True(trackPeak < Required, $"bare track peaks at {trackPeak:0.00}:1, so this is not measuring a thumb");
        }
    }

    /// <summary>The best contrast in a thumb-sized square of the picture, and how much of it is marked.</summary>
    /// <remarks>
    ///     The rasteriser writes the linear values straight through with no encode, so a byte over
    ///     255 is the same quantity <see cref="Luminance" /> already weights and no decode belongs
    ///     here either.
    /// </remarks>
    static (double Peak, int Inked) Window(UiTest ui, int left, int top, Color4 ground) {
        var image = ui.Capture();
        var peak = 1d;
        var inked = 0;

        for (var y = Math.Max(0, top); y < Math.Min(image.Height, top + (int)ThumbSize); y++) {
            for (var x = Math.Max(0, left); x < Math.Min(image.Width, left + (int)ThumbSize); x++) {
                var offset = image.Offset(x, y);

                var pixel = new Color4(
                    image.Pixels[offset] / 255f,
                    image.Pixels[offset + 1] / 255f,
                    image.Pixels[offset + 2] / 255f,
                    1f
                );

                var contrast = Contrast(pixel, ground);
                peak = Math.Max(peak, contrast);

                if (contrast >= 1.5d) {
                    inked++;
                }
            }
        }

        return (peak, inked);
    }
}
