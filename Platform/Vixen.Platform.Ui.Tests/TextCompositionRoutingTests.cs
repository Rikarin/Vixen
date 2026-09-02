// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>
///     An input method's pre-edit reaches the document, which for four years it did not.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The gap this closes was invisible from both ends, and that is the point of putting a
///         test on the seam rather than on either side of it.</b>
///         <c>PlatformEventKind.TextEditing</c> has existed since the platform layer was written, is
///         documented on <c>PlatformEvent</c>, is produced by <c>DesktopPlatform</c> from SDL and by
///         <c>WebPlatform</c> from the invisible <c>&lt;input&gt;</c>'s composition events, and has a
///         constructor test of its own in <c>Vixen.Platform.Tests</c>. <c>PlatformInput.Dispatch</c>
///         had no arm for it, so every one of them fell through the <c>default</c> and was dropped.
///         Both halves were tested and correct; the join was neither.
///     </para>
///     <para>
///         ⚠ <b>The symptom is not an error and not a blank frame.</b> A Japanese, Chinese or Korean
///         user types, the field shows nothing at all until the composition commits, and the
///         candidate window — which <c>ITextInput.SetCandidateArea</c> does place correctly — floats
///         over an empty box. Nothing logs, no counter moves, and every English test passes.
///     </para>
/// </remarks>
public class TextCompositionRoutingTests {
    /// <summary>A composition event reaches the focused element as a composition.</summary>
    /// <remarks>
    ///     ⚠ <b>The cursor fields are asserted too, and they are the half a routing change forgets.</b>
    ///     <c>SelectionStart</c> and <c>SelectionLength</c> are the input method's own cursor
    ///     <i>within the pre-edit</i>; dropped, the caret sits in front of a half-converted phrase
    ///     rather than inside it, which is a working-looking field that is wrong for exactly the
    ///     users the feature is for.
    /// </remarks>
    [Fact]
    public void A_composition_event_reaches_the_document() {
        using var document = new UiDocument(200f, 100f);
        // ⚠ On the root, because a bare `div` is not focusable and `Dispatch` falls back to the
        // root when nothing has the focus — which is the path a document with no field in it takes.
        TextCompositionEvent? seen = null;
        document.Root.AddHandler<TextCompositionEvent>((_, args) => seen = args);

        var handled = PlatformInput.Dispatch(document, PlatformEvent.TextEditing(1, 0, "にほ", 1, 1));

        Assert.True(handled);
        Assert.NotNull(seen);
        Assert.Equal("にほ", seen.Text);
        Assert.Equal(1, seen.Start);
        Assert.Equal(1, seen.Length);
    }

    /// <summary>And it arrives as a composition rather than as typed text.</summary>
    /// <remarks>
    ///     ⚠ <b>The load-bearing half.</b> Routing a pre-edit to <c>TextInputEvent</c> is the obvious
    ///     way to make the field show something, and it is worse than dropping it: every intermediate
    ///     reading of every word ends up committed into the value. This is the assertion that says
    ///     the two are different events.
    /// </remarks>
    [Fact]
    public void A_composition_is_not_delivered_as_typed_text() {
        using var document = new UiDocument(200f, 100f);
        var typed = 0;
        document.Root.AddHandler<TextInputEvent>((_, _) => typed++);

        PlatformInput.Dispatch(document, PlatformEvent.TextEditing(1, 0, "にほ", 2, 0));

        Assert.Equal(0, typed);
    }
}
