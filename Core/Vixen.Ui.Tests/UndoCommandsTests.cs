// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Tests;

/// <summary>Undo as a command rather than as a chord: who answers <c>edit.undo</c>, and from where.</summary>
/// <remarks>
///     ⚠ <b>Every test here resolves through <see cref="CommandRoute" /> with a focus somewhere
///     deliberate.</b> Calling <c>manager.Undo()</c> and reading the value back would pass with
///     <see cref="UndoCommands.Install" /> deleted — the thing under test is that a menu item spelling
///     <c>edit.undo</c> <i>reaches</i> a manager from wherever the caret happens to be, which is the
///     half that did not exist while undo was a keystroke the text field consumed.
/// </remarks>
public class UndoCommandsTests {
    static UiElement View(UiElement parent) {
        var element = parent.Add("div");
        element.Focusable = true;

        return element;
    }

    /// <summary>An edit already applied, recorded so that it can be taken back.</summary>
    static void Record(UndoManager manager, List<string> log, string name) =>
        manager.Register(name, () => log.Add($"-{name}"), () => log.Add($"+{name}"));

    [Fact]
    public void A_focused_leaf_that_handles_nothing_reaches_the_installed_manager() {
        using var document = new UiDocument(100f, 100f);

        var manager = new UndoManager();
        document.UndoManager = manager;
        UndoCommands.Install(document.Root);

        var log = new List<string>();
        Record(manager, log, "typing");

        // The focus is on a leaf with no handler of its own — a text field, in an application. The
        // walk has to climb out of it to find the verb.
        var field = View(document.Root.Add("div"));
        document.Focus(field);

        Assert.True(CommandRoute.CanExecute(document, UndoCommands.Undo));
        Assert.True(CommandRoute.Execute(document, UndoCommands.Undo));
        Assert.Equal(["-typing"], log);

        // And Redo is live in the same breath, which is the pair being one stack rather than two.
        Assert.True(CommandRoute.CanExecute(document, UndoCommands.Redo));
        Assert.True(CommandRoute.Execute(document, UndoCommands.Redo));
        Assert.Equal(["-typing", "+typing"], log);
    }

    [Fact]
    public void With_nothing_recorded_both_verbs_are_greyed_rather_than_running() {
        using var document = new UiDocument(100f, 100f);

        document.UndoManager = new UndoManager();
        UndoCommands.Install(document.Root);

        // ⚠ Resolved but refused, not unresolved: the item exists and is grey, which is the
        // difference between "this application has no Undo" and "there is nothing to take back".
        Assert.NotNull(CommandRoute.Resolve(document, UndoCommands.Undo));
        Assert.False(CommandRoute.CanExecute(document, UndoCommands.Undo));
        Assert.False(CommandRoute.Execute(document, UndoCommands.Undo));

        Assert.NotNull(CommandRoute.Resolve(document, UndoCommands.Redo));
        Assert.False(CommandRoute.CanExecute(document, UndoCommands.Redo));
    }

    [Fact]
    public void With_no_manager_anywhere_the_verb_is_greyed_and_the_route_still_answers() {
        using var document = new UiDocument(100f, 100f);

        UndoCommands.Install(document.Root);

        // ⚠ Resolved, and that is the half that distinguishes this from `Install` never having run:
        // an application with no manager still has the item, greyed.
        Assert.NotNull(CommandRoute.Resolve(document, UndoCommands.Undo));
        Assert.Null(document.UndoManager);
        Assert.False(CommandRoute.CanExecute(document, UndoCommands.Undo));
        Assert.False(CommandRoute.Execute(document, UndoCommands.Undo));
    }

    [Fact]
    public void The_nearer_panels_stack_wins_and_the_documents_is_untouched() {
        using var document = new UiDocument(100f, 100f);

        var application = new UndoManager();
        document.UndoManager = application;
        UndoCommands.Install(document.Root);

        var applicationLog = new List<string>();
        Record(application, applicationLog, "shell");

        var panel = document.Root.Add("div");
        var panelStack = new UndoManager();
        panel.UndoManager = panelStack;
        UndoCommands.Install(panel);

        var panelLog = new List<string>();
        Record(panelStack, panelLog, "panel");

        var inside = View(panel);
        document.Focus(inside);

        Assert.Same(panel, CommandRoute.Resolve(document, UndoCommands.Undo)!.Value.Element);
        Assert.True(CommandRoute.Execute(document, UndoCommands.Undo));
        Assert.Equal(["-panel"], panelLog);
        Assert.Empty(applicationLog);

        // Move the focus outside the panel and the same id means the shell's stack. Nothing was
        // pushed or popped to make that happen.
        document.Focus(View(document.Root));

        Assert.Same(document.Root, CommandRoute.Resolve(document, UndoCommands.Undo)!.Value.Element);
        Assert.True(CommandRoute.Execute(document, UndoCommands.Undo));
        Assert.Equal(["-shell"], applicationLog);
        Assert.Equal(["-panel"], panelLog);
    }

    [Fact]
    public void The_manager_is_read_on_every_ask_so_a_replacement_takes_effect() {
        using var document = new UiDocument(100f, 100f);

        document.UndoManager = new UndoManager();
        UndoCommands.Install(document.Root);

        var later = new UndoManager();
        var log = new List<string>();
        Record(later, log, "later");

        // Installed against a stack that is then thrown away, which is what an application replacing
        // the host's default does.
        document.UndoManager = later;

        Assert.True(CommandRoute.Execute(document, UndoCommands.Undo));
        Assert.Equal(["-later"], log);
    }

    [Fact]
    public void Running_one_of_the_pair_invalidates_the_other() {
        using var document = new UiDocument(100f, 100f);

        var manager = new UndoManager();
        document.UndoManager = manager;
        UndoCommands.Install(document.Root);

        Record(manager, [], "typing");

        var raises = 0;
        document.CommandsInvalidated += _ => raises++;

        var clock = TimeSpan.Zero;

        void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            document.Tick(clock);
            document.Update();
        }

        Frame();
        var before = raises;

        Assert.True(CommandRoute.Execute(document, UndoCommands.Undo));
        Frame();

        // ⚠ Undo greyed itself and un-greyed Redo in one step, and command state is pulled rather
        // than observed — so an item that was already on screen keeps its enablement until this
        // raise reaches it.
        Assert.Equal(before + 1, raises);

        // The other half, which is the assertion that could not otherwise fail: a refused run has
        // moved nothing and must not raise.
        var refused = raises;
        Assert.False(CommandRoute.Execute(document, UndoCommands.Undo));
        Frame();

        Assert.Equal(refused, raises);
    }
}
