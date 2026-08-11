// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Input;
using Vixen.Input;
using Vixen.Platform;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Holds the pointer, so looking around is not bounded by the edge of a screen.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Without this a mouse look is silently clamped, and it does not look like a clamp.</b>
///         <c>&lt;Mouse&gt;/delta</c> is the difference between two cursor positions, and a cursor that
///         has reached the edge of the desktop stops producing one — so turning right stops at
///         whatever fraction of a turn the remaining screen was worth, and turning back works
///         immediately. That reads as a yaw limit somebody coded rather than as a pointer that ran
///         out of desk, which is why <see cref="CursorMode.Relative" /> is a *game* decision and not
///         a windowing detail: the platform has had the mode since it was written and nothing asked.
///     </para>
///     <para>
///         <b>Escape lets go, and the window losing focus lets go by itself.</b> A game that grabs
///         the pointer with no way out cannot be quit with a mouse, and one that keeps holding it
///         after an alt-tab steers itself while somebody reads their mail. Clicking back in takes it
///         again — which is what every game does and what nothing in the engine can decide for one.
///     </para>
///     <para>
///         ⚠ <b>It starts <em>un</em>captured, and that is a fix rather than a preference.</b> This
///         began at <c>wanted = true</c>, and an SDL window takes focus the moment it is created — so
///         the pointer was grabbed the instant the window appeared, before anybody had touched it.
///         With a sample being launched from a script every few minutes beside somebody working,
///         that is a mouse and a keyboard disappearing mid-sentence into a game nobody asked to play.
///         The paragraph above already had the answer written down — "clicking back in takes it
///         again" — and only the initial state disagreed with it. So the first click takes the
///         pointer, exactly as clicking back in after an alt-tab does, and the two are now one rule
///         rather than a rule and an exception.
///     </para>
///     <para>
///         ⚠ <b>The release is a release and no longer a toggle, and that follows from the line
///         above.</b> Escape used to flip <c>wanted</c>, which reads correctly only from a captured
///         start: from an uncaptured one the first Escape would <em>grab</em> the pointer, which is
///         the opposite of what the key means everywhere. Press to take, Escape to give back, and
///         the pair is complete without a flip.
///     </para>
///     <para>
///         <b>What this does not touch is the keyboard.</b> A focused window receives key events
///         whatever the cursor is doing, so a sample that pops up still takes typing until somebody
///         clicks away — that is window activation rather than pointer capture, it belongs to the
///         platform layer, and it is bounded now in a way it was not: with the pointer free, clicking
///         away is possible at all.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Input)]
[UpdateAfter(typeof(InputUpdateSystem))]
public sealed class MouseCaptureSystem : SystemBase {
    readonly IWindow? window;
    readonly InputAction? release;
    readonly InputAction? capture;

    bool wanted;
    bool wasFocused = true;

    /// <summary>Captures the pointer of a window on a click, until an action asks for it back.</summary>
    /// <param name="window">The window, or null in a headless run — where this does nothing.</param>
    /// <param name="release">The action that gives the pointer back, normally Escape.</param>
    /// <param name="capture">
    ///     The action that takes it, normally the primary mouse button. Null leaves the pointer free
    ///     for the whole run, which is what a headless or scripted run wants.
    /// </param>
    public MouseCaptureSystem(IWindow? window, InputAction? release, InputAction? capture = null) {
        this.window = window;
        this.release = release;
        this.capture = capture;
    }

    /// <summary>Whether the game is currently asking for the pointer.</summary>
    /// <remarks>
    ///     What it <em>asks</em> for, which is not what the window is doing while it is unfocused —
    ///     the two are separate so that regaining focus restores what the player last chose rather
    ///     than re-grabbing a pointer they deliberately let go of. It now starts <see langword="false" />;
    ///     see the class remarks for why that is the fix and the model is unchanged.
    /// </remarks>
    public bool IsCapturing => wanted;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        if (window is null) {
            return dependency;
        }

        var focused = window.IsFocused;

        if (release?.WasPressedThisFrame == true) {
            wanted = false;
        } else if (focused && capture?.WasPressedThisFrame == true) {
            // ⚠ Gated on focus, because a click is only a click *into this window* when this window
            // is the one receiving it. Without the gate a sample in the background could take the
            // pointer off whatever the person was actually clicking on, which is the failure this
            // whole change exists to remove, arriving one step later.
            wanted = true;
        }

        // Only on a change, and only through the property that already early-outs on one: relative
        // mode is a global SDL state, and setting it every frame is a syscall per frame to say what
        // it already says.
        if (focused != wasFocused || window.CursorMode != Mode(focused)) {
            window.CursorMode = Mode(focused);
            wasFocused = focused;
        }

        return dependency;
    }

    CursorMode Mode(bool focused) => wanted && focused ? CursorMode.Relative : CursorMode.Normal;
}
