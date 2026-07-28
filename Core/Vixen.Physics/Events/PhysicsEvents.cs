// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Physics.Bodies;

namespace Vixen.Physics.Events;

/// <summary>Where in a contact's life an event was raised.</summary>
public enum ContactPhase {
    /// <summary>The first step the two bodies touched.</summary>
    Began,

    /// <summary>A step in which they went on touching.</summary>
    Continued,

    /// <summary>The step after they stopped.</summary>
    /// <remarks>
    ///     Carries no manifold: there is nothing to describe, because the contact is being reported
    ///     precisely because it no longer exists. The positions and the normal are zero.
    /// </remarks>
    Ended
}

/// <summary>Two bodies touched, went on touching, or stopped.</summary>
/// <param name="Phase">Which of the three.</param>
/// <param name="First">One body.</param>
/// <param name="Second">The other.</param>
/// <param name="Position">A point on the contact manifold, in world space.</param>
/// <param name="Normal">
///     The contact normal, pointing from <paramref name="First" /> towards <paramref name="Second" />.
/// </param>
/// <param name="PenetrationDepth">How far the two overlap along the normal, in metres.</param>
/// <remarks>
///     <para>
///         One event per pair per step, not one per manifold point. A box landing flat on the floor
///         has four contact points and is one thing that happened; the callback that wants all four
///         wants the manifold, and the callback that wants to play a thud wants this.
///     </para>
///     <para>
///         <b>Which body is first is not stable.</b> Jolt orders a pair by its own internal rules and
///         the order can change between steps as bodies are created and destroyed. Anything that
///         cares which is which must test, not assume.
///     </para>
/// </remarks>
public readonly record struct ContactEvent(
    ContactPhase Phase,
    BodyHandle First,
    BodyHandle Second,
    Vector3 Position,
    Vector3 Normal,
    float PenetrationDepth
);

/// <summary>Something entered or left a sensor.</summary>
/// <param name="Phase">
///     <see cref="ContactPhase.Began" /> on entry, <see cref="ContactPhase.Ended" /> on exit.
///     A trigger never reports <see cref="ContactPhase.Continued" /> — see the remarks.
/// </param>
/// <param name="Sensor">The trigger volume.</param>
/// <param name="Other">What entered or left it.</param>
/// <remarks>
///     Enter and exit only, because that is what a trigger is for and because "still inside" is a
///     question with a cheaper answer: the set of bodies currently in a sensor is a query the caller
///     can keep for itself, and raising an event per body per step for a large volume is a great deal
///     of traffic to describe a thing that did not change.
/// </remarks>
public readonly record struct TriggerEvent(ContactPhase Phase, BodyHandle Sensor, BodyHandle Other);
