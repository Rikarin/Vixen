// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Microsoft.Extensions.Logging;
using Vixen.Core.Diagnostics;
using Vixen.Editor.Core;
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
        var registry = context.Services.Require<IEditorRegistry>();

        // ── What the editor contributes to the frame a session runs (#1293) ─────────────────────
        // ⚠ Moved out of `EditorApplication`'s constructor: these are the Session context's, and a
        // module's contribution is the door a third party's passes come through too — so switching
        // Play off takes them with the transport, and an unload withdraws them.
        //
        // ⚠ The editor's own contribution to the frame a session runs. Doc 31 § D10 said an embedding
        // host would have to add the four physics passes by hand; it does not, because `IPlaySystems`
        // is read at every Play.
        //
        // ⚠ This comment used to go on "and this is registered before any module activates — which
        // is also what lets the terrain module's collider contribution find the scene this one
        // provides". That dependency is now declared: `PlayPhysics` carries
        // `[Provides(typeof(PhysicsScene))]` and the collider contribution `[RunsAfter]` for the
        // same type, so the sequence of these two lines in two assemblies no longer decides it.
        // ⚠ First, and ordered by `[Provides]` rather than by being written first — see
        // `PlayDiagnostics`. Until this a `PlaySession` seeded two services, the loop and the world,
        // so `PhysicsSystems.AddPhysicsOverlay` had nothing in an editor to be handed (#1247).
        if (play is EditorPlaySession session) {
            context.Owns(registry.Add<IPlaySystems>(new PlayDiagnostics(session.Debug, play.Overlays)));
        }

        context.Owns(registry.Add<IPlaySystems>(new PlayPhysics()));

        // ⚠ And the animation passes, which until #1221 no production code anywhere registered: the
        // only call to `AnimationSystems.AddAnimation` in the tree was a test's, so an
        // `AnimatorComponent` was evaluated by nothing. Unconditional and owning nothing — see
        // `PlayAnimation`, which also records why `[GameSystem]` is not the answer for an engine
        // pass.
        context.Owns(registry.Add<IPlaySystems>(new PlayAnimation()));

        // ⚠ And the agents, built from what the project's assemblies declared — see `PlayAi`, and
        // `IActivePlaySession` for how the agent debugger reaches a system that lives only as
        // long as the session. #470.
        context.Owns(registry.Add<IPlaySystems>(new PlayAi()));

        // ⚠ And the keyboard, which a session reads only from a focused scene pane. The object is the
        // application's — every pane's input goes through it ahead of the active mode — and the
        // contribution is this module's, registered after the agents and before any feature module
        // activates, because a contribution that declares no order keeps its registration order.
        context.Owns(registry.Add<IPlaySystems>(context.Services.Require<PlayInput>()));

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

        // ── What entering play says (#1296) ──────────────────────────────────────────────────────
        // ⚠ After the application's own `Entered` handler — it subscribed in the constructor, before any
        // module activated — so the console has been cleared before the first line about play lands.
        var logger = context.Services.Require<RingBufferSink>().CreateLogger(EditorLog.Category);
        var project = context.Services.Require<IProjectSession>();

        void Entered(IPlaySession _) => Announce(context.Shell, logger, play, project);

        play.Entered += Entered;
        context.OnUnload(() => play.Entered -= Entered);

        context.AddDefaultBinding("play.play", new KeyChord(InputKey.F5, ModifierKeys.None));
        context.AddDefaultBinding("play.pause", new KeyChord(InputKey.P, ModifierKeys.Control | ModifierKeys.Shift));
        context.AddDefaultBinding("play.step", new KeyChord(InputKey.F10, ModifierKeys.None));
        context.AddDefaultBinding("play.stop", new KeyChord(InputKey.F5, ModifierKeys.Shift));
    }

    /// <summary>Says that play-mode edits are discarded, what the session runs, and anything it is not running.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Said before it matters rather than after it has cost something.</b> Doc 20 calls this
    ///         the one place where being different from Unity is better: the rule is that play-mode
    ///         edits are discarded, and an editor that says so on the way in is honest where a silent
    ///         loss is the single most complained-about behaviour in that editor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the gaps are not decoration on that.</b> The graph is the engine's default set
    ///         and a game's own is not declarable anywhere, so a session that said nothing would make the
    ///         systems it is not running read as the user's bug. The wording is the session's —
    ///         <see cref="EditorPlaySession.DescribeRunning" /> and
    ///         <see cref="EditorPlaySession.DescribeMissing" />; where it is said is the editor's log and
    ///         notifications. Moved from the application's <c>PlayEntered</c> (#1296).
    ///     </para>
    /// </remarks>
    static void Announce(EditorShell shell, ILogger logger, IPlaySession play, IProjectSession project) {
        shell.Notifications.Show(
            "Play mode",
            NotificationSeverity.Info,
            "Changes made while playing are discarded when you stop."
        );

        if (play is not EditorPlaySession session) {
            return;
        }

        Log(logger, LogLevel.Information, session.DescribeRunning());

        if (session.DescribeMissing(project.Code.Loaded) is not { } said) {
            return;
        }

        Log(logger, LogLevel.Warning, said);

        shell.Notifications.Show("Play mode is not running everything", NotificationSeverity.Warning, said);
    }

    /// <summary>Writes a sentence the way <see cref="EditorLog.Write(LogLevel, string)" /> does, under its category.</summary>
    static void Log(ILogger logger, LogLevel level, string message) =>
        logger.Log(level, default, message, null, static (state, _) => state);

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
