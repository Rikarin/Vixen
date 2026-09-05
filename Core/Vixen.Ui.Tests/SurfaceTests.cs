// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Several windows over one document, each with its own size and its own pixel grid.</summary>
public class SurfaceTests {
    static UiDocument Document() {
        var document = new UiDocument(800f, 600f);

        document.Load(
            """
            root { flex-direction: column; }
            ui-surface { flex-direction: column; }
            box { background-color: #ffffff; }
            """
        );

        return document;
    }

    [Fact]
    public void A_new_document_has_one_surface_and_it_is_the_root() {
        using var document = Document();

        var surface = Assert.Single(document.Surfaces);

        Assert.Same(document.Primary, surface);
        Assert.Same(document.Root, surface.Root);

        Assert.True(surface.IsPrimary);
        Assert.Equal(800f, surface.Width, 0.001f);
        Assert.Equal(1f, surface.DpiScale, 0.001f);

        // The document's own viewport is the primary surface's, which is what every caller that
        // predates surfaces meant by it.
        Assert.Equal(surface.Metrics, document.Viewport);
    }

    [Fact]
    public void A_second_surface_is_laid_out_against_its_own_size() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f);
        var box = second.Root.Add("box");

        box.SetStyle("width", "50%");
        box.SetStyle("height", "10px");

        document.Update();

        // ⚠ 200, not 400. A percentage resolves against the containing block, and the containing
        // block here is the *second* window — a surface laid out against the primary's 800 would
        // give 400 and would be the whole bug this exists to prevent.
        Assert.Equal(200f, box.Width, 0.001f);
        Assert.Equal(400f, second.Root.Width, 0.001f);
        Assert.Equal(300f, second.Root.Height, 0.001f);

        // And the primary is untouched by the existence of the second.
        Assert.Equal(800f, document.Root.Width, 0.001f);
    }

    [Fact]
    public void Viewport_units_are_the_surfaces_own() {
        using var document = Document();

        var second = document.CreateSurface(400f, 200f);

        var here = document.Root.Add("box");
        var there = second.Root.Add("box");

        here.SetStyle("width", "50vw");
        there.SetStyle("width", "50vw");

        document.Update();

        Assert.Equal(400f, here.Width, 0.001f);
        Assert.Equal(200f, there.Width, 0.001f);
    }

    [Fact]
    public void A_surfaces_subtree_is_not_drawn_or_hit_tested_by_another() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f);

        var here = document.Root.Add("box");
        var there = second.Root.Add("box");

        here.SetStyle("width", "100px");
        here.SetStyle("height", "100px");

        there.SetStyle("width", "100px");
        there.SetStyle("height", "100px");

        document.Update();
        document.Draw();

        // Two lists, and neither carries the other's box. The two rectangles overlap exactly — both
        // are at the top-left of their own window — so a walk that did not stop at the boundary
        // would put the torn-off panel's contents in the main window's corner.
        Assert.Equal(1, document.Primary.Drawing.Commands.Count(command => command.Kind == DrawCommandKind.Rectangle));
        Assert.Equal(1, second.Drawing.Commands.Count(command => command.Kind == DrawCommandKind.Rectangle));

        Assert.Same(here, document.HitTest(50f, 50f));
        Assert.Same(there, document.HitTest(second, 50f, 50f));
    }

    [Fact]
    public void Each_surface_is_snapped_to_its_own_displays_pixel_grid() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f, 2f);

        var here = document.Root.Add("box");
        var there = second.Root.Add("box");

        // A third of a pixel: at 1× the grid cannot express it and rounds to whole points; at 2× the
        // grid is half a point, so the same declaration lands somewhere else.
        here.SetStyle("width", "10.3px");
        here.SetStyle("height", "4px");

        there.SetStyle("width", "10.3px");
        there.SetStyle("height", "4px");

        document.Update();

        Assert.Equal(10f, here.Width, 0.001f);
        Assert.Equal(10.5f, there.Width, 0.001f);
    }

    [Fact]
    public void Rescaling_a_surface_lays_it_out_again_rather_than_reusing_the_old_grid() {
        using var document = Document();

        var box = document.Root.Add("box");

        box.SetStyle("width", "10.3px");
        box.SetStyle("height", "4px");

        document.Update();
        Assert.Equal(10f, box.Width, 0.001f);

        // ⚠ Nothing the element *declared* changed, so its interned computed style is the same
        // object and the pass's reference test would skip it — which is exactly why `Resize` forgets
        // rather than only marking the document dirty. A window dragged onto a 2× display otherwise
        // keeps the 1× grid for everything that did not otherwise change.
        document.Resize(document.Primary, 800f, 600f, 2f);
        document.Update();

        Assert.Equal(10.5f, box.Width, 0.001f);
    }

    [Fact]
    public void A_panel_moved_between_surfaces_is_the_same_element() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f);
        var panel = document.Root.Add("box");

        panel.SetStyle("width", "100px");
        panel.SetStyle("height", "100px");

        document.Update();

        var moved = 0;
        panel.AddHandler<FocusEvent>((_, _) => moved++);

        // The move a torn-off dock group is: a reparent within one document, which is only possible
        // because a window is a surface rather than a document of its own.
        document.Reparent(panel, second.Root);
        document.Update();

        Assert.Same(second, document.SurfaceOf(panel));
        Assert.Same(panel, document.HitTest(second, 50f, 50f));

        // The main window is left with its own root and nothing in it — the panel is not there any
        // more, and the second surface's root, which does overlap that point, is not hit-tested from
        // here at all.
        Assert.Same(document.Root, document.HitTest(50f, 50f));

        // Same instance, same handlers. A panel rebuilt into a second document would have neither,
        // and would have lost the user's scroll position and half-typed text with them.
        panel.Raise(new FocusEvent { Gained = true });
        Assert.Equal(1, moved);
    }

    [Fact]
    public void Removing_a_surface_takes_what_is_left_in_it_and_refuses_the_primary() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f);
        var left = second.Root.Add("box");

        Assert.False(document.RemoveSurface(document.Primary));

        var removed = 0;
        document.SurfaceRemoved += (_, _) => removed++;

        Assert.True(document.RemoveSurface(second));
        Assert.Equal(1, removed);

        Assert.Single(document.Surfaces);
        Assert.True(second.IsRemoved);
        Assert.True(left.IsRemoved);

        // Removing it twice is not a second removal, and not an exception either.
        Assert.False(document.RemoveSurface(second));
    }

    [Fact]
    public void A_surface_inherits_the_documents_theme_and_root_font_size() {
        using var document = new UiDocument(800f, 600f);

        document.Load("root { color: #ff0000; font-size: 20px; } box { width: 2em; height: 4px; }");

        var second = document.CreateSurface(400f, 300f);
        var box = second.Root.Add("box");

        document.Update();

        // ⚠ One style tree across every window, which is the reason a surface root is a child of the
        // document root rather than a root of its own. An `em` in a torn-off window is the same
        // number it is in the main one, and a rule written once styles both.
        Assert.Equal(40f, box.Width, 0.001f);
    }

    [Fact]
    public void A_command_runs_against_the_key_surface_and_not_the_primary_one() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f);

        var ran = "";
        document.Root.AddCommandHandler("edit.copy", () => ran = "main");
        second.Root.AddCommandHandler("edit.copy", () => ran = "inspector");

        // Nothing focused and no key surface: the walk starts at the document root, which is what a
        // one-window application means and what every caller before surfaces existed meant.
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("main", ran);

        // ⚠ The same verb, the same document, nothing focused, and a different answer — because the
        // user is in the other window. A surface root's parents still end at the document root, so
        // this adds the torn-off window's links in front of the walk rather than replacing it.
        document.KeySurface = second;

        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("inspector", ran);
    }

    [Fact]
    public void A_keystroke_with_nothing_focused_lands_in_the_key_surface() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f);
        document.KeySurface = second;

        var args = new KeyEvent { Key = Input.InputKey.F5, Action = KeyAction.Pressed };
        Assert.Same(second.Root, document.Dispatch(args));

        // ⚠ **A focus in the OTHER window does not pull the keystroke back, and it used to.** The
        // key surface answers "where is the user" and the focus answers "what is she typing into",
        // and the second question is asked of the window the first named — so a caret left behind in
        // the main window is not what the user is typing into while she is in the inspector.
        var field = document.Root.Add("div");
        field.Focusable = true;

        Assert.True(document.Focus(field));
        Assert.Same(second.Root, document.Dispatch(new KeyEvent { Key = Input.InputKey.F5, Action = KeyAction.Pressed }));

        // The focus in the key window does outrank its root, which is the half that was always true.
        var probe = second.Root.Add("div");
        probe.Focusable = true;

        Assert.True(document.Focus(probe));
        Assert.Same(probe, document.Dispatch(new KeyEvent { Key = Input.InputKey.F5, Action = KeyAction.Pressed }));
        Assert.Same(probe, document.Focused);

        // Switching back finds the main window's caret where it was left.
        document.KeySurface = document.Primary;

        Assert.Same(field, document.Focused);
        Assert.Same(field, document.Dispatch(new KeyEvent { Key = Input.InputKey.F5, Action = KeyAction.Pressed }));
    }

    [Fact]
    public void Closing_the_key_window_gives_the_answer_back_to_the_primary() {
        using var document = Document();

        var second = document.CreateSurface(400f, 300f);
        document.KeySurface = second;

        Assert.True(document.RemoveSurface(second));

        // ⚠ Not tidiness. A removed surface's root is out of the document, and `UiElement.Document`
        // throws on one of those — so a key surface left pointing at a closed window turns the next
        // keystroke into an exception rather than a misrouted key.
        Assert.Null(document.KeySurface);
        Assert.Same(document.Root, document.Dispatch(new KeyEvent { Key = Input.InputKey.F5, Action = KeyAction.Pressed }));
    }

    [Fact]
    public void A_surface_from_another_document_is_refused() {
        using var document = Document();
        using var other = Document();

        Assert.Throws<ArgumentException>(() => document.KeySurface = other.Primary);
    }
}
