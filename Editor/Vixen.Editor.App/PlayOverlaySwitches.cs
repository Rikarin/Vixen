// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ui;
using Vixen.Engine.Diagnostics.Overlays;

namespace Vixen.Editor.App;

/// <summary>The person-facing switch for a play session's diagnostic overlays.</summary>
/// <remarks>
///     <para>
///         <b>What a game types at its console, as a menu line</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1263">#1263</a>). A game switches an
///         overlay with <c>overlay physics on</c>, <c>--vixen-overlay physics</c> or
///         <c>AppConfig.EnabledOverlays</c>; the editor had none of the three, so the collider
///         wireframes #1247 wired were on in the test suite and in no editor anybody had open. The
///         command palette is the editor's console — <c>WaterModuleDebug</c> is the precedent — so
///         each overlay is one checked command, <c>play.overlay.&lt;name&gt;</c>, also listed under
///         Play ▸ Overlays.
///     </para>
///     <para>
///         ⚠ <b>The wish is the editor's and the switch is the overlay's, and the two have to be
///         kept apart.</b> <c>IDiagnosticOverlay.Enabled</c> lives on the overlay object, and the
///         physics one is registered by <c>PlayPhysics</c> on Play and removed on Stop, because it
///         holds a <c>PhysicsScene</c> Stop destroys. So the registry being editor-wide kept
///         nothing: the next Play registered a new overlay, switched off. What outlives a session
///         here is <see cref="overlayWishes" />, applied to each overlay the first frame it is seen.
///     </para>
///     <para>
///         ⚠ <b>Not a <c>SceneShow</c> flag.</b> A show flag is per pane; an overlay's
///         <c>Enabled</c> is per session and is already the switch, and two switches that have to
///         agree is the failure <c>SceneShow</c>'s own remarks describe.
///     </para>
/// </remarks>
sealed partial class EditorApplication {
    /// <summary>The overlays somebody has switched on, by name, across every session.</summary>
    readonly HashSet<string> overlayWishes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The overlay object last seen under each name, so a new session's is recognised as new.</summary>
    readonly Dictionary<string, IDiagnosticOverlay> overlaysSeen = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The command that switches one diagnostic overlay, by the overlay's name.</summary>
    /// <param name="name">What the overlay is called — <c>physics</c>.</param>
    /// <returns><c>play.overlay.</c> followed by the name.</returns>
    internal static string PlayOverlayCommand(string name) => "play.overlay." + name;

    /// <summary>The overlay names the editor has a declared word for, in menu order.</summary>
    /// <remarks>
    ///     ⚠ <b>From the declaration rather than from <c>PlayOverlays.Registered</c></b>, because
    ///     while editing nothing is registered — the physics overlay exists only between Play and
    ///     Stop — and a switch that appeared only once the game was running could not be set before
    ///     pressing Play, which is when somebody sets up what they are about to watch.
    /// </remarks>
    static IEnumerable<string> SwitchableOverlays() =>
        EditorStrings.PlayOverlays.All.Select(id => id.Id[EditorStrings.PlayOverlays.Prefix.Length..]);

    /// <summary>Registers one checked command per overlay.</summary>
    void PlayOverlayCommands() {
        foreach (var name in SwitchableOverlays()) {
            var overlay = name;

            Verb(
                PlayOverlayCommand(overlay),
                EditorStrings.PlayOverlays[overlay],
                EditorStrings.CategoryPlay,
                () => SwitchOverlay(overlay),
                on: () => OverlayOn(overlay)
            );
        }
    }

    /// <summary>Whether an overlay is on: the live one if a session has it, the wish if not.</summary>
    bool OverlayOn(string name) =>
        playOverlays.Find(name) is { } live ? live.Enabled : overlayWishes.Contains(name);

    /// <summary>Flips the wish, and the live overlay with it if a session is running one.</summary>
    void SwitchOverlay(string name) {
        var on = !OverlayOn(name);

        if (on) {
            overlayWishes.Add(name);
        } else {
            overlayWishes.Remove(name);
        }

        playOverlays.Set(name, on);
    }

    /// <summary>Applies the wishes to overlays a session has just registered, and learns from the rest.</summary>
    /// <remarks>
    ///     <para>
    ///         Run every frame before the loop steps, because a session's overlays are registered by
    ///         its contributions when Play is pressed, and the first frame that draws is this one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An overlay already seen is read <em>from</em>, not written to.</b> Something else
    ///         can switch a live overlay — <c>PlayOverlays.Set</c> is internal API and a plugin may
    ///         hold its overlay — and writing the wish over it every frame would make that switch
    ///         snap back on the next frame. Reading it into the wish keeps one answer and carries it
    ///         across Stop.
    ///     </para>
    /// </remarks>
    void FollowPlayOverlays() {
        foreach (var overlay in playOverlays.Registered) {
            var name = overlay.Name;

            if (overlaysSeen.TryGetValue(name, out var seen) && ReferenceEquals(seen, overlay)) {
                if (overlay.Enabled) {
                    overlayWishes.Add(name);
                } else {
                    overlayWishes.Remove(name);
                }

                continue;
            }

            overlaysSeen[name] = overlay;

            if (overlayWishes.Contains(name)) {
                overlay.Enabled = true;
            }
        }
    }
}
