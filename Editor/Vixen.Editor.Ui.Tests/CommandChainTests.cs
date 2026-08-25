// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>The editor at the end of <see cref="CommandRoute" />'s chain.</summary>
/// <remarks>
///     <para>
///         <b>The consumer that makes the application level real.</b> An extension point nothing
///         registers into is this repository's commonest defect, and the level added past the root
///         would have been one — so <see cref="EditorShell" /> installs its
///         <see cref="CommandRegistry" /> as the document's
///         <see cref="UiDocument.ApplicationCommandResponder" />, and these tests are what says it
///         is wired rather than declared.
///     </para>
///     <para>
///         ⚠ <b>Nothing here binds a control.</b> Moving the editor's menus and toolbars onto
///         <c>ButtonBase.Command</c> is doc 45's step 4 and is not this. What is asserted is only
///         that the chain reaches the registry, keeps its rules on the way through, and lets go of
///         the document when the shell is disposed.
///     </para>
/// </remarks>
public class CommandChainTests {
    static StringId Title(string text) => new("test." + text, text);

    [Fact]
    public void An_editor_command_answers_the_route_once_the_tree_is_silent() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = 0;
        shell.Commands.Add("test.verb", Title("Verb"), () => ran++);

        // ⚠ Before the fix this was null: `Resolve` stopped at the root, and every one of the
        // editor's commands was unreachable from a `Vixen.Ui` surface.
        var handler = CommandRoute.Resolve(shell.Document, "test.verb");

        Assert.NotNull(handler);
        Assert.Null(handler!.Value.Element);
        Assert.Same(shell.Commands, handler.Value.Responder);

        Assert.True(CommandRoute.Execute(shell.Document, "test.verb"));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void An_element_in_the_tree_still_outranks_the_application() {
        using var shell = new EditorShell(1280f, 800f);

        var ran = "";
        shell.Commands.Add("edit.copy", Title("Copy"), () => ran = "application");

        var view = shell.Document.Root.Add("div");
        view.Focusable = true;
        view.AddCommandHandler("edit.copy", () => ran = "view");

        shell.Document.Focus(view);
        Assert.True(CommandRoute.Execute(shell.Document, "edit.copy"));
        Assert.Equal("view", ran);

        // And the editor's is what is left when the focus goes away, which is the whole point of a
        // level at the end rather than a special case in each surface.
        shell.Document.Focus(null);
        Assert.True(CommandRoute.Execute(shell.Document, "edit.copy"));
        Assert.Equal("application", ran);
    }

    [Fact]
    public void The_route_goes_through_the_registry_s_own_gate_rather_than_around_it() {
        using var shell = new EditorShell(1280f, 800f);

        var enabled = false;
        var ran = 0;
        var announced = 0;

        shell.Commands.Add(new EditorCommand("test.verb", Title("Verb"), () => ran++) { Enablement = () => enabled });
        shell.Commands.Executed += _ => announced++;

        // ⚠ It answers while it is disabled rather than declining the id. Declining would let the id
        // fall out of the chain entirely, and there is nothing after the application to catch it —
        // "this verb is mine and I cannot do it right now" is a greyed item, not an absent one.
        Assert.NotNull(CommandRoute.Resolve(shell.Document, "test.verb"));
        Assert.False(CommandRoute.CanExecute(shell.Document, "test.verb"));
        Assert.False(CommandRoute.Execute(shell.Document, "test.verb"));
        Assert.Equal(0, ran);

        enabled = true;
        Assert.True(CommandRoute.Execute(shell.Document, "test.verb"));
        Assert.Equal(1, ran);

        // ⚠ And it lands on the same single `Execute` the palette, the menu and the keymap land on.
        // A fourth entry point that called `EditorCommand.Run` directly would run the command behind
        // `Executed`'s back, which is where the "recently used" list and the bug-report log line
        // both hang.
        Assert.Equal(1, announced);
    }

    [Fact]
    public void A_command_out_of_scope_is_answered_and_refused_rather_than_unhandled() {
        using var shell = new EditorShell(1280f, 800f);

        shell.Commands.Add(new EditorCommand("test.scoped", Title("Scoped"), () => { }) { Context = "outliner" });

        shell.Context = "console";
        Assert.NotNull(CommandRoute.Resolve(shell.Document, "test.scoped"));
        Assert.False(CommandRoute.CanExecute(shell.Document, "test.scoped"));

        shell.Context = "outliner";
        Assert.True(CommandRoute.CanExecute(shell.Document, "test.scoped"));
    }

    [Fact]
    public void Removing_a_command_takes_it_out_of_the_chain_too() {
        using var shell = new EditorShell(1280f, 800f);

        shell.Commands.Add("test.verb", Title("Verb"), () => { });
        Assert.NotNull(CommandRoute.Resolve(shell.Document, "test.verb"));

        // ⚠ The unload case. A handler left behind for a command that is gone is a menu line that
        // runs a plugin which is no longer there, and the registry keeps two tables that have to go
        // out together.
        Assert.True(shell.Commands.Remove("test.verb"));
        Assert.Null(CommandRoute.Resolve(shell.Document, "test.verb"));
    }

    [Fact]
    public void Registering_a_command_invalidates_the_shell_s_command_surfaces() {
        using var shell = new EditorShell(1280f, 800f);

        var raised = 0;
        var clock = TimeSpan.FromSeconds(1);

        shell.Document.CommandsInvalidated += _ => raised++;

        void Frame() {
            clock += TimeSpan.FromMilliseconds(16);
            shell.Tick(clock, TimeSpan.FromMilliseconds(16));
        }

        Frame();
        raised = 0;

        // ⚠ Forty registrations, one raise. A plugin load is the case this exists for: without the
        // coalescing every visible command would be re-asked forty times for one answer.
        for (var i = 0; i < 40; i++) {
            shell.Commands.Add($"test.verb-{i}", Title("Verb"), () => { });
        }

        Frame();
        Assert.Equal(1, raised);

        Frame();
        Assert.Equal(1, raised);
    }

    [Fact]
    public void A_disposed_shell_lets_go_of_the_registry_and_the_registry_of_it() {
        var shell = new EditorShell(1280f, 800f);
        var document = shell.Document;

        shell.Dispose();

        // The document's direction: it has let go of the registry, so the editor's commands — and
        // the plugin assemblies behind their closures — are not held by a window that has closed.
        Assert.Null(document.ApplicationCommandResponder);

        // And the registry works afterwards, so the teardown took the subscription off rather than
        // breaking the table.
        shell.Commands.Add("test.after-dispose", Title("Verb"), () => { });
        Assert.True(shell.Commands.TryGetCommandHandler("test.after-dispose", out _));
    }

    [Fact]
    public void A_kept_registry_no_longer_reaches_the_shell_that_made_it() {
        // ⚠ What the unsubscription is actually worth, stated without overclaiming. `EditorShell` is
        // the only place a `CommandRegistry` is made and it owns the one it makes, so the
        // subscription is a cycle inside one ownership unit and the pair is collected together —
        // this is not the ~95 MB-a-reload shape, and a test that said it was would be asserting a
        // leak that is not there. What it is worth is this: a caller that keeps the registry after
        // disposing the shell must not find the old shell still listening on it.
        var shell = new EditorShell(1280f, 800f);
        var registry = shell.Commands;

        shell.Dispose();

        var raised = 0;
        registry.Changed += _ => raised++;

        registry.Add("test.after-dispose", Title("Verb"), () => { });

        // One subscriber left, and it is the one this test added. Had the shell's survived, the
        // registry would still be driving a disposed document's invalidation every time a plugin
        // registered anything.
        Assert.Equal(1, raised);
        Assert.Equal(1, registry.ChangedSubscriberCount);
    }
}
