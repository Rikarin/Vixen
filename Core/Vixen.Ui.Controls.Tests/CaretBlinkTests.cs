// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;
using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The caret goes out and comes back, measured as pixels rather than as a property.</summary>
/// <remarks>
///     <para>
///         <b>The picture is the assertion, because the property is not the claim.</b> A field with a
///         <c>CaretBlink</c> that nothing consults would satisfy every test that read the property
///         back, and that is exactly the shape this repository keeps producing — a finished thing
///         nothing calls. So the oracle is the green pixel count of a caret drawn over red glyphs in
///         a blue field on a black ground: four colours, three of which are things a careless scan
///         would mistake for the caret. The trick and its reasoning are
///         <see cref="CaretColourPixelTests" />'s.
///     </para>
///     <para>
///         ⚠ <b>No wall clock anywhere, and the numbers are exact rather than bounded.</b> The
///         harness's frame delta is set to 10 ms and the blink to 100 ms, so a half period is ten
///         frames and every assertion below lands on a boundary instead of near one. A budget in
///         milliseconds measured against a real clock is this repository's largest single source of
///         flakes; ten frames is ten frames on a loaded machine.
///     </para>
///     <para>
///         ⚠ <b><see cref="Typing_holds_the_caret_solid" /> is the test that separates a blink from a
///         flicker</b>, and it is the one a naive implementation fails. A caret whose phase runs off a
///         free-running clock blinks out in the middle of a word — the character lands, the caret is
///         somewhere else, and the eye has nothing to follow. The phase has to be measured from the
///         last time the caret moved, which is why 190 ms of elapsed time with a keystroke in the
///         middle of it has to be lit and 190 ms without one has to be dark.
///     </para>
/// </remarks>
public class CaretBlinkTests {
    static readonly FontFace Font = LoadFont();

    /// <summary>Ten frames to a half period, so nothing here lands near a boundary.</summary>
    static readonly TimeSpan Blink = TimeSpan.FromMilliseconds(100);

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }

    /// <summary>A focused green-caret field in a document whose clock steps 10 ms a frame.</summary>
    static (UiTest Ui, TextBox Field) Field(bool focused = true) {
        var ui = UiTest.Create(240f, 120f, new UiTestOptions { FrameDelta = TimeSpan.FromMilliseconds(10) });
        ui.Document.Fonts.Register("Test", Font);

        ui.Load(
            """
            root       { width: 240px; height: 120px; background-color: #000000;
                         font-family: Test; font-size: 24px; }
            textbox    { position: absolute; left: 20px; top: 20px; width: 160px;
                         flex-direction: row; align-items: center; padding: 4px;
                         background-color: #0000c0; color: #c00000; caret-color: #00ff00; }
            field-text { flex-shrink: 0; white-space: nowrap; min-height: 1.2em; }
            """
        );

        var field = ui.Document.Root.Add<TextBox>();
        field.Value = "AB";
        field.CaretBlink = Blink;

        if (focused) {
            ui.Document.Focus(field);
        }

        return (ui, field);
    }

    /// <summary>How many pixels the caret painted, which is every green one and nothing else.</summary>
    static int Caret(UiTest ui) {
        var image = ui.Capture();
        var green = 0;

        for (var y = 0; y < image.Height; y++) {
            for (var x = 0; x < image.Width; x++) {
                var offset = image.Offset(x, y);

                if (image.Pixels[offset + 1] > 24
                    && image.Pixels[offset + 1] > image.Pixels[offset]
                    && image.Pixels[offset + 1] > image.Pixels[offset + 2]) {
                    green++;
                }
            }
        }

        return green;
    }

    /// <summary>Lit, then out, then lit again — half a period apart each time.</summary>
    /// <remarks>
    ///     ⚠ <b>The third reading is what stops this being satisfied by a caret that was drawn once
    ///     and then lost.</b> "It went away" is the same picture as "it was never drawn again", and
    ///     only the return separates a blink from a control that stopped painting its insertion
    ///     point — which is a bug that would look, on a still screenshot, exactly like a success.
    /// </remarks>
    [Fact]
    public void A_focused_caret_goes_out_and_comes_back() {
        var (ui, _) = Field();
        using var owned = ui;

        ui.Frame();
        Assert.True(Caret(ui) > 0, "the caret was not painted on the frame the field took the focus");

        ui.Frames(10);
        Assert.Equal(0, Caret(ui));

        ui.Frames(10);
        Assert.True(Caret(ui) > 0, "the caret went out and did not come back");
    }

    /// <summary>A keystroke restarts the phase, so the caret is solid for as long as somebody is typing.</summary>
    /// <remarks>
    ///     ⚠ <b>190 ms of elapsed time, which is most of two half periods, and the caret is lit for
    ///     all of it.</b> Sabotaging the restart — measuring the phase from zero rather than from the
    ///     last move — makes the final reading dark, which is what makes this a claim about the
    ///     restart rather than about the blink.
    /// </remarks>
    [Fact]
    public void Typing_holds_the_caret_solid() {
        var (ui, _) = Field();
        using var owned = ui;

        ui.Frame();
        ui.Frames(9);

        Assert.True(Caret(ui) > 0, "the caret went out inside the first half period");

        ui.TypeText("C");
        ui.Frames(10);

        Assert.True(
            Caret(ui) > 0,
            "the caret blinked out 190 ms after it appeared, so the phase is not measured from the keystroke"
        );
    }

    /// <summary>Zero is a solid caret, which is what a reduced-motion setting asks for.</summary>
    /// <remarks>
    ///     ⚠ <b>Ten half periods, so a blink that merely ran slowly would still have crossed one.</b>
    ///     The claim is that the arithmetic is skipped, not that it is stretched.
    /// </remarks>
    [Fact]
    public void A_blink_of_zero_is_a_caret_that_never_goes_out() {
        var (ui, field) = Field();
        using var owned = ui;

        field.CaretBlink = TimeSpan.Zero;

        for (var half = 0; half < 10; half++) {
            ui.Frames(10);
            Assert.True(Caret(ui) > 0, $"the caret went out {half + 1} half periods in with blinking turned off");
        }
    }

    /// <summary>A field nobody is typing into costs nothing, which is the whole reason this is not a subscription.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the test <c>EditorStillnessTests</c> would have gone red for.</b> A shell
    ///         has dozens of fields on it and at most one of them has the focus; a caret that ticked
    ///         whether or not it was drawn would make every one of those frames differ, and "the
    ///         editor is still" — a property that took a measurement to establish — would have
    ///         quietly stopped being true.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the focused half is asserted too, because the still half alone is satisfied by
    ///         a caret that never blinks at all.</b> A predicate that cannot be false is worse than
    ///         the thing it replaced.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_unfocused_field_is_still_and_a_focused_one_is_not() {
        var (ui, field) = Field(focused: false);
        using var owned = ui;

        ui.Frames(4);

        var redraws = ui.Redraws;

        ui.Frames(60);
        Assert.Equal(redraws, ui.Redraws);

        ui.Document.Focus(field);
        ui.Frames(4);

        redraws = ui.Redraws;

        // Sixty frames is six half periods, so six flips and nothing else — the field is settled and
        // the caret is the only thing in the document with anything new to say.
        ui.Frames(60);
        Assert.Equal(redraws + 6, ui.Redraws);
    }
}
