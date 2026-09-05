// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Ui.Testing;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The same controls in both themes, and the proof that the theme reaches the pixels.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>What doc 09 § Testing is owed here is the <i>theme</i> dimension and not the
///         pictures.</b> <c>ControlVisualTests</c> already commits a golden per control — thirty-nine
///         of them — so "nothing per control" is no longer true. Every one of those is drawn in the
///         light palette, and none of them would move if the dark palette were deleted.
///     </para>
///     <para>
///         ⚠ <b>Doc 43 recorded exactly what that costs.</b> All 43 committed baselines stayed
///         byte-identical when the oklch palette landed, and it was read as reassurance. It was not:
///         the suites that take pictures had no theme file and never exercised the tokens, so
///         byte-identical was what they would have printed if the palette had been replaced with
///         noise. A picture that cannot move when the palette moves is not coverage of the palette.
///     </para>
///     <para>
///         ⚠ <b>So the first test here is not a golden.</b> Before any reference is compared, the two
///         renderings are held to a property no baseline can express: they must differ over most of
///         the frame, and the dark one must be darker. That is what fails on the day
///         <c>ControlTheme.vcss</c>'s <c>root.dark</c> block stops matching — at which point the two
///         goldens below would be a picture of the light theme committed twice, both green forever.
///     </para>
///     <para>
///         <b>A gallery rather than a control apiece, and that is a judgement about review and not
///         about coverage.</b> A golden is only worth what the person who accepted it looked at, and
///         nobody looks at seventy-eight small images. One frame holding the controls whose
///         appearance is most nearly <i>all</i> palette — a surface, a border, an accent fill, muted
///         text and a danger colour — is a picture a reviewer can actually read, and it moves for
///         every token in the block.
///     </para>
///     <para>
///         ⚠ <b>And there is evidence for that judgement rather than only taste.</b>
///         <c>ControlTheme.vcss</c> has exactly one <c>root.dark</c> block and it declares tokens and
///         nothing else — not a single dark-scoped <i>component</i> rule in the whole sheet. So every
///         control in this project is dark by substitution alone, and thirty-nine per-control dark
///         references would be thirty-nine pictures of the one fact the test above already asserts.
///         The tree's only per-control dark rules are the five <c>root.dark .tok-*</c> lines in
///         <c>AdvancedTheme.vcss</c>, which are in another assembly and are covered by
///         <c>Vixen.Ui.Controls.Advanced.Tests.SyntaxThemeTests</c> — on a contrast oracle, because
///         a syntax colour that loses its dark rule is unreadable rather than merely different.
///     </para>
/// </remarks>
public class ControlThemeVisualTests {
    /// <summary>How wide and tall the gallery is. Small, for <c>ControlVisualTests</c>' reason.</summary>
    const float Width = 220f;
    const float Height = 190f;

    /// <summary>
    ///     ⚠ <b><c>flex-direction: column</c>, because the root's initial value is <c>row</c>.</b> A
    ///     gallery laid out in a row puts six controls off the right edge of a 220-pixel frame and
    ///     commits a picture of the two that fitted — which still passes the luminance assertion, and
    ///     is the way a golden quietly becomes a golden of almost nothing.
    ///     <para>
    ///     ⚠ <b>The root paints <c>--surface</c>, which is what puts the palette under everything.</b>
    ///     <c>UiTestOptions.Background</c> is a fixed dark grey, so a gallery that did not paint its
    ///     own ground would show the light theme's controls on the dark theme's backdrop — and the
    ///     luminance assertion below would be measuring the harness rather than the stylesheet.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>flex-shrink: 0</c>, because six declared heights, five gaps and two paddings come
    ///     to 196 in a 190-point frame.</b> Since #628 gave the bridge CSS's initial shrink of 1 the
    ///     column divides that six-point deficit among whichever controls have slack above their
    ///     automatic minimum — measured, the textbox loses four and the slider two — so the gallery
    ///     would become a picture of the deficit rather than of the palette, and the two goldens
    ///     would move for a reason that has nothing to do with either theme. The declared sizes are
    ///     the subject here; the frame is deliberately small and is allowed to clip.
    /// </remarks>
    const string Css = """
        root      { flex-direction: column; align-items: flex-start;
                    background-color: var(--surface); padding: 8px; gap: 6px; }
        button    { width: 90px; height: 26px; flex-shrink: 0; }
        checkbox  { width: 120px; height: 20px; flex-shrink: 0; }
        textbox   { width: 180px; height: 26px; flex-shrink: 0; }
        slider    { width: 180px; height: 18px; flex-shrink: 0; }
        alert     { width: 180px; height: 34px; flex-shrink: 0; }
        """;

    /// <summary>Builds the gallery, in one theme or the other.</summary>
    /// <param name="dark">Whether to put the <c>dark</c> class on the root.</param>
    /// <remarks>
    ///     The class and nothing else, because that is the whole of how an application asks for the
    ///     dark palette — <c>ControlTheme.vcss</c>'s <c>root.dark</c> is <c>DarkModeStrategy.Class</c>
    ///     as the utility generator already understands it. A fixture that switched themes any other
    ///     way would be testing a mechanism no application uses.
    /// </remarks>
    static UiTest Gallery(bool dark) {
        var ui = ControlHarness.Open(Width, Height, Css);

        if (dark) {
            ui.Document.Root.AddClass("dark");
        }

        var primary = ui.Add<Button>("go");
        primary.Label = "Go";
        primary.Variant = ControlVariant.Primary;

        var danger = ui.Add<Button>("stop");
        danger.Label = "Stop";
        danger.Variant = ControlVariant.Danger;

        var box = ui.Add<CheckBox>("tick");
        box.Label = "Shadows";
        box.IsChecked = true;

        var field = ui.Add<TextBox>("name");
        field.Value = "Materials";

        var slider = ui.Add<Slider>("amount");
        slider.Value = 0.4f;

        var alert = ui.Add<Alert>("notice");
        alert.Title = "Careful";

        ui.Frame();
        return ui;
    }

    /// <summary>The mean of the three colour channels over the whole frame.</summary>
    static double Luminance(in Bitmap image) {
        var total = 0L;

        for (var i = 0; i < image.Width * image.Height; i++) {
            total += image.Pixels[(i * 4) + 0] + image.Pixels[(i * 4) + 1] + image.Pixels[(i * 4) + 2];
        }

        return total / (double)(image.Width * image.Height * 3);
    }

    /// <summary>How many pixels of the two frames are not the same colour.</summary>
    static int Differing(in Bitmap left, in Bitmap right) {
        var count = 0;

        for (var i = 0; i < left.Width * left.Height; i++) {
            if (left.Pixels[i * 4] != right.Pixels[i * 4]
                || left.Pixels[(i * 4) + 1] != right.Pixels[(i * 4) + 1]
                || left.Pixels[(i * 4) + 2] != right.Pixels[(i * 4) + 2]) {
                count++;
            }
        }

        return count;
    }

    /// <summary>The instrument, asserted before either golden is trusted.</summary>
    /// <remarks>
    ///     ⚠ <b>Both halves, because either alone is satisfiable by a broken theme.</b> "The frames
    ///     differ" is met by a single token changing; "the dark one is darker" is met by a fixture
    ///     that drew nothing at all in the dark case. Together they say the palette swapped: most of
    ///     the surface changed colour, and it changed in the direction the block says it should.
    /// </remarks>
    [Fact]
    public void The_dark_class_repaints_most_of_the_frame_and_makes_it_darker() {
        using var light = Gallery(dark: false);
        using var dark = Gallery(dark: true);

        var lit = light.Capture();
        var dim = dark.Capture();

        var differing = Differing(lit, dim);
        var pixels = lit.Width * lit.Height;

        Assert.True(
            differing > pixels / 2,
            $"only {differing} of {pixels} pixels changed, so the `dark` class is not reaching the palette"
        );

        Assert.True(
            Luminance(dim) < Luminance(lit) - 32d,
            $"the dark gallery averages {Luminance(dim):0.0} against the light one's {Luminance(lit):0.0}"
        );
    }

    [Fact]
    public void The_light_gallery_matches_its_reference() {
        using var ui = Gallery(dark: false);
        ui.Screenshot("theme-gallery-light");
    }

    [Fact]
    public void The_dark_gallery_matches_its_reference() {
        using var ui = Gallery(dark: true);
        ui.Screenshot("theme-gallery-dark");
    }
}
