// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui.Testing;
using Vixen.Ui.Text;

namespace Vixen.Ui.Controls.Tests;

/// <summary>A themed document driven through <see cref="UiTest" />.</summary>
/// <remarks>
///     <para>
///         The second harness in this project, and the two are not redundant.
///         <see cref="ControlFixture" /> answers "did the control change its state when it was
///         clicked" and does it by sending events by hand; this answers "would a player be able to
///         do that, and does it look right afterwards" — which needs the waiting, the hit-test check
///         before every action, and a picture.
///     </para>
///     <para>
///         ⚠ <b>The real theme and a real font</b>, for the reason <see cref="ControlFixture" />
///         gives: half of what a control does is arrange for a stylesheet to be able to say
///         something, and a test against an unstyled document cannot tell a control that does that
///         correctly from one that does not do it at all. A picture of an unstyled control is a
///         picture of nothing.
///     </para>
/// </remarks>
static class ControlHarness {
    static readonly FontFace Font = LoadFont();

    /// <summary>Opens a themed interface of the given size.</summary>
    /// <param name="width">The viewport's width.</param>
    /// <param name="height">Its height.</param>
    /// <param name="css">Anything the test wants on top of the theme.</param>
    public static UiTest Open(float width = 400f, float height = 300f, string? css = null) {
        var ui = UiTest.Create(width, height);
        ui.Document.Fonts.Register("Test", Font);

        ControlTheme.Install(ui.Document);
        ui.Load($"root {{ width: {width}px; height: {height}px; }}");

        if (css is not null) {
            ui.Load(css);
        }

        ui.Frame();
        return ui;
    }

    /// <summary>Adds a control under the root and lays the document out.</summary>
    public static T Add<T>(this UiTest ui, string? id = null) where T : UiElement, new() {
        var control = ui.Document.Create<T>(null, ui.Document.Root, id);
        ui.Frame();

        return control;
    }

    /// <summary>How much light the picture is putting out, summed over every pixel.</summary>
    /// <remarks>
    ///     The crudest possible measure, and the right one for the claims it is used for: that a
    ///     control draws <i>something</i>, that a disabled one draws less, that a collapsed one draws
    ///     nothing. Anything finer would be a second implementation of the thing being tested.
    /// </remarks>
    public static long Ink(this UiTest ui) {
        var image = ui.Capture();
        var total = 0L;

        for (var i = 0; i < image.Width * image.Height; i++) {
            total += image.Pixels[(i * 4) + 0] + image.Pixels[(i * 4) + 1] + image.Pixels[(i * 4) + 2];
        }

        return total;
    }

    /// <summary>How much light there is inside a rectangle of the picture.</summary>
    /// <remarks>
    ///     What answers "did the thumb move", which no assertion about the element tree can: a
    ///     slider's thumb is not an element, so the only place it exists is in the pixels.
    /// </remarks>
    public static long InkIn(this UiTest ui, int left, int top, int width, int height) {
        var image = ui.Capture();
        var total = 0L;

        for (var y = Math.Max(0, top); y < Math.Min(image.Height, top + height); y++) {
            for (var x = Math.Max(0, left); x < Math.Min(image.Width, left + width); x++) {
                var offset = image.Offset(x, y);
                total += image.Pixels[offset] + image.Pixels[offset + 1] + image.Pixels[offset + 2];
            }
        }

        return total;
    }

    static FontFace LoadFont() {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Vixen.Ui.Controls.Tests.Fonts.TestShapeLana.ttf")
            ?? throw new InvalidOperationException("the test font is not embedded");

        using var memory = new MemoryStream();
        stream.CopyTo(memory);

        return FontFace.Load(memory.ToArray(), name: "TestShapeLana");
    }
}
