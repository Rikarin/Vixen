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
    ///         ⚠ <b>macOS only, for a different reason from the one this used to give.</b>
    ///         <c>EditorChromeVisualTests</c> measured that Windows and macOS shape this font
    ///         identically, so the per-platform-glyphs argument holds for Linux and not for Windows.
    ///         This picture is held back on Windows because rendered there (2026-10-18) it differs
    ///         from the committed reference by 1.40 %, and not in text: the inspector's tab strip sits
    ///         about ten pixels lower than the hierarchy's beside it, the picker's search field has a
    ///         caret, the hierarchy's filter placeholder reads differently, and the console's empty
    ///         line is muted. The tab strip may be the layout regression this test exists to catch,
    ///         so the reference is not re-taken to make it pass — see
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1477">#1477</a>. Once that is settled,
    ///         Windows belongs in the condition below.
    ///     </para>
    ///     <para>
    ///         A tolerance wide enough to admit a platform's glyph placement would admit a collapsed
    ///         layout, which is the only thing this test exists to catch.
    ///     </para>
    /// </remarks>
    static void SkipWhereTheReferenceDoesNotApply() {
        if (OperatingSystem.IsMacOS()) {
            return;
        }

        const string why = "the committed screenshot is compared on macOS; on Windows it waits on #1477 "
            + "(a stale reference that may hide an inspector tab-strip regression), and Linux's HarfBuzz "
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
