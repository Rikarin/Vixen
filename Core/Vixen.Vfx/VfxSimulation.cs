// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;

namespace Vixen.Vfx;

/// <summary>
///     The CPU backend: it runs a compiled graph over a particle buffer.
/// </summary>
/// <remarks>
///     <para>
///         <b>One operation across every particle, not every operation across one particle.</b> That
///         ordering is the whole performance argument and it is also what makes the GPU backend a
///         translation rather than a redesign. Sweeping per operation means the opcode is dispatched
///         once per frame instead of once per particle, each sweep touches one or two attribute arrays
///         end to end, and the inner loop has no branch in it — which is what a compute shader's
///         invocation looks like from the inside anyway.
///     </para>
///     <para>
///         The other order — walk the particles, run the graph on each — would put a switch inside the
///         hot loop and read every attribute of a particle to change one of them. It is the order that
///         reads better and it is the wrong one.
///     </para>
///     <para>
///         <b>Randomness is drawn from the identifier, never from the index.</b> A particle keeps its
///         identifier for life and its slot only until something ahead of it dies, so an operation
///         that hashed the slot would give a particle new random values partway through its life.
///     </para>
/// </remarks>
public static class VfxSimulation {
    /// <summary>Applies the initializers to a run of newly spawned particles.</summary>
    /// <param name="buffer">The particles.</param>
    /// <param name="operations">The graph's initializers.</param>
    /// <param name="first">The first new particle.</param>
    /// <param name="count">How many.</param>
    /// <param name="seed">The system instance's seed.</param>
    public static void Initialize(ParticleBuffer buffer, ReadOnlySpan<VfxOperation> operations, int first, int count, uint seed) {
        ArgumentNullException.ThrowIfNull(buffer);

        if (count <= 0) {
            return;
        }

        var identifiers = buffer.Identifier;

        foreach (var operation in operations) {
            switch (operation.Opcode) {
                case VfxOpcode.SetPosition: {
                    buffer.Position.Slice(first, count).Fill(new(operation.A.X, operation.A.Y, operation.A.Z));

                    break;
                }

                case VfxOpcode.PositionInSphere: {
                    var centre = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);
                    var radius = operation.A.W;
                    var positions = buffer.Position;

                    for (var index = first; index < first + count; index++) {
                        var identifier = identifiers[index];

                        // The cube root is what makes it uniform by volume. Scaling a direction by a
                        // uniform radius piles two thirds of the particles into the outer third of
                        // the sphere, which reads as a shell rather than as a ball.
                        var fraction = MathF.Cbrt(VfxRandom.Value(identifier, seed, operation.Salt + 2));

                        positions[index] = centre + (VfxRandom.Direction(identifier, seed, operation.Salt) * radius * fraction);
                    }

                    break;
                }

                case VfxOpcode.PositionInBox: {
                    var minimum = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);
                    var maximum = new Vector3(operation.B.X, operation.B.Y, operation.B.Z);
                    var positions = buffer.Position;

                    for (var index = first; index < first + count; index++) {
                        var fraction = VfxRandom.Value3(identifiers[index], seed, operation.Salt);

                        positions[index] = new(
                            minimum.X + ((maximum.X - minimum.X) * fraction.X),
                            minimum.Y + ((maximum.Y - minimum.Y) * fraction.Y),
                            minimum.Z + ((maximum.Z - minimum.Z) * fraction.Z)
                        );
                    }

                    break;
                }

                case VfxOpcode.SetVelocity: {
                    buffer.Velocity.Slice(first, count).Fill(new(operation.A.X, operation.A.Y, operation.A.Z));

                    break;
                }

                case VfxOpcode.VelocityRandomDirection: {
                    var velocities = buffer.Velocity;

                    for (var index = first; index < first + count; index++) {
                        var identifier = identifiers[index];
                        var speed = VfxRandom.Range(identifier, seed, operation.Salt + 2, operation.A.X, operation.A.Y);

                        velocities[index] = VfxRandom.Direction(identifier, seed, operation.Salt) * speed;
                    }

                    break;
                }

                case VfxOpcode.VelocityInCone: {
                    var axis = Vector3.Normalize(new(operation.A.X, operation.A.Y, operation.A.Z));
                    var velocities = buffer.Velocity;

                    for (var index = first; index < first + count; index++) {
                        var identifier = identifiers[index];
                        var speed = VfxRandom.Range(identifier, seed, operation.Salt + 2, operation.B.X, operation.B.Y);

                        velocities[index] = Cone(axis, operation.A.W, identifier, seed, operation.Salt) * speed;
                    }

                    break;
                }

                case VfxOpcode.SetLifetime: {
                    Randomize(buffer.Lifetime, identifiers, first, count, seed, operation);

                    break;
                }

                case VfxOpcode.SetSize: {
                    Randomize(buffer.Size, identifiers, first, count, seed, operation);

                    break;
                }

                case VfxOpcode.SetRotation: {
                    Randomize(buffer.Rotation, identifiers, first, count, seed, operation);

                    break;
                }

                case VfxOpcode.SetAngularVelocity: {
                    Randomize(buffer.AngularVelocity, identifiers, first, count, seed, operation);

                    break;
                }

                case VfxOpcode.SetColour: {
                    buffer.Colour.Slice(first, count).Fill(operation.A);

                    break;
                }

                case VfxOpcode.SetCustom: {
                    var values = buffer.Custom(operation.Slot);
                    var width = buffer.Lanes(operation.Slot);

                    for (var index = first; index < first + count; index++) {
                        for (var lane = 0; lane < width; lane++) {
                            values[(index * width) + lane] = Lane(operation.A, lane);
                        }
                    }

                    break;
                }

                case VfxOpcode.RandomCustom: {
                    var values = buffer.Custom(operation.Slot);
                    var width = buffer.Lanes(operation.Slot);

                    for (var index = first; index < first + count; index++) {
                        var particle = identifiers[index];

                        for (var lane = 0; lane < width; lane++) {
                            // A salt per lane, so a three-lane attribute is three unrelated draws
                            // rather than one value in three places. The stride between operations is
                            // four, which is why four lanes is the widest an attribute can be.
                            values[(index * width) + lane] = VfxRandom.Range(
                                particle,
                                seed,
                                operation.Salt + (uint)lane,
                                Lane(operation.A, lane),
                                Lane(operation.B, lane)
                            );
                        }
                    }

                    break;
                }

                default: {
                    // An updater in the initializer list. The compiler does not refuse it, because
                    // "apply gravity once at birth" is a legitimate thing to author.
                    Apply(buffer, operation, first, count, 0f, 0f);

                    break;
                }
            }
        }

        if (buffer.Has(VfxAttribute.Age)) {
            buffer.Age.Slice(first, count).Clear();
        }
    }

    /// <summary>Advances every live particle by one step.</summary>
    /// <param name="buffer">The particles.</param>
    /// <param name="operations">The graph's updaters.</param>
    /// <param name="deltaTime">How much time passed.</param>
    /// <param name="time">
    ///     How long the system has been running, at the start of this step. Only a field that drifts
    ///     reads it.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         Ageing happens first and reaping last, so a particle is updated on the step it dies and
    ///         not after it. Doing it the other way round leaves one step of an effect drawn with a
    ///         particle that should already have gone.
    ///     </para>
    ///     <para>
    ///         <b>The clock is handed in, never read.</b> A drifting noise field needs to know when it
    ///         is, and the moment this function asked an ambient clock for that, two systems with the
    ///         same seed and the same steps would stop being identical — which is the property the
    ///         whole module is arranged around.
    ///     </para>
    /// </remarks>
    /// <param name="graveyard">
    ///     Filled with the position of each particle that died, for a sub-emitter to burst from.
    ///     Empty by default, which records nothing and costs nothing.
    /// </param>
    /// <returns>How many particles died.</returns>
    public static int Update(
        ParticleBuffer buffer,
        ReadOnlySpan<VfxOperation> operations,
        float deltaTime,
        float time = 0f,
        Span<Vector3> graveyard = default
    ) {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Count == 0 || deltaTime <= 0f) {
            return 0;
        }

        if (buffer.Has(VfxAttribute.Age)) {
            var ages = buffer.Age;

            for (var index = 0; index < buffer.Count; index++) {
                ages[index] += deltaTime;
            }
        }

        foreach (var operation in operations) {
            Apply(buffer, operation, 0, buffer.Count, deltaTime, time);
        }

        return buffer.Reap(graveyard);
    }

    /// <summary>Advances every live particle by one step, across the scheduler's threads.</summary>
    /// <param name="buffer">The particles.</param>
    /// <param name="operations">The graph's updaters.</param>
    /// <param name="deltaTime">How much time passed.</param>
    /// <param name="time">How long the system has been running, at the start of this step.</param>
    /// <param name="scheduler">Where the work runs.</param>
    /// <param name="batchSize">How many particles one work item covers, or zero for a derived size.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer" /> or <paramref name="scheduler" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>One dispatch for the whole graph, not one per operation.</b> Scheduling per operation
    ///         is the shape that matches the serial sweep, and it is the wrong one: a frame with six
    ///         updaters would pay six barriers, and gravity over ten thousand particles is over before
    ///         the barrier is. So a batch runs the <em>whole updater list</em> over its own range of
    ///         particles.
    ///     </para>
    ///     <para>
    ///         <b>That is exactly the serial result, and the reason is worth stating:</b> no operation
    ///         reads another particle. The order of operations within one particle is preserved, and
    ///         the order between particles was never observable. It is also the order the GPU backend
    ///         runs in, which makes this the first place the two agree about more than arithmetic.
    ///     </para>
    ///     <para>
    ///         Ageing goes in the batch for the same reason; reaping does not, because it moves
    ///         particles between slots and changes the count. It runs here, once, after the barrier.
    ///     </para>
    /// </remarks>
    /// <param name="graveyard">
    ///     Filled with the position of each particle that died. Reaping is serial, so this is filled
    ///     in the same order and by the same code as the single-threaded sweep's.
    /// </param>
    /// <returns>How many particles died.</returns>
    public static int Update(
        ParticleBuffer buffer,
        VfxOperation[] operations,
        float deltaTime,
        float time,
        JobScheduler scheduler,
        int batchSize = 0,
        Span<Vector3> graveyard = default
    ) {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(scheduler);

        if (buffer.Count == 0 || deltaTime <= 0f) {
            return 0;
        }

        // Four batches a thread, which is what the scheduler would pick for itself; named here
        // because the length being scheduled is a batch count rather than a particle count, and the
        // scheduler cannot work that out from a number it was handed already divided.
        var batch = batchSize > 0
            ? batchSize
            : Math.Max(64, (buffer.Count / Math.Max(1, scheduler.WorkerCount * 4)) + 1);

        var batches = (buffer.Count + batch - 1) / batch;

        scheduler.ScheduleParallel(
            new Sweep {
                Buffer = buffer,
                Operations = operations,
                Count = buffer.Count,
                Batch = batch,
                DeltaTime = deltaTime,
                Time = time
            },
            batches,
            1
        ).Complete();

        return buffer.Reap(graveyard);
    }

    /// <summary>One batch of particles, run through the whole updater list.</summary>
    /// <remarks>
    ///     The index is a <em>batch</em> and not a particle, which is what keeps the opcode switch out
    ///     of the per-particle path: dispatching per particle would run it ten thousand times a frame
    ///     to answer the same question. The scheduler is told a batch size of one for that reason —
    ///     the division has already happened.
    /// </remarks>
    readonly struct Sweep : IJobParallelFor {
        public required ParticleBuffer Buffer { get; init; }
        public required VfxOperation[] Operations { get; init; }
        public required int Count { get; init; }
        public required int Batch { get; init; }
        public required float DeltaTime { get; init; }
        public required float Time { get; init; }

        public void Execute(int index) {
            var first = index * Batch;
            var count = Math.Min(Batch, Count - first);

            if (count <= 0) {
                return;
            }

            if (Buffer.Has(VfxAttribute.Age)) {
                var ages = Buffer.Age;

                for (var particle = first; particle < first + count; particle++) {
                    ages[particle] += DeltaTime;
                }
            }

            foreach (var operation in Operations) {
                Apply(Buffer, operation, first, count, DeltaTime, Time);
            }
        }
    }

    /// <summary>One updater over a run of particles.</summary>
    static void Apply(ParticleBuffer buffer, VfxOperation operation, int first, int count, float deltaTime, float time = 0f) {
        switch (operation.Opcode) {
            case VfxOpcode.Integrate: {
                var positions = buffer.Position;
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    positions[index] += velocities[index] * deltaTime;
                }

                break;
            }

            case VfxOpcode.Gravity: {
                var step = new Vector3(operation.A.X, operation.A.Y, operation.A.Z) * deltaTime;
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    velocities[index] += step;
                }

                break;
            }

            case VfxOpcode.Drag: {
                // Exponential, so the result does not depend on the step size. The linear form
                // `v *= 1 - k dt` is the same to first order and goes negative at a large step,
                // which turns a strong drag into a particle that reverses.
                var retained = MathF.Exp(-operation.A.X * deltaTime);
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    velocities[index] *= retained;
                }

                break;
            }

            case VfxOpcode.Rotate: {
                var rotations = buffer.Rotation;
                var angular = buffer.AngularVelocity;

                for (var index = first; index < first + count; index++) {
                    rotations[index] += angular[index] * deltaTime;
                }

                break;
            }

            case VfxOpcode.SizeOverLife: {
                var sizes = buffer.Size;
                var ages = buffer.Age;
                var lifetimes = buffer.Lifetime;

                for (var index = first; index < first + count; index++) {
                    sizes[index] = float.Lerp(operation.A.X, operation.A.Y, Fraction(ages[index], lifetimes[index]));
                }

                break;
            }

            case VfxOpcode.ColourOverLife: {
                var colours = buffer.Colour;
                var ages = buffer.Age;
                var lifetimes = buffer.Lifetime;

                for (var index = first; index < first + count; index++) {
                    colours[index] = Vector4.Lerp(operation.A, operation.B, Fraction(ages[index], lifetimes[index]));
                }

                break;
            }

            case VfxOpcode.CustomOverLife: {
                var values = buffer.Custom(operation.Slot);
                var width = buffer.Lanes(operation.Slot);
                var ages = buffer.Age;
                var lifetimes = buffer.Lifetime;

                for (var index = first; index < first + count; index++) {
                    var fraction = Fraction(ages[index], lifetimes[index]);

                    for (var lane = 0; lane < width; lane++) {
                        values[(index * width) + lane] =
                            float.Lerp(Lane(operation.A, lane), Lane(operation.B, lane), fraction);
                    }
                }

                break;
            }

            case VfxOpcode.Attract: {
                var centre = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);
                var strength = operation.A.W * deltaTime;
                var radius = operation.B.X;
                var positions = buffer.Position;
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    var offset = centre - positions[index];
                    var distance = offset.Length();

                    // A particle exactly at the centre has no direction to be pulled in, and
                    // normalizing its zero offset is how a fountain fills with NaNs.
                    if (distance <= 0f) {
                        continue;
                    }

                    velocities[index] += offset / distance * strength * Falloff(distance, radius);
                }

                break;
            }

            case VfxOpcode.Vortex: {
                var centre = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);
                var axis = Vector3.Normalize(new(operation.B.X, operation.B.Y, operation.B.Z));
                var strength = operation.A.W * deltaTime;
                var radius = operation.B.W;
                var positions = buffer.Position;
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    var offset = positions[index] - centre;

                    // The component along the axis contributes nothing to going round it, so it is
                    // taken out before the cross product rather than after — otherwise the swirl
                    // weakens with height above the centre for no reason anybody chose.
                    var radial = offset - (axis * Vector3.Dot(offset, axis));
                    var distance = radial.Length();

                    if (distance <= 0f) {
                        continue;
                    }

                    velocities[index] += Vector3.Cross(axis, radial / distance) * strength * Falloff(distance, radius);
                }

                break;
            }

            case VfxOpcode.Turbulence: {
                var frequency = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);
                var strength = operation.A.W * deltaTime;
                var drift = operation.B.X * time;
                var octaves = Math.Clamp((int)operation.B.Y, 1, 4);
                var positions = buffer.Position;
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    var sample = (positions[index] * frequency) + new Vector3(drift, drift, drift);

                    velocities[index] += VfxNoise.Turbulence(sample, operation.Salt, octaves) * strength;
                }

                break;
            }

            case VfxOpcode.CollidePlane: {
                var normal = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);
                var distance = operation.A.W;
                var bounce = operation.B.X;
                var friction = operation.B.Y;
                var positions = buffer.Position;
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    var depth = Vector3.Dot(normal, positions[index]) - distance;

                    if (depth >= 0f) {
                        continue;
                    }

                    // Put it back on the surface rather than where it would have been had it stopped
                    // there: this runs after the integration, so the particle is already through, and
                    // the frame it spends inside the floor is the frame a viewer notices.
                    positions[index] -= normal * depth;
                    velocities[index] = Bounce(velocities[index], normal, bounce, friction);
                }

                break;
            }

            case VfxOpcode.CollideSphere: {
                var centre = new Vector3(operation.A.X, operation.A.Y, operation.A.Z);
                var radius = operation.A.W;
                var bounce = operation.B.X;
                var friction = operation.B.Y;
                var positions = buffer.Position;
                var velocities = buffer.Velocity;

                for (var index = first; index < first + count; index++) {
                    var offset = positions[index] - centre;
                    var length = offset.Length();

                    if (length >= radius) {
                        continue;
                    }

                    // A particle exactly at the centre has no direction to be pushed out along, and
                    // normalizing its zero offset is how a collider fills a system with NaNs. Up is
                    // an arbitrary choice and an arbitrary choice is what the case needs.
                    var normal = length > 0f ? offset / length : Vector3.UnitY;

                    positions[index] = centre + (normal * radius);
                    velocities[index] = Bounce(velocities[index], normal, bounce, friction);
                }

                break;
            }

            default: {
                // An initializer in the updater list — "reset the colour every step" — which is
                // legitimate and is handled by the initializer switch instead.
                break;
            }
        }
    }

    /// <summary>A velocity reflected off a surface, with a bounce and a friction.</summary>
    /// <param name="velocity">What it was.</param>
    /// <param name="normal">The unit surface normal, pointing away from the surface.</param>
    /// <param name="bounce">How much of the approach speed survives, from nothing to all of it.</param>
    /// <param name="friction">How much of the sliding speed is lost, from none of it to all.</param>
    /// <returns>What it becomes.</returns>
    /// <remarks>
    ///     <para>
    ///         Split into the part along the normal and the part across it, because the two are what
    ///         the two numbers mean: bounce is how much of the approach comes back, friction is how
    ///         much of the slide is scrubbed off. Reflecting the whole vector and scaling it would
    ///         make a ball dropped straight down and one thrown along the floor lose the same
    ///         fraction of their speed, which is not what either word means.
    ///     </para>
    ///     <para>
    ///         Only an approach is reflected. A particle already moving away from the surface is one
    ///         that was pushed out last step and is leaving; bouncing it again would trap it against
    ///         the surface, vibrating, which is the classic way a collider makes a system buzz.
    ///     </para>
    /// </remarks>
    static Vector3 Bounce(Vector3 velocity, Vector3 normal, float bounce, float friction) {
        var approach = Vector3.Dot(velocity, normal);
        var along = velocity - (normal * approach);
        var away = approach < 0f ? -approach * Math.Clamp(bounce, 0f, 1f) : approach;

        return (along * (1f - Math.Clamp(friction, 0f, 1f))) + (normal * away);
    }

    /// <summary>How much of a field's strength reaches a particle this far from it.</summary>
    /// <remarks>
    ///     <para>
    ///         Linear to zero at the radius, and one everywhere when the radius is zero or less. Not
    ///         inverse-square: a real attractor's strength goes to infinity at its centre, which on a
    ///         particle that wanders close enough gives an acceleration large enough to throw it out
    ///         of the scene in one step. An effect wants a region of influence rather than a physical
    ///         law, and the falloff that ends is the one an author can reason about.
    ///     </para>
    ///     <para>
    ///         Squared before the clamp so the edge of the region eases rather than creases — a linear
    ///         falloff has a discontinuous derivative at the radius, and a stream of particles
    ///         crossing it visibly kinks.
    ///     </para>
    /// </remarks>
    static float Falloff(float distance, float radius) {
        if (radius <= 0f) {
            return 1f;
        }

        var remaining = 1f - Math.Clamp(distance / radius, 0f, 1f);

        return remaining * remaining;
    }

    /// <summary>Fills a run of a scalar attribute from a uniform range.</summary>
    static void Randomize(Span<float> values, ReadOnlySpan<uint> identifiers, int first, int count, uint seed, VfxOperation operation) {
        for (var index = first; index < first + count; index++) {
            values[index] = VfxRandom.Range(identifiers[index], seed, operation.Salt, operation.A.X, operation.A.Y);
        }
    }

    /// <summary>One lane of a parameter block, by index rather than by name.</summary>
    /// <remarks>
    ///     A custom attribute has between one and four lanes and the operation does not know which
    ///     until it reads the graph, so the parameters are reached positionally. This is the only
    ///     place in the module that does — every built-in knows what its own <c>A.x</c> means.
    /// </remarks>
    static float Lane(Vector4 value, int lane) => lane switch {
        0 => value.X,
        1 => value.Y,
        2 => value.Z,
        _ => value.W
    };

    /// <summary>How far through its life a particle is, clamped to [0, 1].</summary>
    /// <remarks>
    ///     A lifetime of zero would divide by nothing. It reads as "already over" rather than as an
    ///     infinity, which is what a particle with no lifetime is.
    /// </remarks>
    static float Fraction(float age, float lifetime) => lifetime > 0f ? Math.Clamp(age / lifetime, 0f, 1f) : 1f;

    /// <summary>A direction inside a cone about an axis, uniform over the cap it subtends.</summary>
    static Vector3 Cone(Vector3 axis, float halfAngle, uint identifier, uint seed, uint salt) {
        var cosine = MathF.Cos(halfAngle);

        // Uniform in cos(theta) rather than in theta, for the same reason Direction samples z
        // uniformly: the other way concentrates particles on the cone's axis.
        var z = VfxRandom.Range(identifier, seed, salt, cosine, 1f);
        var azimuth = VfxRandom.Range(identifier, seed, salt + 1, 0f, MathF.Tau);
        var radius = MathF.Sqrt(MathF.Max(0f, 1f - (z * z)));

        // A frame about the axis. The choice of reference only has to avoid being parallel to it.
        var reference = MathF.Abs(axis.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
        var right = Vector3.Normalize(Vector3.Cross(reference, axis));
        var up = Vector3.Cross(axis, right);

        return (right * (radius * MathF.Cos(azimuth))) + (up * (radius * MathF.Sin(azimuth))) + (axis * z);
    }
}
