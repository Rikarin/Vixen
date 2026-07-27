// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Engine.Coroutines;

/// <summary>Where in a frame a suspended coroutine comes back.</summary>
/// <remarks>
///     <para>
///         A small enum of its own rather than <c>SystemPhase</c>, because these are the points a
///         coroutine may be <i>resumed</i> at, and that is a shorter list than the points a system
///         may run at. A resume point costs a drain call in the frame whether or not anything is
///         waiting on it, and points nobody can express a wait against would be paying that for
///         nothing. UniTask draws the same line, and for the same reason: its <c>PlayerLoopTiming</c>
///         is not Unity's <c>PlayerLoop</c>.
///     </para>
///     <para>
///         The four here are the four Unity's coroutines actually offer — <c>yield return null</c>,
///         a late variant, <c>WaitForFixedUpdate</c> and <c>WaitForEndOfFrame</c> — because those are
///         the four questions gameplay code asks. More can be added; each one is a system and a
///         list.
///     </para>
/// </remarks>
public enum ResumePoint {
    /// <summary>With the rest of the frame's game logic, in <c>SystemPhase.Update</c>.</summary>
    Update,

    /// <summary>After everything has moved, in <c>SystemPhase.LateUpdate</c>.</summary>
    LateUpdate,

    /// <summary>
    ///     At a fixed simulation step, in <c>SystemPhase.FixedUpdate</c>. Ticks with the steps, not
    ///     with the frames, so a frame that owes three steps resumes a waiting coroutine three
    ///     times and one that owes none resumes it not at all.
    /// </summary>
    FixedStep,

    /// <summary>After the frame has been submitted, in <c>SystemPhase.PostRender</c>.</summary>
    EndOfFrame
}
