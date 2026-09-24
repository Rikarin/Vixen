// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A slider's tick marks, and the snapping that may be asked of them — doc 49 § 7.1's named gap.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The marks are asserted in the picture, against a closed form.</b> The rail is inset by
///         half a thumb, so over a 214-pixel slider with the theme's 14-pixel thumb it runs from column
///         7 for 200 pixels, and tick <c>i</c> of <c>n</c> is the column
///         <c>floor(7 + 200 · i / (n − 1))</c>. A scan of one pixel row just above the rail — inside a
///         tick's reach and outside the rail, the fill and, away from the value, the thumb — must find
///         exactly those columns dark and nothing else. A tick drawn off by a pixel, a tick drawn
///         twice or a tick not drawn all fail it, and a slider with no ticks is the empty row that
///         shows the scan can come back empty.
///     </para>
/// </remarks>
public class SliderTickTests {
    const int Width = 214;
    const int Height = 20;
    const float RailStart = 7f;
    const float RailLength = 200f;

    /// <summary>A row inside a tick's reach and above the rail: the rail spans ~7.9 to ~12.1.</summary>
    const int Row = 6;

    static readonly string Css = $$"""
        root   { background-color: var(--surface); }
        slider { position: absolute; left: 0px; top: 0px; width: {{Width}}px; height: {{Height}}px; }
        """;

    [Fact]
    public void Five_ticks_mark_the_quarters_of_the_rail_and_nothing_else() {
        using var ui = ControlHarness.Open(Width, Height, Css);
        var slider = ui.Add<Slider>("amount");

        ui.Frame();
        Assert.Empty(DarkColumns(ui));

        slider.TickCount = 5;
        ui.Frame();

        // At the minimum the thumb covers the first tick, which is where the value is.
        Assert.Equal(Expected(5).Skip(1), DarkColumns(ui));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(11)]
    public void Any_count_puts_its_ticks_at_the_even_divisions(int count) {
        using var ui = ControlHarness.Open(Width, Height, Css);
        var slider = ui.Add<Slider>("amount");

        slider.TickCount = count;
        slider.Value = slider.Maximum;
        ui.Frame();

        // At the maximum the thumb covers the last one instead.
        Assert.Equal(Expected(count).SkipLast(1), DarkColumns(ui));
    }

    [Fact]
    public void One_tick_is_drawn_at_the_middle() {
        using var ui = ControlHarness.Open(Width, Height, Css);
        var slider = ui.Add<Slider>("amount");

        slider.TickCount = 1;
        ui.Frame();

        Assert.Equal([(int) MathF.Floor(RailStart + (RailLength * 0.5f))], DarkColumns(ui));
    }

    /// <summary>A range slider draws the same marks, under both of its thumbs.</summary>
    [Fact]
    public void A_range_slider_draws_the_same_marks() {
        using var ui = ControlHarness.Open(Width, Height, Css.Replace("slider {", "range-slider {", StringComparison.Ordinal));
        var slider = ui.Add<RangeSlider>("span");

        slider.TickCount = 5;
        slider.Low = 0.25f;
        slider.High = 0.75f;
        ui.Frame();

        // The thumbs cover the second and fourth; the ends and the middle stay visible.
        var expected = Expected(5);
        Assert.Equal([expected[0], expected[2], expected[4]], DarkColumns(ui));
    }

    /// <summary>Without snapping, ticks are a ruler: the value goes wherever it is put.</summary>
    [Fact]
    public void Ticks_alone_do_not_constrain_the_value() {
        using var ui = ControlHarness.Open(Width, Height, Css);
        var slider = ui.Add<Slider>("amount");

        slider.TickCount = 5;
        slider.Value = 0.3f;

        Assert.Equal(0.3f, slider.Value);
    }

    /// <summary>
    ///     ⚠ <b>Snapping replaces the step for every route in</b>: an assignment, a press on the
    ///     rail, and an arrow key — which is the one that would otherwise be stuck, since a hundredth
    ///     of the range rounds back to the tick it started on.
    /// </summary>
    [Fact]
    public void Snapping_puts_every_value_on_a_tick_and_an_arrow_moves_one_tick() {
        using var ui = ControlHarness.Open(Width, Height, Css);
        var slider = ui.Add<Slider>("amount");

        slider.TickCount = 5;
        slider.Value = 0.3f;
        slider.SnapsToTicks = true;

        // Turning it on brings a value already there onto the nearest tick.
        Assert.Equal(0.25f, slider.Value);

        slider.Value = 0.6f;
        Assert.Equal(0.5f, slider.Value);

        ui.Document.Focus(slider);
        ui.PressKey(InputKey.Right);
        Assert.Equal(0.75f, slider.Value);

        ui.PressKey(InputKey.Left);
        ui.PressKey(InputKey.Left);
        Assert.Equal(0.25f, slider.Value);

        // A press a little past the three-quarter mark lands on it.
        var x = RailStart + (RailLength * 0.8f);
        ui.Drag(x, Height * 0.5f, x, Height * 0.5f, steps: 1);
        Assert.Equal(0.75f, slider.Value);
    }

    /// <summary>The columns tick <c>i</c> of <paramref name="count" /> must occupy.</summary>
    static int[] Expected(int count) =>
        [.. Enumerable.Range(0, count).Select(index => (int) MathF.Floor(RailStart + (RailLength * index / (count - 1))))];

    /// <summary>The columns of <see cref="Row" /> drawn much darker than the surface.</summary>
    /// <remarks>
    ///     The light palette's surface is white and its <c>--tick-color</c> <c>#6b7280</c>; the thumb's
    ///     ring (<c>#7a7f88</c>) is the only other dark thing near this row, which is why every
    ///     expectation above leaves out the tick the thumb sits on — the ring's columns are dark too.
    ///     The thumb spans fourteen columns centred on the value, so those are dropped here rather than
    ///     classified.
    /// </remarks>
    static List<int> DarkColumns(UiTest ui) {
        var image = ui.Capture();
        var columns = new List<int>();
        var slider = ui.Document.Root.Children[0];
        var value = slider switch {
            Slider single => new[] { single.Value },
            RangeSlider span => new[] { span.Low, span.High },
            _ => []
        };

        for (var x = 0; x < image.Width; x++) {
            if (value.Any(fraction => MathF.Abs(x - (RailStart + (RailLength * fraction))) <= 8f)) {
                continue;
            }

            var at = image.Offset(x, Row);

            if (image.Pixels[at] < 200 && image.Pixels[at + 1] < 200 && image.Pixels[at + 2] < 200) {
                columns.Add(x);
            }
        }

        return columns;
    }
}
