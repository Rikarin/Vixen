// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The asset picker, as a picture.</summary>
/// <remarks>
///     <para>
///         <b>The grid is a visual change and no assertion about it is evidence for one.</b> The tests
///         beside this one say the tiles exist, that the right one answers the dialog and that a
///         picture reaches a tile once it has been decoded — all of which stayed true through the
///         version of this grid that laid out as a single column down the left, because a control
///         whose host element collapses is still bound, still hit-testable and still wrong.
///     </para>
///     <para>
///         ⚠ <b>This is also the first thing in the tree that photographs a panel of the editor
///         <i>as the application assembles it</i>.</b> <c>EditorChromeVisualTests</c> builds its own
///         shell with five test panels, so what it holds is the palette and the chrome; nothing until
///         now held any real surface. That gap is <a href="https://github.com/Rikarin/Vixen/issues/500">#500</a>,
///         and one picture does not close it.
///     </para>
/// </remarks>
public class PickerVisualTests {
    /// <summary>
    ///     Skips where the committed picture is not compared, and fails instead of skipping where
    ///     <c>VIXEN_REQUIRE_SCREENSHOTS=1</c> says the comparison must happen — which CI says on the
    ///     macOS leg (<a href="https://github.com/Rikarin/Vixen/issues/500">#500</a>).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>macOS and Windows, which <c>EditorChromeVisualTests</c> measured to shape this font
    ///         identically</b>, so the per-platform-glyphs argument holds for Linux only. Windows was
    ///         held back while the reference was stale (#1477), and the staleness turned out to be what
    ///         this test exists to catch: two strings that had started to WRAP. The inspector's
    ///         "Build Settings" tab title wrapped to two lines and stretched its whole tab strip ten
    ///         pixels below the hierarchy's, and the hierarchy's filter prompt wrapped to three and
    ///         showed the middle one. Both are one line again (<c>DockTabTitleTests</c>,
    ///         <c>PlaceholderLineTests</c>), and the reference was re-taken after, not instead.
    ///     </para>
    ///     <para>
    ///         A tolerance wide enough to admit a platform's glyph placement would admit a collapsed
    ///         layout, which is the only thing this test exists to catch.
    ///     </para>
    /// </remarks>
    static void SkipWhereTheReferenceDoesNotApply() {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) {
            return;
        }

        const string why = "the committed screenshot is compared on macOS and Windows; Linux's HarfBuzz "
            + "natives place glyphs differently enough that a pixel comparison is meaningless there.";

        Assert.False(
            Environment.GetEnvironmentVariable("VIXEN_REQUIRE_SCREENSHOTS") == "1",
            $"VIXEN_REQUIRE_SCREENSHOTS=1, and this platform has no references: {why}"
        );

        Assert.Skip(why);
    }

    /// <summary>
    ///     ⚠ <b>What this is looking for is a grid that is a grid.</b> "Everything in a strip down the
    ///     left" and "the panel is blank" are the same trap one level apart — an element nobody
    ///     styled, a CSS-initial <c>row</c>, a missing height — and this editor has had it twice. A
    ///     dialog is where it is most likely, because a dialog body sizes itself to its contents and
    ///     <c>asset-grid</c> asks to grow into a parent that has no height of its own.
    /// </summary>
    [Fact]
    public void The_asset_picker_is_a_grid_of_tiles() {
        SkipWhereTheReferenceDoesNotApply();

        using var editor = EditorSession.Start(new EditorSessionOptions { Width = 900f, Height = 700f });

        var descriptor = InspectorRegistry.Find(typeof(PickerFixture))
            ?? throw editor.Fail("the generator registered no descriptor for PickerFixture");

        var member = descriptor.Members.Single(candidate => candidate.Name == "Anything");

        new AssetPicker(editor.Project, editor.Shell.Dialogs).Open(
            new InspectorField(descriptor, member, [new PickerFixture()], editor.Scene)
        );

        // Two frames to open and lay out, which is what every other test of this dialog waits for.
        editor.Frames(2);

        editor.Screenshot("asset-picker-grid");
    }
}
