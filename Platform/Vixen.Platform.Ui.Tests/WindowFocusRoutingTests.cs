// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Platform.Ui.Tests;

/// <summary>Which window the user is in reaches the document, which for its whole life it did not.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same seam <c>TextCompositionRoutingTests</c> is about, and the same shape of
///         gap.</b> <c>PlatformEventKind.WindowFocusGained</c> and <c>WindowFocusLost</c> are
///         produced by every backend the engine has — <c>DesktopPlatform</c> from SDL,
///         <c>WebPlatform</c> from the canvas's focus and blur, <c>HeadlessWindow</c> from its own
///         harness — and <c>PlatformInput.Dispatch</c> had no arm for either, so both fell through
///         the <c>default</c> and were dropped. Two tested halves and an untested join.
///     </para>
///     <para>
///         ⚠ <b>The symptom is not an error.</b> A key event is not routed by surface — the focus is
///         the document's, not a window's — so with nothing focused every keystroke went to the
///         <i>primary</i> surface's root. In a one-window application that is right by construction,
///         which is why nothing caught it; in one that has torn a panel off into its own window it
///         means a key pressed in the inspector ran against the main window.
///     </para>
/// </remarks>
public class WindowFocusRoutingTests {
    [Fact]
    public void A_window_taking_focus_becomes_the_documents_key_surface() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        Assert.Null(document.KeySurface);

        Assert.True(PlatformInput.Dispatch(document, second, PlatformEvent.Window(PlatformEventKind.WindowFocusGained, 2, 0)));
        Assert.Same(second, document.KeySurface);

        Assert.True(PlatformInput.Dispatch(document, second, PlatformEvent.Window(PlatformEventKind.WindowFocusLost, 2, 0)));
        Assert.Null(document.KeySurface);
    }

    [Fact]
    public void A_keystroke_with_nothing_focused_follows_the_window_the_user_is_in() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        var main = 0;
        var inspector = 0;

        document.Root.AddHandler<KeyEvent>((_, _) => main++);
        second.Root.AddHandler<KeyEvent>((_, _) => inspector++);

        PlatformInput.Dispatch(document, second, PlatformEvent.Window(PlatformEventKind.WindowFocusGained, 2, 0));
        PlatformInput.Dispatch(
            document,
            second,
            PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 2, 0, Key.F5, KeyModifiers.None)
        );

        // The document root is every element's last ancestor, so it hears the event on the bubble
        // leg either way. What changed is where the route *starts*, and that is what decides which
        // handler sees it first and which control could take it.
        Assert.Equal(1, inspector);
    }

    /// <summary>
    ///     ⚠ <b>And the window manager's opinion is no longer the only route.</b> The bridge now
    ///     passes the surface the platform delivered the key to, so a keystroke lands in the right
    ///     window even when <c>WindowFocusGained</c> never arrived — a backend that does not produce
    ///     it, a host driving the document itself, a replayed trace.
    /// </summary>
    [Fact]
    public void A_keystroke_follows_the_window_it_was_delivered_to_with_no_focus_event_at_all() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        var inspector = 0;
        second.Root.AddHandler<KeyEvent>((_, _) => inspector++);

        PlatformInput.Dispatch(
            document,
            second,
            PlatformEvent.Keyboard(PlatformEventKind.KeyDown, 2, 0, Key.F5, KeyModifiers.None)
        );

        Assert.Null(document.KeySurface);
        Assert.Equal(1, inspector);
    }

    [Fact]
    public void Losing_focus_to_another_window_does_not_undo_that_windows_gain() {
        using var document = new UiDocument(200f, 100f);
        var second = document.CreateSurface(120f, 80f);

        // ⚠ Gained then lost, in that order, from two different windows — which is ordinary and not
        // a corner. A window manager owes no ordering between the pair, and an unconditional clear
        // on Lost would take the key status away from the window that had just been given it and
        // leave the application with no key window at all.
        PlatformInput.Dispatch(document, second, PlatformEvent.Window(PlatformEventKind.WindowFocusGained, 2, 0));
        PlatformInput.Dispatch(document, document.Primary, PlatformEvent.Window(PlatformEventKind.WindowFocusLost, 1, 0));

        Assert.Same(second, document.KeySurface);
    }
}
