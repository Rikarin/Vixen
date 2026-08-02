// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Behaviors;
using Vixen.Vfx;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>Steps one lamp's embers, once a frame.</summary>
/// <remarks>
///     <para>
///         <b>Nothing in the engine steps a <see cref="VfxSystem" />.</b> There is no particle
///         component and therefore no system that could find one — <c>ParticleRenderFeature</c> draws
///         whatever it has been handed and never advances it, deliberately, because a renderer that
///         simulated would simulate once per view. So the advance is somebody's, and in this project
///         the per-frame hook is a behaviour: the same one <see cref="LampFlicker" /> is attached
///         through, on the same entities, for the same reason.
///     </para>
///     <para>
///         <b>Third of the three ways this level attaches a behaviour, and the shortest.</b>
///         <c>PlayerRig</c> hangs its behaviours off entities it made; <see cref="LampFlicker" /> and
///         <see cref="SunOrbit" /> find entities the level made through a query and configure
///         themselves from what they find. This one is found the same way and configures nothing: the
///         effect, the render object and the material are all graphics, and graphics is
///         <c>Arena.SupplyFrame</c>'s — so what arrives here is the one thing a behaviour can do about
///         it, which is to advance the clock.
///     </para>
///     <para>
///         ⚠ <b>The effect is shared, not owned.</b> <c>Arena</c> holds the same instance so that it
///         can hand it to the render feature and give it back at shutdown, so this must not dispose
///         it: a behaviour destroyed when its entity goes would otherwise free a buffer the frame in
///         flight is still expanding.
///     </para>
/// </remarks>
public sealed class EmberDrift : Behavior {
    /// <summary>The lamp's particle system, created and owned by <see cref="Arena" />.</summary>
    public required VfxSystem Effect { get; init; }

    /// <inheritdoc />
    /// <remarks>
    ///     <c>Update</c> rather than <c>LateUpdate</c>, and it matters by exactly one frame: the
    ///     renderer expands what the system holds when it prepares, which is after the whole frame's
    ///     behaviours have run. Stepping late would still be before the expansion — but it would put
    ///     the particles a step ahead of the transforms everything else was solved from, which is the
    ///     ordering <c>Arena</c>'s camera note is about.
    /// </remarks>
    protected override void Update() => Effect.Step(Time.DeltaSeconds);
}
