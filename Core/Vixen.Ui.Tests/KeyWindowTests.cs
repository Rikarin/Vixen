// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Which window a keystroke belongs to, asked of the event rather than of a global.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>Dispatch(KeyEvent)</c> took no surface while the pointer and the wheel overloads
///         both did</b>, and the stated reason — a key goes to the focus and the focus is the
///         document's — stops being an answer the moment nothing is focused, which is the state
///         every application starts in. The fallback was <c>Primary</c>'s root, so a keystroke the
///         operating system delivered to a torn-off inspector ran against the main window.
///     </para>
///     <para>
///         ⚠ <b>This is not a duplicate of <see cref="UiDocument.KeySurface" />.</b> The key surface
///         is the window manager's opinion arriving through <c>WindowFocusGained</c>; the surface
///         passed here is where <i>this</i> event was actually delivered. The second is the better
///         answer where both exist, and the only one where the bridge that supplies the first has
///         not been wired — a host embedding Vixen in its own event loop, for instance.
///     </para>
///     <para>
///         ⚠ <b>The focus that outranks the delivering surface is now that surface's own.</b>
///         <see cref="UiSurface.Focused" /> exists, so each window keeps its own first responder and
///         a keystroke delivered to a torn-off inspector can no longer reach a field in the main
///         window. Until it did, these tests asserted the opposite <i>as present behaviour</i> —
///         which is what made the change show up here as two red tests rather than as nothing at
///         all.
///     </para>
/// </remarks>
public class KeyWindowTests {
    static KeyEvent Pressed(InputKey key = InputKey.F5) => new() { Key = key, Action = KeyAction.Pressed };

    [Fact]
    public void A_key_delivered_to_a_surface_starts_at_that_surface_s_root() {
        using var document = new UiDocument(200f, 100f);
        var inspector = document.CreateSurface(120f, 80f);

        var started = document.Dispatch(inspector, Pressed());

        // ⚠ And `KeySurface` was never set. Before the overload the only way to aim a keystroke at a
        // second window was through the window manager's `WindowFocusGained`, so a host that drives
        // the document itself — an embedder, a test, a replayed trace — had no way at all.
        Assert.Null(document.KeySurface);
        Assert.Same(inspector.Root, started);
    }

    [Fact]
    public void The_focus_that_outranks_the_delivering_surface_is_that_surface_s_own() {
        using var document = new UiDocument(200f, 100f);
        var inspector = document.CreateSurface(120f, 80f);

        var field = document.Root.Add("div");
        field.Focusable = true;

        Assert.True(document.Focus(field));

        // ⚠ **The half that was owed, and it went the other way before.** A field in the main window
        // holding the focus used to take a key the operating system had delivered to the inspector,
        // because the focus was one element for the whole document and the surface only decided the
        // fallback. The main window keeps its caret — nothing was blurred — and the key goes to the
        // window it arrived at.
        Assert.Same(inspector.Root, document.Dispatch(inspector, Pressed()));
        Assert.Same(field, document.Primary.Focused);

        // And within that window the focus outranks the root exactly as it always did, which is the
        // rule the old assertion was really about.
        var probe = inspector.Root.Add("div");
        probe.Focusable = true;

        Assert.True(document.Focus(probe));
        Assert.Same(probe, document.Dispatch(inspector, Pressed()));

        // ⚠ Two carets in one document, one per window, and both still there. This is what lets the
        // user come back to the main window and go on typing where she left off, with no
        // focus-restore machinery anywhere.
        Assert.Same(field, document.Primary.Focused);
        Assert.Same(probe, inspector.Focused);
    }

    /// <summary>
    ///     ⚠ <b>The issue's Gate, with something focused in each window.</b> The verb, the document
    ///     and the two handlers are the same; which one runs is decided by which window the user is
    ///     in. While the command origin was one document-global element this could not be written at
    ///     all — a menu opened over the inspector resolved <c>edit.copy</c> against whatever the main
    ///     window had last focused.
    /// </summary>
    [Fact]
    public void A_command_resolves_from_the_focus_in_the_key_window() {
        using var document = new UiDocument(200f, 100f);
        var inspector = document.CreateSurface(120f, 80f);

        var here = document.Root.Add("div");
        var there = inspector.Root.Add("div");

        here.Focusable = true;
        there.Focusable = true;

        var ran = "";
        here.AddCommandHandler("edit.copy", () => ran = "main");
        there.AddCommandHandler("edit.copy", () => ran = "inspector");

        Assert.True(document.Focus(here));
        Assert.True(document.Focus(there));

        // Nothing has been named key yet, so "the focus" is still the primary's — and the inspector
        // holds its own caret at the same time without either having taken the other's.
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("main", ran);

        document.KeySurface = inspector;

        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("inspector", ran);
    }

    /// <summary>
    ///     ⚠ <b>Tab is window-local, because a surface root ends the scope climb.</b> A surface
    ///     root's parents run on to the document root, so without that rule Tab in the inspector
    ///     would walk into the main window's controls — moving the focus to a window the user is not
    ///     in, and leaving the key window with none.
    /// </summary>
    [Fact]
    public void Tab_in_one_window_does_not_walk_into_another() {
        using var document = new UiDocument(200f, 100f);
        var inspector = document.CreateSurface(120f, 80f);

        var here = document.Root.Add("div");
        here.Focusable = true;

        var first = inspector.Root.Add("div");
        var second = inspector.Root.Add("div");

        first.Focusable = true;
        second.Focusable = true;

        document.KeySurface = inspector;

        Assert.True(document.Focus(first));
        Assert.True(document.MoveFocus(FocusDirection.Next));
        Assert.Same(second, document.Focused);

        // ⚠ And it wraps rather than escaping: two stops in this window, so Next from the last is
        // the first again and never the control in the other one.
        Assert.True(document.MoveFocus(FocusDirection.Next));
        Assert.Same(first, document.Focused);
        Assert.Null(document.Primary.Focused);
    }

    [Fact]
    public void A_surface_of_another_document_is_refused_rather_than_routed_somewhere() {
        using var document = new UiDocument(200f, 100f);
        using var other = new UiDocument(200f, 100f);

        Assert.Throws<ArgumentException>(() => document.Dispatch(other.Primary, Pressed()));
    }

    [Fact]
    public void The_key_surface_announces_both_edges_and_the_state_is_already_new() {
        using var document = new UiDocument(200f, 100f);
        var first = document.CreateSurface(120f, 80f);
        var second = document.CreateSurface(120f, 80f);

        List<(UiSurface Surface, bool IsKeyNow)> seen = [];
        document.KeySurfaceChanged += (_, surface) => seen.Add((surface, ReferenceEquals(document.KeySurface, surface)));

        document.KeySurface = first;
        document.KeySurface = second;

        // ⚠ Both edges and the losing one first, and every raise reads the state *after* the change.
        // A handler that saw "first is still key" while being told second had taken it would draw
        // two active title bars, which is the whole reason the two raises are ordered rather than
        // being one event carrying a before and an after.
        Assert.Equal(3, seen.Count);
        Assert.Equal((first, true), seen[0]);
        Assert.Equal((first, false), seen[1]);
        Assert.Equal((second, true), seen[2]);
    }

    [Fact]
    public void Setting_the_key_surface_to_what_it_already_is_announces_nothing() {
        using var document = new UiDocument(200f, 100f);
        var first = document.CreateSurface(120f, 80f);

        var raises = 0;
        document.KeySurfaceChanged += (_, _) => raises++;

        document.KeySurface = first;
        document.KeySurface = first;

        // A window manager repeating itself is ordinary — a raise, a click, an alt-tab back — and a
        // title bar that redrew on each would be flickering for no change at all.
        Assert.Equal(1, raises);
    }

    [Fact]
    public void A_window_reads_its_key_status_off_the_document_rather_than_keeping_one() {
        using var document = new UiDocument(200f, 100f);

        IUiWindow main = new Window(document.Primary);
        IUiWindow inspector = new Window(document.CreateSurface(120f, 80f));

        Assert.False(main.IsKey);
        Assert.False(inspector.IsKey);

        document.KeySurface = inspector.Surface;

        // ⚠ There is exactly one key surface per document, so two windows cannot both believe they
        // have it — which is what a `bool` on each window would have made possible, and what the
        // default implementation refuses by construction.
        Assert.False(main.IsKey);
        Assert.True(inspector.IsKey);

        document.KeySurface = null;
        Assert.False(inspector.IsKey);
    }

    /// <summary>The least an <see cref="IUiWindow" /> can be: a surface and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>It implements neither <c>IsKey</c> nor anything that answers it</b>, which is the
    ///     assertion: both are defaulted on the interface, so an existing implementation compiles
    ///     unchanged and gets the right answer without opting in.
    /// </remarks>
    sealed class Window : IUiWindow {
        public Window(UiSurface surface) => Surface = surface;

        public UiSurface Surface { get; }

        public string Title { get; set; } = "";

        public (float X, float Y, float Width, float Height) Bounds { get; set; }

        public float DpiScale => 1f;

        public bool IsClosed => false;

        public void Focus() { }

        // Accessors that do nothing rather than field-like events: this double raises none of
        // them, and a field-like event nobody raises is a compiler error in this repository.
        public event Action<IUiWindow>? CloseRequested { add { } remove { } }

        public event Action<IUiWindow>? Moved { add { } remove { } }

        public event Action<IUiWindow>? DidBecomeKey { add { } remove { } }

        public void Dispose() { }
    }
}
