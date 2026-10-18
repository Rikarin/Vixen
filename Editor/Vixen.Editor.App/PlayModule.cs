// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Input;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.App;

/// <summary>The transport — Play, Pause, Step, Stop — as a module over <see cref="IPlaySession" />.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D3: the application's own verbs go through the front door.</b> These four
///         were registered by <c>EditorParity.PlayCommands</c> over private methods; they are now
///         registered through the <see cref="PluginContext" /> a third party is handed, over the
///         service a third party can <c>Require</c> — so a command id is a name for a use case and
///         not the place it lives (#1289).
///     </para>
///     <para>
///         ⚠ <b>Registered by hand, not by <c>[EditorMenu]</c>.</b> Doc 50 § D5's in-tree generator
///         does not exist yet (#1283), and an in-tree attribute with no generator does nothing. The
///         Play menu and the toolbar strip still name these ids from the application, which builds
///         the bar; a menu line naming a command nobody registered is skipped, so switching this
///         module off in the plugin manager takes the transport off the bar with it.
///     </para>
/// </remarks>
sealed class PlayModule : IEditorPlugin {
    /// <summary>What the host activates it under.</summary>
    public const string ModuleId = "vixen.play";

    /// <summary>What the plugin manager calls it.</summary>
    public const string ModuleName = "Play";

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        var play = context.Services.Require<IPlaySession>();

        // ⚠ The four carry an icon and a colour class, and the transport is the one strip where that
        // is not decoration. It is the most-clicked control in either reference editor, it is read at
        // a glance rather than looked at, and — the part that matters most — "am I in play mode" has
        // to be answerable without reading anything. A row of identical grey glyphs answers none of
        // those. The theme fills the button when the command is on, so Play is a green button with a
        // white triangle while the game is running and a green triangle when it is not.
        Transport(
            context,
            "play.play",
            EditorStrings.CommandPlayPlay,
            EditorIcons.Play,
            () => play.Enter(),
            () => !play.IsPlaying,
            () => play.IsPlaying
        );

        Transport(
            context,
            "play.pause",
            EditorStrings.CommandPlayPause,
            EditorIcons.Pause,
            play.TogglePause,
            () => play.IsPlaying,
            () => play.State == PlayState.Paused
        );

        Transport(
            context,
            "play.step",
            EditorStrings.CommandPlayStep,
            EditorIcons.Step,
            play.StepFrame,
            () => play.State == PlayState.Paused
        );

        Transport(context, "play.stop", EditorStrings.CommandPlayStop, EditorIcons.Stop, () => play.Leave(), () => play.IsPlaying);

        context.AddDefaultBinding("play.play", new KeyChord(InputKey.F5, ModifierKeys.None));
        context.AddDefaultBinding("play.pause", new KeyChord(InputKey.P, ModifierKeys.Control | ModifierKeys.Shift));
        context.AddDefaultBinding("play.step", new KeyChord(InputKey.F10, ModifierKeys.None));
        context.AddDefaultBinding("play.stop", new KeyChord(InputKey.F5, ModifierKeys.Shift));
    }

    /// <summary>Registers one of the four transport verbs: an icon, a colour, and a state.</summary>
    /// <remarks>
    ///     The class is the command's and the rules are the theme's — see
    ///     <see cref="EditorCommand.ClassName" /> — so the toolbar stays a view over ids and does not
    ///     acquire a list of which buttons are green.
    /// </remarks>
    static void Transport(
        PluginContext context,
        string id,
        StringId title,
        PathBuilder icon,
        Action run,
        Func<bool>? enabled = null,
        Func<bool>? on = null
    ) =>
        context.AddCommand(
            new EditorCommand(id, title, run) {
                Category = EditorStrings.CategoryPlay,
                Icon = icon,
                ClassName = "transport-" + id["play.".Length..],
                Enablement = enabled,
                Checked = on
            }
        );
}
