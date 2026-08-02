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
/// </remarks>
[UpdateInGroup(SystemPhase.Input)]
[UpdateAfter(typeof(InputUpdateSystem))]
public sealed class MouseCaptureSystem : SystemBase {
    readonly IWindow? window;
    readonly InputAction? release;

    bool wanted = true;
    bool wasFocused = true;

    /// <summary>Captures the pointer of a window, until an action asks for it back.</summary>
    /// <param name="window">The window, or null in a headless run — where this does nothing.</param>
    /// <param name="release">The action that gives the pointer back, normally Escape.</param>
    public MouseCaptureSystem(IWindow? window, InputAction? release) {
        this.window = window;
        this.release = release;
    }

    /// <summary>Whether the game is currently asking for the pointer.</summary>
    /// <remarks>
    ///     What it <em>asks</em> for, which is not what the window is doing while it is unfocused —
    ///     the two are separate so that regaining focus restores what the player last chose rather
    ///     than re-grabbing a pointer they deliberately let go of.
    /// </remarks>
    public bool IsCapturing => wanted;

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        if (window is null) {
            return dependency;
        }

        if (release?.WasPressedThisFrame == true) {
            wanted = !wanted;
        }

        var focused = window.IsFocused;

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
