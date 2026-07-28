// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Input;

/// <summary>The knobs that are the same for every action unless something says otherwise.</summary>
/// <remarks>
///     Constants rather than a settings asset. Every one of these is overridable per binding through
///     an interaction's arguments, which is where a game that cares actually wants to say it; a
///     global settings object would be a second place for the same number to live and a third answer
///     to "why does this action feel different in the editor".
/// </remarks>
public static class InputSettings {
    /// <summary>How far from rest an analogue control must be to count as pressed.</summary>
    /// <remarks>
    ///     Half. Low enough that a trigger pull registers before it bottoms out, high enough that a
    ///     worn stick's drift does not fire a button.
    /// </remarks>
    public const float DefaultPressPoint = 0.5f;

    /// <summary>
    ///     How far a control must move before a rebinding operation will accept it as the answer.
    /// </summary>
    /// <remarks>
    ///     Higher than <see cref="DefaultPressPoint" /> on purpose: a player rebinding "jump" who
    ///     brushes a stick on the way to the button must not have the stick captured instead.
    /// </remarks>
    public const float DefaultRebindThreshold = 0.7f;
}

/// <summary>Where an action is in its lifecycle.</summary>
/// <remarks>
///     <para>
///         Unity's five, because the four an engine reaches for first — pressed, held, released,
///         nothing — cannot express an interaction that starts and then <em>fails</em>: a hold that
///         was released early has to end without having acted, and a state machine with no
///         <see cref="Canceled" /> has to report it as a release, which is exactly the frame a
///         charge-attack fires on when it should not.
///     </para>
/// </remarks>
public enum InputActionPhase : byte {
    /// <summary>The action is not enabled and will report nothing.</summary>
    Disabled = 0,

    /// <summary>Enabled, and nothing is happening.</summary>
    Waiting = 1,

    /// <summary>A control was actuated and an interaction is watching it.</summary>
    Started = 2,

    /// <summary>The interaction's condition was met. This is the frame a game acts on.</summary>
    Performed = 3,

    /// <summary>The interaction ended without being met, or the control was released.</summary>
    Canceled = 4
}
