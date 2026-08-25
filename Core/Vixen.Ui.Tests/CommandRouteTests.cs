// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>The responder chain: which element answers a command id, and whether it can.</summary>
/// <remarks>
///     ⚠ <b>Every test here builds a real tree and moves the real focus.</b> A test that newed up a
///     <see cref="CommandHandler" /> and read a property back would pass with the walk deleted,
///     which is the failure this file exists to make impossible: the thing under test is
///     <i>resolution</i>, so nothing is asserted that does not depend on where the focus is.
/// </remarks>
public class CommandRouteTests {
    /// <summary>A focusable element under a parent, which is what a view is for these purposes.</summary>
    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;
        return element;
    }

    [Fact]
    public void The_focused_leaf_decides_which_of_two_handlers_runs() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";

        var outliner = View(document.Root);
        var browser = View(document.Root);

        outliner.AddCommandHandler("edit.copy", () => ran = "outliner");
        browser.AddCommandHandler("edit.copy", () => ran = "browser");

        document.Focus(outliner);
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("outliner", ran);

        document.Focus(browser);
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("browser", ran);

        // And neither view knows the other exists: the only thing that changed between the two runs
        // is where the focus is.
        Assert.Same(outliner, CommandRoute.Resolve(document, "edit.copy")!.Value.Element.Parent!.Children[0]);
    }

    [Fact]
    public void A_leaf_with_no_handler_reaches_its_ancestors() {
        using var document = new UiDocument(100f, 100f);

        var ran = 0;

        var panel = document.Root.Add("div");
        var row = panel.Add("div");
        var leaf = View(row);

        panel.AddCommandHandler("edit.copy", () => ran++);
        document.Focus(leaf);

        var handler = CommandRoute.Resolve(document, "edit.copy");

        Assert.NotNull(handler);
        Assert.Same(panel, handler!.Value.Element);
        Assert.True(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void The_nearer_of_two_responders_wins_and_the_further_one_is_never_asked() {
        using var document = new UiDocument(100f, 100f);

        var nearAsked = 0;
        var farAsked = 0;
        var ran = "";

        var panel = document.Root.Add("div");
        var leaf = View(panel);

        panel.AddCommandHandler("edit.copy", () => ran = "panel", () => { farAsked++; return true; });
        leaf.AddCommandHandler("edit.copy", () => ran = "leaf", () => { nearAsked++; return true; });

        document.Focus(leaf);

        Assert.True(CommandRoute.CanExecute(document, "edit.copy"));
        Assert.True(CommandRoute.Execute(document, "edit.copy"));

        Assert.Equal("leaf", ran);
        Assert.Equal(0, farAsked);
        Assert.True(nearAsked > 0);
    }

    [Fact]
    public void The_nearer_responder_saying_no_does_not_fall_through_to_the_further_one() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";

        var panel = document.Root.Add("div");
        var leaf = View(panel);

        panel.AddCommandHandler("edit.copy", () => ran = "panel");
        leaf.AddCommandHandler("edit.copy", () => ran = "leaf", () => false);

        document.Focus(leaf);

        // ⚠ The rule that keeps "which handler" from depending on how many are listening. If this
        // fell through, adding a panel above a view would silently change what its disabled Copy did.
        Assert.False(CommandRoute.CanExecute(document, "edit.copy"));
        Assert.False(CommandRoute.Execute(document, "edit.copy"));
        Assert.Equal("", ran);
        Assert.Same(leaf, CommandRoute.Resolve(document, "edit.copy")!.Value.Element);
    }

    [Fact]
    public void Nobody_responds_so_it_is_not_executable() {
        using var document = new UiDocument(100f, 100f);

        var leaf = View(document.Root);
        document.Focus(leaf);

        Assert.Null(CommandRoute.Resolve(document, "edit.copy"));
        Assert.False(CommandRoute.CanExecute(document, "edit.copy"));
        Assert.False(CommandRoute.Execute(document, "edit.copy"));
    }

    [Fact]
    public void A_handler_on_the_root_answers_wherever_the_focus_is_including_nowhere() {
        using var document = new UiDocument(100f, 100f);

        var ran = 0;

        // This is the shape a command with a registration-time implementation has, and the reason
        // nothing changes for the editor's existing commands: the document itself responds.
        document.Root.AddCommandHandler("file.save", () => ran++);

        Assert.Null(document.Focused);
        Assert.True(CommandRoute.CanExecute(document, "file.save"));
        Assert.True(CommandRoute.Execute(document, "file.save"));

        var leaf = View(document.Root.Add("div"));
        document.Focus(leaf);

        Assert.True(CommandRoute.Execute(document, "file.save"));
        Assert.Equal(2, ran);
        Assert.Same(document.Root, CommandRoute.Resolve(document, "file.save")!.Value.Element);
    }

    [Fact]
    public void Enablement_follows_a_selection_with_no_code_in_the_caller() {
        using var document = new UiDocument(100f, 100f);

        var selection = 0;

        var view = View(document.Root);
        view.AddCommandHandler("edit.copy", () => { }, () => selection > 0);

        document.Focus(view);
        Assert.False(CommandRoute.CanExecute(document, "edit.copy"));

        selection = 3;
        Assert.True(CommandRoute.CanExecute(document, "edit.copy"));

        selection = 0;
        Assert.False(CommandRoute.CanExecute(document, "edit.copy"));
    }

    [Fact]
    public void A_scope_is_inherited_down_the_tree_and_a_child_may_narrow_it() {
        using var document = new UiDocument(100f, 100f);

        var panel = document.Root.Add("div");
        panel.CommandScope = "outliner";

        var row = panel.Add("div");
        var leaf = View(row);

        var inspector = document.Root.Add("div");
        inspector.CommandScope = "inspector";
        var field = View(inspector);

        document.Focus(leaf);
        Assert.Equal("outliner", CommandRoute.ScopeOf(document));

        document.Focus(field);
        Assert.Equal("inspector", CommandRoute.ScopeOf(document));

        // Narrowing: an element inside a scope declares its own and everything below it follows.
        row.CommandScope = "outliner.rename";
        document.Focus(leaf);
        Assert.Equal("outliner.rename", CommandRoute.ScopeOf(document));

        // And only this element declares it; the panel above still says what it said.
        Assert.Null(leaf.CommandScope);
        Assert.Equal("outliner.rename", leaf.EffectiveCommandScope);
        Assert.Equal("outliner", panel.EffectiveCommandScope);
    }

    [Fact]
    public void With_nothing_focused_the_scope_is_the_documents_own() {
        using var document = new UiDocument(100f, 100f);

        var panel = document.Root.Add("div");
        panel.CommandScope = "outliner";

        Assert.Null(document.Focused);
        Assert.Null(CommandRoute.ScopeOf(document));

        document.Root.CommandScope = "shell";
        Assert.Equal("shell", CommandRoute.ScopeOf(document));

        // And focusing into the panel still narrows to the panel's.
        var leaf = View(panel);
        document.Focus(leaf);
        Assert.Equal("outliner", CommandRoute.ScopeOf(document));
    }

    [Fact]
    public void Blurring_puts_the_route_back_on_the_document() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";

        document.Root.AddCommandHandler("edit.copy", () => ran = "root");

        var view = View(document.Root);
        view.AddCommandHandler("edit.copy", () => ran = "view");

        document.Focus(view);
        CommandRoute.Execute(document, "edit.copy");
        Assert.Equal("view", ran);

        document.Focus(null);
        CommandRoute.Execute(document, "edit.copy");
        Assert.Equal("root", ran);
    }

    [Fact]
    public void Removing_the_focused_view_takes_its_handler_off_the_route() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";

        document.Root.AddCommandHandler("edit.copy", () => ran = "root");

        var view = View(document.Root);
        view.AddCommandHandler("edit.copy", () => ran = "view");

        document.Focus(view);
        Assert.Same(view, CommandRoute.Resolve(document, "edit.copy")!.Value.Element);

        // `UiDocument.Release` clears the focus when the focused subtree goes, so the route falls
        // back to the document rather than resolving through a retired element.
        view.Remove();

        Assert.Null(document.Focused);
        Assert.Same(document.Root, CommandRoute.Resolve(document, "edit.copy")!.Value.Element);

        CommandRoute.Execute(document, "edit.copy");
        Assert.Equal("root", ran);
    }

    [Fact]
    public void A_handler_can_be_taken_off_and_the_route_then_reaches_past_it() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";

        document.Root.AddCommandHandler("edit.copy", () => ran = "root");

        var view = View(document.Root);
        view.AddCommandHandler("edit.copy", () => ran = "view");
        document.Focus(view);

        Assert.True(view.RemoveCommandHandler("edit.copy"));
        Assert.False(view.RemoveCommandHandler("edit.copy"));

        CommandRoute.Execute(document, "edit.copy");
        Assert.Equal("root", ran);
    }

    [Fact]
    public void One_element_declaring_the_same_id_twice_says_so() {
        using var document = new UiDocument(100f, 100f);

        var view = View(document.Root);
        view.AddCommandHandler("edit.copy", () => { });

        var thrown = Assert.Throws<ArgumentException>(() => view.AddCommandHandler("edit.copy", () => { }));
        Assert.Contains("edit.copy", thrown.Message, StringComparison.Ordinal);

        // Two *different* elements declaring it is the whole point and is not a collision.
        var other = View(document.Root);
        other.AddCommandHandler("edit.copy", () => { });

        Assert.Equal(["edit.copy"], view.CommandHandlerIds);
    }

    [Fact]
    public void An_element_that_takes_no_part_carries_nothing() {
        using var document = new UiDocument(100f, 100f);

        var plain = document.Root.Add("div");

        Assert.Null(plain.CommandScope);
        Assert.Empty(plain.CommandHandlerIds);
        Assert.False(plain.TryGetCommandHandler("edit.copy", out _));

        // Writing null is not a registration, so the lazy store stays unallocated for the
        // overwhelming majority of a tree.
        plain.CommandScope = null;
        Assert.Null(plain.EffectiveCommandScope);
    }

    [Fact]
    public void The_route_crosses_a_surface_boundary_up_to_the_document() {
        using var document = new UiDocument(100f, 100f);

        var ran = "";
        document.Root.AddCommandHandler("file.save", () => ran = "document");

        var surface = document.CreateSurface(64f, 64f);
        var leaf = View(surface.Root);

        document.Focus(leaf);

        // A palette dragged into its own window is still in the same document, and its ancestors
        // still end at the root — which is what makes one Ctrl+S serve every window.
        Assert.True(CommandRoute.Execute(document, "file.save"));
        Assert.Equal("document", ran);
        Assert.Same(document.Root, CommandRoute.Resolve(document, "file.save")!.Value.Element);
    }
}
