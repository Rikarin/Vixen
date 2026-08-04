// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The two menus the viewport summons: a pie under the cursor, and a list.</summary>
/// <remarks>
///     <para>
///         <b>Both are one registration and one filter, which is the whole design.</b> A module or a
///         plugin adds a <see cref="SceneMenuItem" /> naming a command, a surface and — the part that
///         matters — a mode; what it gets is a line in the context menu, a wedge in the pie, or both,
///         in every pane, without either menu knowing the module exists. See
///         <c>SceneMenuItem</c> for why it is a command id rather than an action.
///     </para>
///     <para>
///         ⚠ <b>Doc 24's argument for modes, finished.</b> A mode is a set of verbs somebody uses
///         constantly and a viewport cannot show them all — the mode bar and the tool strip are two
///         answers to that, and both cost a trip to the edge of the screen. A pie under the cursor
///         costs a flick and does not move the eye, which is what the tools somebody uses forty times
///         a minute need. Blockout's extrude and the terrain brushes are exactly that shape.
///     </para>
///     <para>
///         ⚠ <b>Nothing is registered for a mode that is not active, rather than being greyed.</b> A
///         pie with four live wedges and eleven dead ones is a pie whose directions move as modes
///         change, and the directions being fixed is the entire reason it is fast. A command that is
///         momentarily unavailable — nothing selected — is still shown, disabled, because that is
///         about the selection rather than about which menu this is.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>The pie, made on first use because an overlay needs a document.</summary>
    RadialMenu? radial;

    /// <summary>The list the context key opens.</summary>
    ContextMenu? sceneMenu;

    void SceneMenuCommands() {
        Shell.Commands.Add(
            new EditorCommand(
                "scene.radial-menu",
                new StringId("editor.command.radial-menu", "Radial Menu"),
                OpenRadialMenu
            ) {
                Category = CategoryScene,
                Enablement = () => Viewport is not null
            }
        );

        Shell.Commands.Add(
            new EditorCommand(
                "scene.context-menu",
                new StringId("editor.command.scene-context-menu", "Scene Context Menu"),
                OpenSceneMenu
            ) {
                Category = CategoryScene,
                Enablement = () => Viewport is not null
            }
        );

        // ⚠ Two unmodified keys in the viewport, which is a budget worth spending here. W, E and R
        // are the gizmo and 1–4 belong to whichever element mode is active; Q and C were free, and a
        // menu that costs a chord is a menu people reach for the mouse instead of.
        Shell.Keys.SetDefault("scene.radial-menu", new KeyChord(InputKey.Q, ModifierKeys.None));
        Shell.Keys.SetDefault("scene.context-menu", new KeyChord(InputKey.C, ModifierKeys.None));
    }

    /// <summary>What a menu should offer, given which one it is and what mode is on.</summary>
    /// <remarks>
    ///     ⚠ <b>Asked on every open rather than built once.</b> Both halves move: a plugin loading
    ///     changes what is registered and a mode change changes what applies, and a menu assembled at
    ///     start-up would be the Select mode's for the life of the session. It is a filter over a few
    ///     dozen records.
    /// </remarks>
    IEnumerable<SceneMenuItem> SceneMenuEntries(SceneMenuSurface surface) {
        var mode = Shell.Modes.Active?.Id;

        return Extensions.All<SceneMenuItem>()
            .Where(entry => (entry.Surface & surface) != 0)
            .Where(entry => entry.Mode is null || string.Equals(entry.Mode, mode, StringComparison.Ordinal))
            .OrderBy(entry => entry.Order)
            .ThenBy(entry => Title(entry), StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>What a line says: the entry's own label, or the command's.</summary>
    string Title(SceneMenuItem entry) =>
        entry.Label.Length > 0
            ? entry.Label
            : Shell.Commands[entry.CommandId]?.Title.Text ?? entry.CommandId;

    /// <summary>Drops the pie under the cursor, in the pane the keyboard is driving.</summary>
    /// <remarks>
    ///     ⚠ <b>Opened as a held gesture, because a command runs on the key going <i>down</i>.</b>
    ///     That is what makes both of the menu's two gestures reachable from one binding: the release
    ///     that follows a moment later either lands on a wedge, and runs it, or lands in the dead zone
    ///     and leaves the menu up to be clicked. See <c>RadialMenu.Hold</c>.
    /// </remarks>
    void OpenRadialMenu() {
        if (Viewport is not { } pane) {
            return;
        }

        radial ??= Shell.Document.Root.Add<RadialMenu>();
        radial.Clear();

        var offered = SceneMenuEntries(SceneMenuSurface.Radial).ToList();

        if (offered.Count == 0) {
            // ⚠ Said rather than shown empty. A pie that opens as an empty ring reads as broken; a
            // mode nobody has given verbs to is an ordinary state and one sentence covers it.
            Shell.Notifications.Show(
                Shell.Modes.Active is { } active
                    ? $"{active.Title.Text} has no radial menu entries"
                    : "Nothing is registered for the radial menu"
            );

            return;
        }

        foreach (var entry in offered) {
            var command = Shell.Commands[entry.CommandId];
            var item = radial.AddItem(Title(entry));

            item.Disabled = command is null || !Shell.Commands.CanExecute(command);

            if (entry.Art is { } art) {
                item.LeadingIcon.Art = art;
            }
        }

        radial.Chose -= RadialChosen;
        radial.Chose += RadialChosen;

        radial.OpenAt(pane.PointerPosition.X, pane.PointerPosition.Y, hold: true);
    }

    void RadialChosen(RadialMenu menu, RadialItem item) {
        var offered = SceneMenuEntries(SceneMenuSurface.Radial).ToList();

        if (item.Index >= 0 && item.Index < offered.Count) {
            Shell.Commands.Execute(offered[item.Index].CommandId);
        }
    }

    /// <summary>Opens the context list under the cursor.</summary>
    /// <remarks>
    ///     ⚠ <b>A key rather than the secondary button, and the viewport is why.</b> Right-drag in a
    ///     3D pane orbits and right-press begins fly navigation — see <c>SceneViewport.Flies</c> —
    ///     which every 3D editor binds the same way and none of them is willing to give up for a
    ///     menu. A key costs a binding and takes nothing away.
    /// </remarks>
    void OpenSceneMenu() {
        if (Viewport is not { } pane) {
            return;
        }

        sceneMenu ??= Shell.Document.Root.Add<ContextMenu>();
        sceneMenu.Clear();

        var offered = 0;

        foreach (var entry in SceneMenuEntries(SceneMenuSurface.Context)) {
            var command = Shell.Commands[entry.CommandId];
            var item = sceneMenu.AddItem(Title(entry));
            var id = entry.CommandId;

            item.Disabled = command is null || !Shell.Commands.CanExecute(command);

            // The chord, so that the list is also where somebody learns the shortcut — which is most
            // of what a context menu is for once the commands are known.
            if (Shell.Keys.ChordFor(id) is { } chord) {
                item.ShowShortcut(chord.Key, chord.Modifiers);
            }

            item.Clicked += _ => Shell.Commands.Execute(id);
            offered++;
        }

        if (offered == 0) {
            // A menu that opens onto nothing reads as broken rather than as empty — the rule the Add
            // Component list and Open Recent both follow.
            sceneMenu.AddItem("Nothing here for this mode").Disabled = true;
        }

        sceneMenu.OpenAt(pane.PointerPosition.X, pane.PointerPosition.Y);
    }

    /// <summary>What the editor itself puts in the two menus.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Registered rather than built in, so that the editor's own entries and a plugin's
    ///         arrive by the same door.</b> This is doc 36 § D2's rule applied to one more surface:
    ///         an extension point whose own author bypasses it is a guess rather than a contract, and
    ///         the fastest way to find out that <see cref="SceneMenuItem" /> is missing something is
    ///         to be its first user.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Eight wedges and no more.</b> A pie is aimed, and past eight the wedges are
    ///         forty-five degrees apart and a flick starts choosing its neighbours; every editor that
    ///         ships one draws the line in the same place. The context list has no such limit and
    ///         carries the longer tail.
    ///     </para>
    /// </remarks>
    void RegisterSceneMenus() {
        // The pie: what somebody does constantly in the default mode, in a fixed ring. The order is
        // the directions — top, then clockwise — and it is not to be shuffled, because a flick that
        // moves is a flick nobody learns.
        Radial("scene.translate", 0);
        Radial("scene.rotate", 1);
        Radial("scene.scale", 2);
        Radial("scene.focus", 3);
        Radial("scene.toggle-grid", 4);
        Radial("scene.frame-all", 5);
        Radial("scene.maximise", 6);
        Radial("scene.toggle-projection", 7);

        // The list: the verbs that act on what is selected, which are read rather than aimed.
        Context("entity.duplicate", 0);
        Context("entity.delete", 1);
        Context("entity.rename", 2);
        Context("entity.focus", 3);
        Context("entity.snap-to-floor", 4);
        Context("entity.move-to-view", 5);
        Context("scene.create-empty", 6);

        void Radial(string command, int order) => Offer(command, SceneMenuSurface.Radial, order);

        void Context(string command, int order) => Offer(command, SceneMenuSurface.Context, order);

        void Offer(string command, SceneMenuSurface surface, int order) {
            // ⚠ Only for commands that exist. This runs after the editor's own commands are
            // registered, so a name that resolves to nothing here is a typo in this method rather
            // than a plugin's business — and a wedge that runs nothing is worse than one less wedge.
            if (Shell.Commands[command] is null) {
                return;
            }

            Extensions.Add(new SceneMenuItem(command, surface) { Order = order });
        }
    }
}
