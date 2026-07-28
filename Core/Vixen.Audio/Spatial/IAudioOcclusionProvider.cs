// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Audio.Spatial;

/// <summary>Answers how much solid geometry is between a sound and the person hearing it.</summary>
/// <remarks>
///     <para>
///         <b>An interface, and not a reference to the physics assembly.</b> Occlusion is a raycast
///         and the only thing in this engine that casts rays is <c>Vixen.Physics</c>, which binds
///         Jolt — a native library. A game with sound and no physics would then be shipping and
///         loading Jolt to play a footstep, and the browser target would be shipping it to not load
///         it. So the mixer states what it needs answered and something else answers;
///         <c>Vixen.Audio.Physics</c> is that something for anybody who has physics, and a game with
///         its own idea of what blocks sound implements this instead.
///     </para>
///     <para>
///         <b>Asked on the game thread, once a frame, and not for every voice.</b> A raycast per
///         audible sound per frame is a real cost — sixty-four voices is sixty-four casts — so
///         <see cref="AudioOcclusion" /> spreads them over frames and smooths between the answers.
///         An implementation may therefore assume it is called a bounded number of times per frame
///         and need not cache anything itself.
///     </para>
/// </remarks>
public interface IAudioOcclusionProvider {
    /// <summary>How blocked the path is.</summary>
    /// <param name="source">Where the sound is.</param>
    /// <param name="listener">Where the ear is.</param>
    /// <returns>
    ///     0 for a clear path and 1 for a fully blocked one. Values between are partial — a thin
    ///     wall, a doorway half in the way — and are interpolated across the authored curve like any
    ///     other parameter position.
    /// </returns>
    float Occlusion(in Vector3 source, in Vector3 listener);
}
