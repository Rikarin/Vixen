// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>Typed text and a pre-edit follow the window they were delivered to, as a key already did.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A keystroke and the character it produces are two platform events, and until this they
///         were routed by two different rules.</b> <c>KeyDown</c> was given the surface it arrived at;
///         <c>TextInput</c> and <c>TextEditing</c> were not, and fell back to <c>Primary</c>'s root
///         whenever nothing was focused. So the key reached the torn-off inspector and the letter it
///         typed reached the main window — the two halves of one keystroke landing in two windows.
///     </para>
///     <para>
///         ⚠ <b>Nothing logs and nothing throws</b>, which is why this outlived the key-window work
///         sitting immediately beside it: in a one-window application the two rules agree by
///         construction, and every test written on a single surface passes either way. The surface
///         has to be a second one for the question to have an answer at all.
///     </para>
///     <para>
///         ⚠ <b>The assertion is on the secondary root and not on the primary one</b>, because
///         <c>UiDocument.Root</c> <i>is</i> the primary surface's root and is every other surface's
///         ancestor — so it hears the event on the bubble leg whichever way the route was started, and
///         a test that counted it would be green against both. What the old routing could never do is
///         reach a secondary root at all: it is an ancestor of nothing the primary route walks.
///     </para>
/// </remarks>
public class TextWindowRoutingTests {
    /// <summary>Typed text with nothing focused lands in the window it was typed into.</summary>
    [Fact]
    public void Typed_text_follows_the_window_it_was_delivered_to() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        var inspector = 0;
        second.Root.AddHandler<TextInputEvent>((_, _) => inspector++);

        Assert.True(PlatformInput.Dispatch(document, second, PlatformEvent.TextInput(2, 0, "a")));

        Assert.Equal(1, inspector);
    }

    /// <summary>And the route starts there rather than merely passing through it.</summary>
    /// <remarks>
    ///     The document overload answers with the element it targeted, which is the one thing a
    ///     handler count cannot distinguish — a bubble that happens to arrive and a route that was
    ///     aimed look identical from inside a handler.
    /// </remarks>
    [Fact]
    public void The_route_starts_at_the_window_the_text_was_delivered_to() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        Assert.Same(second.Root, document.Dispatch(second, new TextInputEvent { Text = "a" }));
        Assert.Same(second.Root, document.Dispatch(second, new TextCompositionEvent { Text = "に" }));
    }

    /// <summary>And so does an input method's pre-edit.</summary>
    /// <remarks>
    ///     ⚠ <b>Worse than a character to misroute rather than better.</b> A pre-edit raised on the
    ///     wrong root leaves the window the user is actually composing in with no composition to end,
    ///     so the candidate window floats over a field that will never receive the commit — and the
    ///     field that did receive the pre-edit is in a window nobody is looking at.
    /// </remarks>
    [Fact]
    public void A_pre_edit_follows_the_window_it_was_delivered_to() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        TextCompositionEvent? seen = null;
        second.Root.AddHandler<TextCompositionEvent>((_, args) => seen = args);

        Assert.True(PlatformInput.Dispatch(document, second, PlatformEvent.TextEditing(2, 0, "にほ", 1, 1)));

        Assert.NotNull(seen);

        // The input method's own cursor within the pre-edit, carried through the new overload. A
        // routing change is exactly where these get dropped, and a caret in front of a
        // half-converted phrase rather than inside it is a field that looks like it works.
        Assert.Equal("にほ", seen.Text);
        Assert.Equal(1, seen.Start);
        Assert.Equal(1, seen.Length);
    }

    /// <summary>Text goes to the focus in the window it was delivered to, for both, as a key does.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This test used to assert the opposite, and its premise expired rather than its
    ///         intent.</b> It read "<c>UiSurface.Focused</c> does not exist — the focus is one
    ///         document-global element", which was true when it was written and false the moment a
    ///         window gained its own first responder. Under the old model, routing by surface really
    ///         would have taken a character away from the control the user was in; under this one it
    ///         is what puts it there.
    ///     </para>
    ///     <para>
    ///         So the rule is the same rule and the subject moved: text follows the focus, and the
    ///         focus is the one belonging to the surface the event was delivered to. A field focused
    ///         in another window does not catch what is typed into this one — which is the half a
    ///         document-global read got wrong, and the half that matters, because the two halves of
    ///         one keystroke must not part company: the key already routes this way.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Text_goes_to_the_focus_in_the_window_it_was_delivered_to() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        var elsewhere = document.Create("div", document.Root);
        elsewhere.Focusable = true;

        var here = document.Create("div", second.Root);
        here.Focusable = true;

        Assert.True(document.Focus(elsewhere));

        // Nothing is focused in `second`, so its own root takes it — not the field focused in the
        // window the user is not typing into.
        Assert.Same(second.Root, document.Dispatch(second, new TextInputEvent { Text = "a" }));
        Assert.Same(second.Root, document.Dispatch(second, new TextCompositionEvent { Text = "に" }));

        Assert.True(document.Focus(here));

        Assert.Same(here, document.Dispatch(second, new TextInputEvent { Text = "b" }));
        Assert.Same(here, document.Dispatch(second, new TextCompositionEvent { Text = "ほ" }));

        // And the other window still answers for its own.
        Assert.Same(elsewhere, document.Dispatch(document.Primary, new TextInputEvent { Text = "c" }));
    }

    /// <summary>A surface from another document is refused rather than quietly routed to the primary.</summary>
    /// <remarks>
    ///     ⚠ The refusal is the point. Falling back to <c>Primary</c> on a surface this document does
    ///     not own would be precisely the behaviour these overloads exist to stop, arrived at from a
    ///     different direction and with no way to notice.
    /// </remarks>
    [Fact]
    public void A_surface_from_another_document_is_refused() {
        using var document = new UiDocument(200f, 100f);
        using var other = new UiDocument(200f, 100f);

        Assert.Throws<ArgumentException>(() => document.Dispatch(other.Primary, new TextInputEvent { Text = "a" }));
        Assert.Throws<ArgumentException>(() => document.Dispatch(other.Primary, new TextCompositionEvent()));
    }
}
