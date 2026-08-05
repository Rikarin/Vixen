// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;

namespace Vixen.Water.Physics;

/// <summary>What a scene says about a floating body: its pontoons, and how they float.</summary>
/// <remarks>
///     <para>
///         <b>[35 § D10](../../docs/plan/35-water.md#d10-buoyancy-is-pontoons-over-jolt-evaluated-at-the-fixed-steps-water-time),
///         the world-facing half.</b> The arithmetic is <see cref="Buoyancy" />'s and is tested
///         against an analytic rest displacement; what this adds is an entity, a rigid body and a
///         fixed step.
///     </para>
///     <para>
///         ⚠ <b>The pontoons are a displacement volume and not the collision shape.</b> A barge is
///         four large spheres and a canoe is six small ones along its keel; matching them to the hull
///         mesh is how a boat ends up floating a hand's width too high, and matching them to the
///         collider is how it ends up floating on its bounding box.
///     </para>
///     <para>
///         ⚠ <b>An array makes this a <em>managed</em> component</b>, on
///         <c>WaterBodyComponent</c>'s terms: its values live in the world's store and the chunk
///         holds handles, so <see cref="BuoyancySystem" /> reads it an entity at a time. A fixed
///         inline list would be a cap on how many pontoons a hull may have, chosen here, for every
///         game.
///     </para>
///     <para>
///         ⚠ <b>A body with no pontoons floats nowhere and is not an error.</b> It is what an entity
///         part-way through being authored looks like, and it costs one length check per step.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct BuoyancyBody {
    /// <summary>The spheres, in the body's own frame.</summary>
    public BuoyancyPontoon[] Pontoons;

    /// <summary>A multiplier on the displaced weight. One is Archimedes.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero takes the default of one</b>, on <c>WaterZoneComponent.ScrollThreshold</c>'s
    ///     terms: zero is what a zeroed component holds, and a coefficient of zero is a boat with no
    ///     buoyancy at all — which reads as the whole system being unwired rather than as a field
    ///     nobody filled in.
    /// </remarks>
    public float Coefficient;

    /// <summary>First-order damping on the submerged part's velocity, per second.</summary>
    public float Damping;

    /// <summary>Second-order damping, which is what gives a boat a top speed.</summary>
    public float QuadraticDamping;

    /// <summary>The most upward force one pontoon may produce, in newtons. Zero for no limit.</summary>
    public float MaximumForce;

    /// <summary>How hard a current drags a submerged pontoon along with it, per second.</summary>
    public float FlowDrag;

    /// <summary>A crate: Archimedes exactly, damped enough to settle in a couple of seconds.</summary>
    public static BuoyancyBody Default =>
        new() {
            Pontoons = [],
            Coefficient = 1f,
            Damping = 3f,
            QuadraticDamping = 0.4f,
            MaximumForce = 0f,
            FlowDrag = 2f
        };

    /// <summary>This component as the kernel's own description.</summary>
    /// <remarks>
    ///     The seam where an unset <see cref="Coefficient" /> becomes one rather than a body that
    ///     sinks — see the field's own remarks. The kernel keeps zero meaningful; this is where unset
    ///     becomes the default.
    /// </remarks>
    public readonly BuoyancySettings Settings =>
        new() {
            Coefficient = Coefficient == 0f ? BuoyancySettings.Default.Coefficient : Coefficient,
            Damping = Damping,
            QuadraticDamping = QuadraticDamping,
            MaximumForce = MaximumForce,
            FlowDrag = FlowDrag
        };

    /// <summary>A single sphere of a radius, which is what a crate or a barrel is.</summary>
    /// <param name="radius">How big, in metres.</param>
    /// <returns>The component.</returns>
    public static BuoyancyBody Sphere(float radius) =>
        Default with { Pontoons = [new(Vector3.Zero, radius)] };

    /// <summary>Four pontoons at the corners of a hull, which is what a raft is.</summary>
    /// <param name="halfLength">Half the hull's length along Z, in metres.</param>
    /// <param name="halfWidth">Half its width along X, in metres.</param>
    /// <param name="radius">How big each sphere is, in metres.</param>
    /// <param name="height">How far below the origin they sit, in metres.</param>
    /// <returns>The component.</returns>
    /// <remarks>
    ///     ⚠ <b>Four and not one, because one sphere cannot pitch or roll.</b> A hull with a single
    ///     pontoon bobs and never leans, which reads as a boat on rails — the whole reason the model
    ///     is a list rather than a volume is that the corners tell the solver about the attitude.
    /// </remarks>
    public static BuoyancyBody Raft(float halfLength, float halfWidth, float radius, float height = 0f) =>
        Default with {
            Pontoons = [
                new(new(-halfWidth, height, -halfLength), radius),
                new(new(halfWidth, height, -halfLength), radius),
                new(new(halfWidth, height, halfLength), radius),
                new(new(-halfWidth, height, halfLength), radius)
            ]
        };
}

/// <summary>What the last fixed step did to a floating body.</summary>
/// <remarks>
///     <para>
///         <b>A readout and not an input.</b> <see cref="BuoyancySystem" /> writes it every step and
///         nothing reads it to decide anything — which is exactly why it exists: a boat that sits too
///         low, launches out of the lake or drifts sideways is a bug with no visible cause, and these
///         five numbers are the difference between "buoyancy is broken" and "two of four pontoons are
///         dry".
///     </para>
///     <para>
///         ⚠ <b>Unmanaged, so a debug draw can walk it as a span</b> — the per-pontoon forces are
///         deliberately not here. Those are <see cref="BuoyancySystem.Forces" />' scratch, live for
///         the step that produced them, and copying them into a component would be an array per body
///         per step for a picture nobody is usually looking at.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct BuoyancyState {
    /// <summary>How many pontoons touched water on the last step.</summary>
    public int Wet;

    /// <summary>How many there are in total.</summary>
    public int Total;

    /// <summary>The mean submerged fraction over every pontoon, 0…1.</summary>
    /// <remarks>
    ///     ⚠ <b>Over <em>every</em> pontoon and not over the wet ones.</b> A raft with one corner
    ///     dipped and three in the air is what capsizing looks like, and averaging over the wet ones
    ///     would report it as fully submerged.
    /// </remarks>
    public float Submerged;

    /// <summary>The total upward force applied last step, in newtons.</summary>
    public float Lift;

    /// <summary>Where the surface was over the body's origin, in world units.</summary>
    public float SurfaceHeight;

    /// <summary>Whether any part of it is in water at all.</summary>
    public readonly bool IsFloating => Wet > 0;
}
