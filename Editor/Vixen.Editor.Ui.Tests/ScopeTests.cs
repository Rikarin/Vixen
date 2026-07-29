// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Xunit;

namespace Vixen.Editor.Ui.Tests;

/// <summary>Two panels, one keystroke, and which command it means.</summary>
/// <remarks>
///     ⚠ <b>The case this exists for is Delete.</b> The outliner deletes an entity, the content
///     browser deletes an asset, both want the key, and an editor that decided between them from an
///     enablement predicate gets it wrong exactly when both panels have a selection — which after
///     clicking an asset and then a row is most of the time.
/// </remarks>
public class ScopeTests : IDisposable {
    readonly UiDocument document = new(800f, 600f);
    readonly CommandRegistry commands = new();
    readonly KeyMap keys = new();
    readonly CommandDispatcher dispatcher;

    string? context;

    public ScopeTests() {
        ControlTheme.Install(document);

        keys.ContextOf = id => commands.TryGet(id, out var command) ? command.Context : null;
        commands.FocusedContext = () => context;

        dispatcher = new CommandDispatcher(commands, keys);
    }

    public void Dispose() {
        document.Dispose();
        GC.SuppressFinalize(this);
    }

    static StringId Title(string text) => new("test." + text, text);

    EditorCommand Add(string id, string? scope, Action run) =>
        commands.Add(new EditorCommand(id, Title(id), run) { Context = scope });

    [Fact]
    public void A_command_with_no_context_is_in_scope_wherever_the_user_is() {
        var command = Add("file.save", null, () => { });

        Assert.True(commands.IsInScope(command));

        context = "project";
        Assert.True(commands.IsInScope(command));
    }

    [Fact]
    public void A_scoped_command_only_runs_in_its_own_context() {
        var ran = 0;
        Add("scene.delete", "scene", () => ran++);

        context = "project";

        Assert.False(commands.CanExecute("scene.delete"));
        Assert.False(commands.Execute("scene.delete"));
        Assert.Equal(0, ran);

        context = "scene";

        Assert.True(commands.CanExecute("scene.delete"));
        Assert.True(commands.Execute("scene.delete"));
        Assert.Equal(1, ran);
    }

    [Fact]
    public void Two_contexts_may_share_a_chord_and_the_focused_one_gets_it() {
        var entities = 0;
        var assets = 0;

        Add("scene.delete", "scene", () => entities++);
        Add("assets.delete", "project", () => assets++);

        // ⚠ Neither is a conflict, which is the whole point. A keymap that refused the second would
        // be one where the content browser's Delete has to be some other key.
        Assert.Equal(BindResult.Bound, keys.SetDefault("scene.delete", Delete));
        Assert.Equal(BindResult.Bound, keys.SetDefault("assets.delete", Delete));

        context = "scene";
        Press(InputKey.Delete);

        Assert.Equal(1, entities);
        Assert.Equal(0, assets);

        context = "project";
        Press(InputKey.Delete);

        Assert.Equal(1, entities);
        Assert.Equal(1, assets);
    }

    [Fact]
    public void Two_commands_in_one_context_still_conflict() {
        Add("scene.delete", "scene", () => { });
        Add("scene.destroy", "scene", () => { });

        Assert.Equal(BindResult.Bound, keys.SetDefault("scene.delete", Delete));
        Assert.Equal(BindResult.Conflict, keys.SetDefault("scene.destroy", Delete));
    }

    [Fact]
    public void A_contexts_binding_shadows_the_global_one_while_it_has_the_focus() {
        var global = 0;
        var scoped = 0;

        Add("edit.delete", null, () => global++);
        Add("graph.delete-node", "graph", () => scoped++);

        keys.SetDefault("edit.delete", Delete);
        keys.SetDefault("graph.delete-node", Delete);

        context = "scene";
        Press(InputKey.Delete);

        // Nothing claims Delete in "scene", so the global binding answers — which is what stops
        // every panel having to re-declare Ctrl+S.
        Assert.Equal(1, global);
        Assert.Equal(0, scoped);

        context = "graph";
        Press(InputKey.Delete);

        Assert.Equal(1, global);
        Assert.Equal(1, scoped);
    }

    [Fact]
    public void An_out_of_scope_chord_is_left_alone_rather_than_reported_as_refused() {
        EditorCommand? refused = null;

        Add("scene.delete", "scene", () => { });
        keys.SetDefault("scene.delete", Delete);

        dispatcher.Refused += command => refused = command;

        context = "project";
        var args = Press(InputKey.Delete);

        // ⚠ Not handled, so whatever does have the focus can still have the key — and no notice,
        // because "Delete — not available right now" every time somebody presses Delete in a text
        // field is worse than silence.
        Assert.Null(refused);
        Assert.False(args.Handled);
    }

    [Fact]
    public void A_disabled_command_in_scope_is_still_reported_as_refused() {
        EditorCommand? refused = null;

        commands.Add(
            new EditorCommand("scene.delete", Title("Delete"), () => { }) {
                Context = "scene",
                Enablement = () => false
            }
        );

        keys.SetDefault("scene.delete", Delete);
        dispatcher.Refused += command => refused = command;

        context = "scene";
        var args = Press(InputKey.Delete);

        // The chord *is* this command's; letting it fall through would have a disabled Delete
        // deleting something else.
        Assert.Same(commands["scene.delete"], refused);
        Assert.True(args.Handled);
    }

    [Fact]
    public void A_command_that_is_declared_but_not_built_cannot_run_and_says_why() {
        var ran = false;

        commands.Add(
            new EditorCommand("tools.profiler", Title("Profiler"), () => ran = true) {
                Unavailable = new StringId("test.planned", "The profiler is milestone E4.")
            }
        );

        var command = commands["tools.profiler"]!;

        Assert.True(command.IsUnavailable);
        Assert.False(command.CanExecute);
        Assert.False(commands.Execute("tools.profiler"));
        Assert.False(ran);
        Assert.Equal("The profiler is milestone E4.", command.Unavailable.Text);
    }

    static KeyChord Delete => new(InputKey.Delete, ModifierKeys.None);

    KeyEvent Press(InputKey key, ModifierKeys modifiers = ModifierKeys.None) {
        var args = new KeyEvent { Key = key, Modifiers = modifiers, Action = KeyAction.Pressed };

        dispatcher.Pressed(document, args);
        return args;
    }
}
