// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Engine.Input;
using Vixen.Input;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>A play session's keyboard: what a focused scene pane hears, and nothing else (#470).</summary>
/// <remarks>
///     <para>
///         <b>The decision, made on Jiu's behalf: gated on viewport focus, with Escape releasing it,
///         as Unreal does.</b> Typing a name into the inspector must never move the player, and a
///         Play button whose game hears no keys is not a play mode. So the session reads keys only
///         while a scene pane has the focus — and the gate costs nothing, because the document already
///         delivers a key to the focused element and nowhere else: a key reaches
///         <see cref="Key" /> only by arriving at a pane.
///     </para>
///     <para>
///         ⚠ <b>Fed from the pane's own key events rather than from the host's pump</b>, which is what
///         answers the issue's third question. <c>EditorHost.Pump</c> is in <c>Vixen.Editor.Host</c>
///         and the session is this assembly's; routing <c>PlatformEvent</c>s across that line would
///         need one of the two to see the other, and would then need focus re-derived from window
///         coordinates. A <see cref="KeyEvent" /> carries the same USB HID <see cref="InputKey" /> a
///         platform event does — <c>PlatformInput.Submit</c>'s cast is the identity — so there is
///         nothing to translate.
///     </para>
///     <para>
///         ⚠ <b>One service for the editor's life, published once.</b> <c>PluginServices</c> has no
///         removal, so a per-session service published there is a dead object for every reader
///         after the first Stop — <see cref="PlayPhysics" />'s reason, one layer over. The service
///         outlives the session and is <see cref="PlaySession.Provide{T}" />d to each one; what a
///         session adds to it — the action assets a game's systems register — leaves with the session,
///         and so does every held key.
///     </para>
///     <para>
///         ⚠ <b>Consumed, except where the editor must still be reachable.</b> A key the game took is
///         marked handled, or W would drive the player <i>and</i> switch the gizmo to translate. A
///         chord with Control, Alt or Meta and every function key go on to the shell as well —
///         F5 and Shift+F5 are the transport, and a captured keyboard that swallowed Stop would be a
///         session with no way out but Escape.
///     </para>
/// </remarks>
/// <param name="next">What has the pane's input when the game does not: the active editor mode.</param>
[Provides(typeof(InputService))]
sealed class PlayInput(IViewportInput next) : IPlaySystems, IViewportInput {
    readonly HashSet<InputActions> before = [];

    PlaySession? session;

    /// <summary>Whether Escape let go of the keyboard since the pane last took the focus or a click.</summary>
    bool released;

    /// <summary>The service every session is handed and the input debug panel reads.</summary>
    public InputService Service { get; } = new();

    /// <summary>Whether a key arriving at a pane now would reach the game.</summary>
    public bool Captured => session is not null && !released;

    /// <inheritdoc />
    public void Attach(PlaySession session) {
        ArgumentNullException.ThrowIfNull(session);

        this.session = session;
        released = false;

        before.Clear();
        before.UnionWith(Service.Assets);

        // In `SystemPhase.Input`, the phase that exists for it, so a game's systems read this frame's
        // keys whatever order they were declared in.
        session.Loop.Add(new InputUpdateSystem(Service));
        session.Provide(Service);
        session.Runs("input");

        session.OnStop(Detach);
    }

    /// <inheritdoc />
    public bool Pointer(IViewportPane pane, PointerEvent args) {
        // A click in the pane takes the keyboard back after Escape, which is Unreal's capture-on-click.
        // Not consumed: the click is still the pane's, to select or to orbit with.
        if (args.Action == PointerAction.Pressed) {
            released = false;
        }

        return next.Pointer(pane, args);
    }

    /// <inheritdoc />
    public bool Key(IViewportPane pane, KeyEvent args) {
        if (!Captured) {
            return next.Key(pane, args);
        }

        if (args is { Key: InputKey.Escape, Action: KeyAction.Pressed }) {
            Release();
            released = true;
            return true;
        }

        Service.Devices.SubmitKey(args.Key, args.Action == KeyAction.Pressed);

        return !Editors(args);
    }

    /// <summary>Follows a pane's focus: gaining it captures, losing it lets go of everything held.</summary>
    /// <param name="gained">Whether the pane gained the focus.</param>
    /// <remarks>
    ///     ⚠ <b>The release is the point.</b> A key held as the focus leaves is released somewhere
    ///     this never hears — the inspector — so without it the game would believe W is still down,
    ///     which <see cref="InputDeviceSet.ResetHeldState" />'s own remarks call the commonest bug in
    ///     this part of an engine.
    /// </remarks>
    public void Focus(bool gained) {
        if (gained) {
            released = false;
        } else {
            Release();
        }
    }

    /// <summary>Whether the editor hears a key the game also took: a command chord or a function key.</summary>
    static bool Editors(KeyEvent args) =>
        (args.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Meta)) != 0
        || args.Key is >= InputKey.F1 and <= InputKey.F12 or >= InputKey.F13 and <= InputKey.F24;

    void Release() {
        if (session is not null) {
            Service.ResetHeldState();
        }
    }

    void Detach() {
        Service.ResetHeldState();

        // What the session's systems registered goes with them: a second Play would otherwise update
        // the first session's action assets, held by systems that no longer exist.
        foreach (var asset in Service.Assets.Where(asset => !before.Contains(asset)).ToArray()) {
            Service.Remove(asset);
        }

        before.Clear();
        session = null;
    }
}
