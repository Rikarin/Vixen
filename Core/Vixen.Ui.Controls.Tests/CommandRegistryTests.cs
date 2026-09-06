// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Controls.Tests;

/// <summary>The command table, exercised from an application that is not the editor.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Which assembly this file is in is half of what it asserts.</b>
///         <see cref="CommandRegistry" /> and <see cref="EditorCommand" /> were
///         <c>Vixen.Editor.Ui</c> types until doc 49 § 4.4's move, and a test project for the
///         controls library cannot reference the editor — so a regression that put them back would
///         not fail these assertions, it would fail to compile them, which is the stronger signal of
///         the two. <see cref="The_table_is_in_the_controls_library" /> says the same thing in a
///         form that survives somebody adding a reference.
///     </para>
///     <para>
///         <b>The symptom the move exists to end</b> is <c>MenuItem.ShowShortcut</c>: the controls
///         library could <i>draw</i> "⌘S" beside a menu item while every part of the machinery
///         behind it lived in the editor, so a non-editor application rendered a shortcut that
///         nothing dispatched. Two of the five files that made that true are now here.
///     </para>
/// </remarks>
public class CommandRegistryTests {
    static EditorCommand Command(string id, Action run) => new(id, new StringId(id, id), run);

    /// <summary>The registry ships in the same assembly as the controls that show its commands.</summary>
    /// <remarks>
    ///     A structural claim rather than a behavioural one, and deliberately so: every other test
    ///     here would pass just as well against a copy of these types in the editor.
    /// </remarks>
    [Fact]
    public void The_table_is_in_the_controls_library() {
        Assert.Same(typeof(Button).Assembly, typeof(CommandRegistry).Assembly);
        Assert.Same(typeof(Button).Assembly, typeof(EditorCommand).Assembly);
    }

    /// <summary>A table installed on a document is the last link of the route's chain.</summary>
    [Fact]
    public void A_table_installed_on_a_document_answers_the_route() {
        using var fixture = new ControlFixture();
        var ran = 0;

        var commands = new CommandRegistry();
        commands.Add(Command("app.file.save", () => ran++));
        fixture.Document.ApplicationCommandResponder = commands;

        Assert.NotNull(CommandRoute.Resolve(fixture.Document, "app.file.save"));
        Assert.True(CommandRoute.CanExecute(fixture.Document, "app.file.save"));
        Assert.True(CommandRoute.Execute(fixture.Document, "app.file.save"));
        Assert.Equal(1, ran);
    }

    /// <summary>An id nothing registered resolves to nothing rather than to a handler that does nothing.</summary>
    [Fact]
    public void An_unregistered_id_resolves_to_nothing() {
        using var fixture = new ControlFixture();

        fixture.Document.ApplicationCommandResponder = new CommandRegistry();

        Assert.Null(CommandRoute.Resolve(fixture.Document, "app.file.save"));
        Assert.False(CommandRoute.Execute(fixture.Document, "app.file.save"));
    }

    /// <summary>A command whose predicate says no is greyed, not absent.</summary>
    /// <remarks>
    ///     ⚠ The distinction the whole chain rests on: returning <c>false</c> from
    ///     <see cref="CommandRegistry.TryGetCommandHandler" /> would let the id fall out of the walk
    ///     entirely, and there is nothing after the application responder to catch it — so a
    ///     temporarily impossible verb answers with a predicate that refuses.
    /// </remarks>
    [Fact]
    public void A_disabled_command_answers_and_refuses() {
        using var fixture = new ControlFixture();
        var ran = 0;
        var allowed = false;

        var commands = new CommandRegistry();
        commands.Add(new EditorCommand("app.edit.undo", new StringId("app.edit.undo", "Undo"), () => ran++) {
            Enablement = () => allowed
        });

        fixture.Document.ApplicationCommandResponder = commands;

        Assert.NotNull(CommandRoute.Resolve(fixture.Document, "app.edit.undo"));
        Assert.False(CommandRoute.CanExecute(fixture.Document, "app.edit.undo"));
        Assert.False(CommandRoute.Execute(fixture.Document, "app.edit.undo"));
        Assert.Equal(0, ran);

        allowed = true;

        Assert.True(CommandRoute.CanExecute(fixture.Document, "app.edit.undo"));
        Assert.True(CommandRoute.Execute(fixture.Document, "app.edit.undo"));
        Assert.Equal(1, ran);
    }

    /// <summary>A command belonging to a context nobody is in refuses the same way.</summary>
    [Fact]
    public void A_command_out_of_scope_refuses() {
        using var fixture = new ControlFixture();
        var ran = 0;
        string? focused = null;

        var commands = new CommandRegistry { FocusedContext = () => focused };
        commands.Add(new EditorCommand("app.item.delete", new StringId("app.item.delete", "Delete"), () => ran++) {
            Context = "outliner"
        });

        fixture.Document.ApplicationCommandResponder = commands;

        Assert.False(commands.CanExecute("app.item.delete"));
        Assert.False(CommandRoute.Execute(fixture.Document, "app.item.delete"));
        Assert.Equal(0, ran);

        focused = "outliner";

        Assert.True(commands.CanExecute("app.item.delete"));
        Assert.True(CommandRoute.Execute(fixture.Document, "app.item.delete"));
        Assert.Equal(1, ran);
    }

    /// <summary>Registering an id twice throws rather than replacing or ignoring.</summary>
    [Fact]
    public void Registering_an_id_twice_throws() {
        var commands = new CommandRegistry();
        commands.Add(Command("app.file.save", () => { }));

        Assert.Throws<ArgumentException>(() => commands.Add(Command("app.file.save", () => { })));
    }
}
