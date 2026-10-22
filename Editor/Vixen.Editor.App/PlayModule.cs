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

        // ── Play ▸ Mode and Mute Audio, declared and disabled with the reason (#1296) ──────────────
        // ⚠ Standalone and Server both named milestone E6, and E6 has shipped — `build.settings` is a
        // window and `build.run` publishes and starts a player. What is actually missing is on this
        // side of the line and is the same thing twice: a *supervised* process. Play is a transport —
        // Pause, Step and Stop have to mean something — and `PlayerDelivery.Build` hands the artefact
        // to the OS and forgets it, so a play mode over it would be a Play button with three dead
        // buttons beside it. A reason naming a mechanism can be checked against the tree; one naming
        // a schedule stayed on screen for two milestones after its schedule closed.
        foreach (var (id, title) in PlannedLines) {
            context.AddCommand(
                new EditorCommand(id, title, () => { }) {
                    Category = EditorStrings.CategoryPlay,
                    Unavailable = EditorStrings.PlannedReasons[id]
                }
            );
        }

        // ── Play ▸ Overlays: one checked line per diagnostic overlay (#1263) ───────────────────
        // ⚠ From the declaration rather than from `IPlaySession.Overlays.Registered`, because while
        // editing nothing is registered — the physics overlay exists only between Play and Stop — and
        // a switch that appeared only once the game was running could not be set before pressing
        // Play, which is when somebody sets up what they are about to watch. Not a `SceneShow` flag
        // either: a show flag is per pane, an overlay's `Enabled` is per session and is already the
        // switch, and two switches that have to agree is the failure `SceneShow`'s remarks describe.
        foreach (var name in SwitchableOverlays()) {
            var overlay = name;

            context.AddCommand(
                new EditorCommand(OverlayCommand(overlay), EditorStrings.PlayOverlays[overlay], () => play.SwitchOverlay(overlay)) {
                    Category = EditorStrings.CategoryPlay,
                    Checked = () => play.IsOverlayOn(overlay)
                }
            );
        }

        context.AddDefaultBinding("play.play", new KeyChord(InputKey.F5, ModifierKeys.None));
        context.AddDefaultBinding("play.pause", new KeyChord(InputKey.P, ModifierKeys.Control | ModifierKeys.Shift));
        context.AddDefaultBinding("play.step", new KeyChord(InputKey.F10, ModifierKeys.None));
        context.AddDefaultBinding("play.stop", new KeyChord(InputKey.F5, ModifierKeys.Shift));
    }

    /// <summary>The Play menu's lines that are declared and not yet built.</summary>
    static readonly (string Id, StringId Title)[] PlannedLines = [
        ("play.mode-in-editor", EditorStrings.CommandPlayModeInEditor),
        ("play.mode-standalone", EditorStrings.CommandPlayModeStandalone),
        ("play.mode-server", EditorStrings.CommandPlayModeServer),
        ("play.mute-audio", EditorStrings.CommandPlayMuteAudio)
    ];

    /// <summary>The command that switches one diagnostic overlay, by the overlay's name.</summary>
    /// <param name="name">What the overlay is called — <c>physics</c>.</param>
    /// <returns><c>play.overlay.</c> followed by the name.</returns>
    internal static string OverlayCommand(string name) => "play.overlay." + name;

    /// <summary>The overlay names the editor has a declared word for, in menu order.</summary>
    internal static IEnumerable<string> SwitchableOverlays() =>
        EditorStrings.PlayOverlays.All.Select(id => id.Id[EditorStrings.PlayOverlays.Prefix.Length..]);

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
