// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>What makes a particle emit particles.</summary>
public enum VfxEmitEvent {
    /// <summary>When it is born. A shell that trails sparks from the moment it leaves the tube.</summary>
    Birth,

    /// <summary>When it dies. A firework: the shell reaches the top, expires, and bursts.</summary>
    /// <remarks>
    ///     Needs <see cref="VfxSystem.RecordDeaths" /> on the source, because by the time a step has
    ///     finished the dead particle's slot belongs to a survivor and there is nothing left to ask.
    /// </remarks>
    Death,

    /// <summary>Every so often while it lives. A trail.</summary>
    Trail
}

/// <summary>
///     One system's particles emitting into another's.
/// </summary>
/// <remarks>
///     <para>
///         <b>Two systems rather than one system with two kinds of particle.</b> A shell and its
///         sparks have different lifetimes, different forces, different renderers and different
///         capacities — they are two effects that happen to be connected, and the connection is this
///         object. Folding them into one graph would mean every operation carrying a test for which
///         kind of particle it was looking at, which is the branch per particle this module's whole
///         storage design exists to avoid.
///     </para>
///     <para>
///         <b>The child's initializers are an offset, not a replacement.</b> A child is initialized by
///         its own graph and then moved to where its parent was, so an initializer that scatters
///         particles through a sphere scatters them around the parent rather than around the origin.
///         That is what an author writing "burst in a sphere" for a sub-emitter means, and it is the
///         only reading under which the child graph is worth authoring separately at all.
///     </para>
///     <para>
///         <b>Step this after both systems.</b> A child spawned here waits until the next step to be
///         updated, which is exactly what happens to a particle a spawner produces —
///         <see cref="VfxSystem.Step" /> updates before it spawns. Emitting between the two steps
///         instead would age a child on the step it was born, and the two ways a particle can come
///         into existence would disagree about how old it is.
///     </para>
///     <para>
///         <b>It is as deterministic as everything else here.</b> A child's randomness comes from the
///         identifier the target buffer hands it, and the target hands them out in order. Two runs
///         with the same steps produce the same children, which is what a replay and a golden image
///         both need.
///     </para>
/// </remarks>
public sealed class VfxSubEmitter {
    readonly VfxSystem source;
    readonly VfxSystem target;

    /// <summary>Connects a system's particles to another system.</summary>
    /// <param name="source">Whose particles trigger the emission.</param>
    /// <param name="target">Where the emitted particles go.</param>
    /// <param name="trigger">What triggers it.</param>
    /// <param name="count">How many children one event produces.</param>
    /// <param name="interval">For <see cref="VfxEmitEvent.Trail" />: seconds between children.</param>
    /// <exception cref="ArgumentNullException"><paramref name="source" /> or <paramref name="target" /> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="source" /> and <paramref name="target" /> are the same system.</exception>
    public VfxSubEmitter(
        VfxSystem source,
        VfxSystem target,
        VfxEmitEvent trigger = VfxEmitEvent.Death,
        int count = 1,
        float interval = 0.1f
    ) {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (ReferenceEquals(source, target)) {
            // Not a subtle bug to leave in: a system emitting into itself would have this walking the
            // very particles it is appending, and the answer would depend on where the buffer happened
            // to resize.
            throw new ArgumentException(
                "A system cannot be its own sub-emitter's target. Give the children a system of their own.",
                nameof(target)
            );
        }

        this.source = source;
        this.target = target;

        Trigger = trigger;
        Count = count;
        Interval = interval;
    }

    /// <summary>What triggers the emission.</summary>
    public VfxEmitEvent Trigger { get; }

    /// <summary>How many children one event produces.</summary>
    public int Count { get; }

    /// <summary>Seconds between children, for a trail. Zero or less emits every step.</summary>
    public float Interval { get; }

    /// <summary>How much of a parent's velocity a child starts with, added to its own.</summary>
    /// <remarks>
    ///     Added rather than replacing, for the same reason the position is an offset: the child's
    ///     graph says how its particles scatter and this says what they were thrown from. One is
    ///     usually right for a trail that keeps drifting with the thing that shed it; zero is usually
    ///     right for a burst.
    /// </remarks>
    public float InheritVelocity { get; set; }

    /// <summary>How many children the last <see cref="Step" /> produced.</summary>
    public int LastEmitted { get; private set; }

    /// <summary>How many it could not, because the target was full.</summary>
    /// <remarks>
    ///     Reported rather than logged, exactly as <see cref="VfxSystem.LastRefused" /> is: a
    ///     sub-emitter at its target's capacity is a normal state for a dense effect and an authoring
    ///     mistake for a sparse one, and only the author can tell which.
    /// </remarks>
    public int LastRefused { get; private set; }

    /// <summary>Emits whatever this step's events call for.</summary>
    /// <param name="deltaTime">The step the two systems just took. Zero or less does nothing.</param>
    public void Step(float deltaTime) {
        LastEmitted = 0;
        LastRefused = 0;

        if (deltaTime <= 0f) {
            return;
        }

        switch (Trigger) {
            case VfxEmitEvent.Birth: {
                var positions = source.Particles.Position;
                var velocities = source.Particles.Velocity;

                for (var index = source.FirstSpawned; index < source.Particles.Count; index++) {
                    Emit(positions[index], Velocity(velocities, index), Count);
                }

                break;
            }

            case VfxEmitEvent.Death: {
                // No velocity: the particle is gone and only its position was kept. Recording the
                // velocity too would double the graveyard for a thing a burst almost never wants —
                // a firework's sparks fly outwards, not onwards.
                foreach (var position in source.Deaths) {
                    Emit(position, Vector3.Zero, Count);
                }

                break;
            }

            case VfxEmitEvent.Trail: {
                Trails(deltaTime);

                break;
            }

            default: {
                break;
            }
        }
    }

    /// <summary>One child per parent per interval, from the parent's own age.</summary>
    /// <remarks>
    ///     <para>
    ///         The interval is counted off the age rather than from a per-particle timer, which is why
    ///         a trail needs no storage of its own: a particle that has just crossed a multiple of the
    ///         interval is one whose age and whose age-a-step-ago fall either side of it. That is
    ///         exact, it costs nothing per particle, and it stays right when particles are reordered
    ///         by a death — which a per-slot timer would not.
    ///     </para>
    ///     <para>
    ///         A newly spawned particle has an age of zero and a previous age below it, so it sheds
    ///         its first child straight away. A trail that started an interval late would leave a gap
    ///         between the parent and the trail behind it, which is the artefact this shape avoids.
    ///     </para>
    /// </remarks>
    void Trails(float deltaTime) {
        var particles = source.Particles;

        if (!particles.Has(VfxAttribute.Age)) {
            // Nothing to count intervals against. A graph whose particles have no age is one whose
            // particles are immortal, and a trail off one would be an unbounded emission.
            return;
        }

        var ages = particles.Age;
        var positions = particles.Position;
        var velocities = particles.Velocity;

        for (var index = 0; index < particles.Count; index++) {
            if (!Due(ages[index], deltaTime)) {
                continue;
            }

            Emit(positions[index], Velocity(velocities, index), Count);
        }
    }

    bool Due(float age, float deltaTime) =>
        Interval <= 0f || MathF.Floor(age / Interval) > MathF.Floor((age - deltaTime) / Interval);

    Vector3 Velocity(Span<Vector3> velocities, int index) =>
        InheritVelocity == 0f || velocities.IsEmpty ? Vector3.Zero : velocities[index] * InheritVelocity;

    /// <summary>Spawns a run of children about a point, and moves them onto it.</summary>
    void Emit(Vector3 position, Vector3 velocity, int count) {
        var particles = target.Particles;
        var added = particles.Spawn(count, out var first);

        LastEmitted += added;
        LastRefused += count - added;

        if (added == 0) {
            return;
        }

        VfxSimulation.Initialize(particles, target.Graph.Initializers, first, added, target.Seed);

        if (particles.Has(VfxAttribute.Position)) {
            var positions = particles.Position;

            for (var index = first; index < first + added; index++) {
                positions[index] += position;
            }
        }

        if (velocity != Vector3.Zero && particles.Has(VfxAttribute.Velocity)) {
            var velocities = particles.Velocity;

            for (var index = first; index < first + added; index++) {
                velocities[index] += velocity;
            }
        }
    }
}
