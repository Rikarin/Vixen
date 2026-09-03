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
    ///     Skips where a committed picture cannot be compared, which is every platform but the one
    ///     that recorded it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The same gate, and the same argument, as <c>EditorChromeVisualTests</c>.</b> Shaping
    ///     goes through HarfBuzz and <c>Vixen.Ui.Text</c> takes a different native package per
    ///     platform, which places the same glyphs a fraction differently — indistinguishable to look
    ///     at, and about eight per cent of the pixels. A tolerance wide enough to admit that would
    ///     admit a collapsed layout, which is the only thing this test exists to catch.
    ///     ⚠ <b>And CI has no leg on which this may not skip</b>, which is the second half of #500.
    /// </remarks>
    static void SkipWhereTheReferenceDoesNotApply() =>
        Assert.SkipUnless(
            OperatingSystem.IsMacOS(),
            "the committed screenshots are recorded on macOS, and HarfBuzz's per-platform natives "
            + "place glyphs differently enough that a pixel comparison against them is meaningless "
            + "elsewhere."
        );

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
