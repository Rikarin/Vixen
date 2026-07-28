// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

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

                default: {
                    // An updater in the initializer list. The compiler does not refuse it, because
                    // "apply gravity once at birth" is a legitimate thing to author.
                    Apply(buffer, operation, first, count, 0f);

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
    /// <remarks>
    ///     Ageing happens first and reaping last, so a particle is updated on the step it dies and not
    ///     after it. Doing it the other way round leaves one step of an effect drawn with a particle
    ///     that should already have gone.
    /// </remarks>
    public static void Update(ParticleBuffer buffer, ReadOnlySpan<VfxOperation> operations, float deltaTime) {
        ArgumentNullException.ThrowIfNull(buffer);

        if (buffer.Count == 0 || deltaTime <= 0f) {
            return;
        }

        if (buffer.Has(VfxAttribute.Age)) {
            var ages = buffer.Age;

            for (var index = 0; index < buffer.Count; index++) {
                ages[index] += deltaTime;
            }
        }

        foreach (var operation in operations) {
            Apply(buffer, operation, 0, buffer.Count, deltaTime);
        }

        buffer.Reap();
    }

    /// <summary>One updater over a run of particles.</summary>
    static void Apply(ParticleBuffer buffer, VfxOperation operation, int first, int count, float deltaTime) {
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

            default: {
                // An initializer in the updater list — "reset the colour every step" — which is
                // legitimate and is handled by the initializer switch instead.
                break;
            }
        }
    }

    /// <summary>Fills a run of a scalar attribute from a uniform range.</summary>
    static void Randomize(Span<float> values, ReadOnlySpan<uint> identifiers, int first, int count, uint seed, VfxOperation operation) {
        for (var index = first; index < first + count; index++) {
            values[index] = VfxRandom.Range(identifiers[index], seed, operation.Salt, operation.A.X, operation.A.Y);
        }
    }

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
