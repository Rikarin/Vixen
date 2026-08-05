// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>One sphere of a floating body, in the body's own frame.</summary>
/// <param name="Offset">Where its centre sits relative to the body's origin.</param>
/// <param name="Radius">How big it is, in metres.</param>
/// <remarks>
///     <para>
///         <b>A volumetric approximation cheap enough to run on every crate in a river</b> —
///         [35 § D10](../../docs/plan/35-water.md#d10-buoyancy-is-pontoons-over-jolt-evaluated-at-the-fixed-steps-water-time),
///         and it is the reference's model because it is the right one. Three or four spheres tell a
///         solver everything it needs about how a hull sits: where it floats, how it pitches when
///         somebody stands at the bow, and how it rights itself.
///     </para>
///     <para>
///         ⚠ <b>The radius is the whole of the shape and it is not the collision shape.</b> A pontoon
///         is a displacement volume, not a collider — a barge is four large spheres and a canoe is six
///         small ones along its keel, and matching them to the hull mesh is how a boat ends up
///         floating a hand's width too high.
///     </para>
/// </remarks>
public readonly record struct BuoyancyPontoon(Vector3 Offset, float Radius) {
    /// <summary>How much water it displaces when it is entirely under the surface, in m³.</summary>
    public float Volume => 4f / 3f * MathF.PI * Radius * Radius * Radius;
}

/// <summary>How a body floats.</summary>
/// <remarks>
///     Unreal's <c>UBuoyancyComponent</c>, minus the pieces that belong elsewhere: the pontoon list is
///     the caller's span, the FX flags are <c>Vixen.Vfx</c>'s, and the boat that steers with these
///     forces is [28 § Vixen.Gameplay.Movement](../../docs/plan/28-gameplay-framework.md)'s.
/// </remarks>
public readonly record struct BuoyancySettings {
    /// <summary>A multiplier on the displaced weight.</summary>
    /// <remarks>
    ///     One is Archimedes. Above one is the fudge every boat needs, because a hull's pontoons are
    ///     an approximation of a shape and a designer wants the waterline where they drew it.
    /// </remarks>
    public float Coefficient { get; init; }

    /// <summary>First-order damping on the submerged part's velocity, per second.</summary>
    /// <remarks>
    ///     What stops a released crate oscillating for ever. A restoring force with no losses is a
    ///     pendulum, and the symptom is a boat that never settles however long the scene runs.
    /// </remarks>
    public float Damping { get; init; }

    /// <summary>Second-order damping, per metre of travel.</summary>
    /// <remarks>
    ///     The term that dominates at speed, which is what makes a boat have a top speed without one
    ///     being typed. Applied to the square of the velocity, in its own direction.
    /// </remarks>
    public float QuadraticDamping { get; init; }

    /// <summary>The most upward force one pontoon may produce, in newtons. Zero for no limit.</summary>
    /// <remarks>
    ///     ⚠ <b>The knob that stops a body launching when it is pushed deep.</b> A pontoon a metre
    ///     under produces the force of a metre of water, which for a large sphere is a great deal —
    ///     and a crate dropped from a height would leave the lake faster than it arrived.
    /// </remarks>
    public float MaximumForce { get; init; }

    /// <summary>How hard a current drags a submerged pontoon along with it, per second.</summary>
    /// <remarks>
    ///     ⚠ <b>A drag towards the flow rather than a force in its direction.</b> A constant push
    ///     accelerates a raft for ever and it ends the river faster than the water; a drag brings it
    ///     to the water's own speed and leaves it there, which is what a river does.
    /// </remarks>
    public float FlowDrag { get; init; }

    /// <summary>A crate: Archimedes exactly, damped enough to settle in a couple of seconds.</summary>
    public static BuoyancySettings Default =>
        new() {
            Coefficient = 1f,
            Damping = 3f,
            QuadraticDamping = 0.4f,
            MaximumForce = 0f,
            FlowDrag = 2f
        };
}

/// <summary>What one pontoon does to the body this step.</summary>
/// <param name="Force">The force, in newtons, in world space.</param>
/// <param name="Position">Where it applies, in world space — which is what makes a body pitch.</param>
/// <param name="Submerged">How much of the sphere is under the surface, 0…1.</param>
/// <param name="SurfaceHeight">Where the surface was over the pontoon, in world units.</param>
public readonly record struct BuoyancyForce(
    Vector3 Force,
    Vector3 Position,
    float Submerged,
    float SurfaceHeight
) {
    /// <summary>A pontoon in the air.</summary>
    public static BuoyancyForce None => default;
}

/// <summary>
///     Pontoons floating on the one surface everything else reads.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D10](../../docs/plan/35-water.md#d10-buoyancy-is-pontoons-over-jolt-evaluated-at-the-fixed-steps-water-time).</b>
///         Per fixed step, per pontoon: ask the evaluator where the surface is, work out how much of
///         the sphere is under it, and produce an upward force at the pontoon's world position, a drag
///         opposing its relative velocity, and a pull towards the flow.
///     </para>
///     <para>
///         ⚠ <b>It reads the simulation's water time and never a frame time.</b> A buoyancy force
///         computed from an interpolated render-time surface is a force that changes when the frame
///         rate does, and in a networked game that is a client and a server disagreeing about where a
///         boat is. That is [16](../../docs/plan/16-networking.md)'s determinism requirement applied to
///         a force, and it is why the evaluator's explicit <c>waterTime</c> is not a stylistic choice.
///     </para>
///     <para>
///         ⚠ <b>Jolt has a buoyancy impulse of its own and it is deliberately not used.</b> It takes a
///         <em>plane</em>, which is exactly the approximation a wave surface is not — and using it
///         would put a second definition of the water surface inside the physics engine, where the
///         seam test cannot reach it.
///     </para>
///     <para>
///         <b>Zero-allocation and pure</b>, because the answer to "how many floating crates" should be
///         "as many as you like" — and because a rollback re-simulating six ticks needs this to be a
///         function of its arguments and nothing else.
///     </para>
/// </remarks>
public static class Buoyancy {
    /// <summary>Fresh water, in kilograms per cubic metre.</summary>
    /// <remarks>
    ///     ⚠ <b>Not configurable, on <see cref="GerstnerWave.Gravity" />'s reasoning.</b> A density
    ///     the client and the server would have to agree about without either sending it is a density
    ///     that eventually disagrees; a project that wants a boat to ride higher raises
    ///     <see cref="BuoyancySettings.Coefficient" />, which is per body and is authored.
    /// </remarks>
    public const float Density = 1000f;

    /// <summary>How much of a sphere is under a surface, 0…1.</summary>
    /// <param name="radius">The sphere's radius, in metres.</param>
    /// <param name="centre">Where its centre is, in world units.</param>
    /// <param name="surface">Where the surface is, in world units.</param>
    /// <returns>The fraction of its volume that is submerged.</returns>
    /// <remarks>
    ///     <para>
    ///         The exact spherical cap and not a linear ramp on the depth. A linear approximation is
    ///         wrong by a third at half submersion, which is precisely where a floating body rests —
    ///         so a crate tuned against it sits at a waterline the arithmetic never predicted.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Monotone in the depth and exactly 0 and 1 at the ends</b>, which the property tests
    ///         assert: a fraction that overshot would make a body that is pushed deeper produce
    ///         <em>less</em> lift, and the solver has no way back from that.
    ///     </para>
    /// </remarks>
    public static float SubmergedFraction(float radius, float centre, float surface) {
        if (!(radius > 0f)) {
            return surface >= centre ? 1f : 0f;
        }

        // How far the surface is above the sphere's lowest point, clamped to the sphere.
        var depth = Math.Clamp(surface - (centre - radius), 0f, 2f * radius);

        // The spherical cap: V = π d² (3r − d) / 3, over the sphere's 4πr³/3.
        //
        // ⚠ Saturated, and not for tidiness. The expression is exactly 1 at full submersion only in
        // exact arithmetic; in floats it overshoots by an ulp or two — measured at 1.0000001 for a
        // 2.2 m sphere — and a fraction above one is lift above Archimedes. The property test found
        // it, which is what a property test is for.
        return WaterMath.Saturate(depth * depth * ((3f * radius) - depth) / (4f * radius * radius * radius));
    }

    /// <summary>What one pontoon does to a body this step.</summary>
    /// <param name="evaluator">The one definition of where the surface is.</param>
    /// <param name="pontoon">The sphere.</param>
    /// <param name="centre">Where its centre is, in world space.</param>
    /// <param name="velocity">How fast that point of the body is moving, in world space.</param>
    /// <param name="gravity">The downward acceleration, in m/s². Negative.</param>
    /// <param name="settings">How the body floats.</param>
    /// <param name="waterTime">The <em>simulation's</em> water time, in seconds.</param>
    /// <param name="ripples">A simulation to add, or null for the closed form alone.</param>
    /// <returns>The force and where it applies.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The centre is passed in rather than read off the pontoon</b>, because a pontoon is
    ///         authored in the body's frame and placing it is <see cref="Solve" />'s transform. What
    ///         comes back applies at that point, which is what makes a body pitch when one end is
    ///         lifted — a force at the centre of mass would make a boat that bobs and never rolls.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every term is scaled by the submerged fraction</b>, drag included. A pontoon
    ///         breaking the surface that kept its full drag would brake a boat in mid-air, which reads
    ///         as a wake that grips.
    ///     </para>
    /// </remarks>
    public static BuoyancyForce Evaluate(
        in WaterEvaluator evaluator,
        in BuoyancyPontoon pontoon,
        Vector3 centre,
        Vector3 velocity,
        float gravity,
        in BuoyancySettings settings,
        float waterTime,
        IWaterRipples? ripples = null
    ) {
        var surface = evaluator.Sample(new(centre.X, centre.Z), waterTime, ripples);

        if (!surface.IsWet) {
            return BuoyancyForce.None;
        }

        var submerged = SubmergedFraction(pontoon.Radius, centre.Y, surface.Height);

        if (submerged <= 0f) {
            return new(Vector3.Zero, centre, 0f, surface.Height);
        }

        // Archimedes: the weight of the water displaced, upward. Gravity is negative, so −gravity is
        // the magnitude, and the coefficient is the per-body fudge a hull's approximation needs.
        var displaced = pontoon.Volume * submerged;
        var lift = Density * -gravity * displaced * MathF.Max(settings.Coefficient, 0f);

        if (settings.MaximumForce > 0f) {
            lift = MathF.Min(lift, settings.MaximumForce);
        }

        var force = new Vector3(0f, lift, 0f);

        // The mass of the water this pontoon has moved aside, which is what both damping terms are
        // scaled by: a submerged sphere drags in proportion to the water it is pushing, not to a
        // number with no units.
        var displacedMass = Density * displaced;

        // Drag, against the velocity relative to the water. The flow is horizontal — a current does
        // not lift — so the vertical term is the body's own motion through still water.
        var relative = velocity - new Vector3(surface.Flow.X, 0f, surface.Flow.Y);
        var speed = relative.Length();

        if (speed > 0f) {
            // Linear: a rate per second against the displaced mass, so `Damping` is in s⁻¹ and a
            // value of 3 means the water takes about a third of a second off a drifting body.
            var linear = MathF.Max(settings.Damping, 0f) * displacedMass * speed;

            // Quadratic: the real form, ½ρCdAv², over the sphere's own cross-section. This is what
            // gives a boat a top speed without one being typed.
            var area = MathF.PI * pontoon.Radius * pontoon.Radius * submerged;
            var quadratic = 0.5f * Density * MathF.Max(settings.QuadraticDamping, 0f) * area * speed * speed;

            force -= relative / speed * (linear + quadratic);
        }

        // And the current, as a drag towards the water's own speed rather than a push in its
        // direction — see BuoyancySettings.FlowDrag.
        if (settings.FlowDrag > 0f && surface.Flow != Vector2.Zero) {
            var wanted = new Vector3(surface.Flow.X, 0f, surface.Flow.Y);
            var along = new Vector3(velocity.X, 0f, velocity.Z);

            force += (wanted - along) * (settings.FlowDrag * displacedMass);
        }

        return new(force, centre, submerged, surface.Height);
    }

    /// <summary>Every pontoon of one body, in one pass.</summary>
    /// <param name="evaluator">The one definition of where the surface is.</param>
    /// <param name="pontoons">The spheres, in the body's own frame.</param>
    /// <param name="placement">Where the body is, which is what places them.</param>
    /// <param name="velocity">How fast the body's centre is moving, in world space.</param>
    /// <param name="gravity">The downward acceleration, in m/s². Negative.</param>
    /// <param name="settings">How the body floats.</param>
    /// <param name="waterTime">The simulation's water time, in seconds.</param>
    /// <param name="into">Where the forces go. As long as <paramref name="pontoons" />.</param>
    /// <param name="ripples">A simulation to add, or null for the closed form alone.</param>
    /// <returns>How many pontoons touched water.</returns>
    /// <exception cref="ArgumentException"><paramref name="into" /> is too short.</exception>
    /// <remarks>
    ///     ⚠ <b>Every pontoon reads the same velocity, which ignores the body's rotation.</b> A
    ///     spinning raft's bow and stern move at different speeds, and the difference is a damping
    ///     torque this does not produce. It is a stated approximation rather than an oversight: the
    ///     angular part needs an inertia tensor, which is the physics engine's and not the kernel's,
    ///     and the join is where it belongs. Pass a per-pontoon velocity to
    ///     <see cref="Evaluate" /> directly to have it.
    /// </remarks>
    public static int Solve(
        in WaterEvaluator evaluator,
        ReadOnlySpan<BuoyancyPontoon> pontoons,
        in Matrix4x4 placement,
        Vector3 velocity,
        float gravity,
        in BuoyancySettings settings,
        float waterTime,
        Span<BuoyancyForce> into,
        IWaterRipples? ripples = null
    ) {
        if (into.Length < pontoons.Length) {
            throw new ArgumentException(
                $"{pontoons.Length} pontoons need {pontoons.Length} slots, not {into.Length}.",
                nameof(into)
            );
        }

        var wet = 0;

        for (var index = 0; index < pontoons.Length; index++) {
            into[index] = Evaluate(
                in evaluator,
                in pontoons[index],
                Matrix4x4.TransformPosition(pontoons[index].Offset, in placement),
                velocity,
                gravity,
                in settings,
                waterTime,
                ripples
            );

            if (into[index].Submerged > 0f) {
                wet++;
            }
        }

        return wet;
    }

    /// <summary>Where a body of a given mass floats, given its pontoons, in metres of submersion.</summary>
    /// <param name="pontoons">The spheres, in the body's own frame.</param>
    /// <param name="mass">How heavy the body is, in kilograms.</param>
    /// <param name="settings">How it floats.</param>
    /// <returns>The submerged volume at rest, in m³.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The analytic answer a convergence test measures the solver against</b> —
    ///         [§ Part 4]'s "a body released above the surface settles to a rest height matching the
    ///         analytic displacement of its pontoons". At rest the lift equals the weight, so the
    ///         displaced volume is the mass over the density, and the coefficient scales it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It answers a volume and not a height, because the height depends on which pontoons
    ///         it is spread across.</b> A body whose displacement exceeds its pontoons' total volume
    ///         does not float at all, and that is the number to check rather than a depth that would
    ///         have to be solved for.
    ///     </para>
    /// </remarks>
    public static float RestDisplacement(
        ReadOnlySpan<BuoyancyPontoon> pontoons,
        float mass,
        in BuoyancySettings settings
    ) {
        var coefficient = MathF.Max(settings.Coefficient, 0f);

        if (!(coefficient > 0f) || !(mass > 0f)) {
            return 0f;
        }

        var wanted = mass / (Density * coefficient);
        var available = 0f;

        foreach (var pontoon in pontoons) {
            available += pontoon.Volume;
        }

        // ⚠ Clamped to what the pontoons actually hold: a body heavier than the water it can displace
        // sinks, and reporting the volume it *would* have needed reads as a body that floats.
        return MathF.Min(wanted, available);
    }
}
