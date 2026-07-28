// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Vfx;

/// <summary>One corner of a particle's quad, as the vertex buffer holds it.</summary>
/// <param name="Position">Where it is, in world space.</param>
/// <param name="Texture">Its texture coordinate, from (0, 0) to (1, 1) across the quad.</param>
/// <param name="Colour">The particle's colour, the same on all four corners.</param>
/// <remarks>
///     <b>World space, expanded on the CPU.</b> The alternative — uploading a particle per instance
///     and expanding in the vertex shader — is fewer bytes over the bus and is what the GPU path will
///     do. This one exists because it needs nothing from the graphics stack at all, which is what
///     lets the expansion be tested without a device.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParticleVertex(Vector3 Position, Vector2 Texture, Vector4 Colour);

/// <summary>Where the camera is and how it is turned, as the expansion needs it.</summary>
/// <param name="Position">Where the camera is, for depth sorting.</param>
/// <param name="Right">Its right vector, normalised.</param>
/// <param name="Up">Its up vector, normalised.</param>
/// <remarks>
///     Three vectors rather than a view matrix, because that is all the expansion uses and taking the
///     matrix would mean this module had an opinion about which convention it was in.
/// </remarks>
public readonly record struct VfxCamera(Vector3 Position, Vector3 Right, Vector3 Up);

/// <summary>
///     Turns particles into quads.
/// </summary>
/// <remarks>
///     <para>
///         The last step before something is drawn, and the last one that is pure arithmetic — what
///         happens to the vertices afterwards belongs to a render feature in <c>Vixen.Rendering</c>,
///         which is where the pipelines and descriptor sets live. Keeping the expansion here means
///         <c>Vixen.Vfx</c> depends on no graphics at all and every one of these decisions can be
///         checked against a number rather than against a screenshot.
///     </para>
///     <para>
///         <b>Four vertices a particle, not six.</b> The two triangles share an edge, and the index
///         pattern that joins them is the same for every particle in every effect ever — so it is a
///         buffer built once by whoever draws, not two repeated vertices per particle forever.
///         <see cref="WriteQuadIndices" /> is that pattern.
///     </para>
/// </remarks>
public sealed class VfxGeometryBuilder {
    float[] keys = [];
    int[] order = [];

    /// <summary>How many vertices one particle needs.</summary>
    public const int VerticesPerParticle = 4;

    /// <summary>How many indices one particle needs.</summary>
    public const int IndicesPerParticle = 6;

    /// <summary>The order the last <see cref="Build" /> drew the particles in.</summary>
    /// <remarks>
    ///     Exposed because a caller that uploads per-instance data rather than expanded quads needs
    ///     the same order, and recomputing it would be a second sort that could disagree with this one.
    /// </remarks>
    public ReadOnlySpan<int> Order => order.AsSpan(0, Math.Min(order.Length, LastCount));

    /// <summary>How many particles the last <see cref="Build" /> wrote.</summary>
    public int LastCount { get; private set; }

    /// <summary>Writes the index pattern for a number of particles.</summary>
    /// <param name="indices">Where to write. Needs <see cref="IndicesPerParticle" /> per particle.</param>
    /// <param name="particles">How many particles to cover.</param>
    /// <returns>How many indices were written.</returns>
    /// <remarks>
    ///     Two triangles over four corners, wound counter-clockwise: 0-1-2 and 0-2-3. Built once for
    ///     the largest system anybody has and reused by every draw, because it never depends on
    ///     anything but the count.
    /// </remarks>
    public static int WriteQuadIndices(Span<uint> indices, int particles) {
        var written = 0;

        for (var particle = 0; particle < particles; particle++) {
            var corner = (uint)(particle * VerticesPerParticle);

            if (written + IndicesPerParticle > indices.Length) {
                break;
            }

            indices[written++] = corner;
            indices[written++] = corner + 1;
            indices[written++] = corner + 2;
            indices[written++] = corner;
            indices[written++] = corner + 2;
            indices[written++] = corner + 3;
        }

        return written;
    }

    /// <summary>Expands a system's live particles into quads.</summary>
    /// <param name="system">The system.</param>
    /// <param name="camera">Where the camera is and how it is turned.</param>
    /// <param name="vertices">
    ///     Where to write. Needs <see cref="VerticesPerParticle" /> per particle; a shorter span
    ///     writes as many whole particles as fit.
    /// </param>
    /// <returns>How many particles were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The graph declared no renderer.</exception>
    public int Build(VfxSystem system, in VfxCamera camera, Span<ParticleVertex> vertices) {
        ArgumentNullException.ThrowIfNull(system);

        if (system.Graph.Renderer is not { } renderer) {
            throw new InvalidOperationException(
                "The graph declares no renderer, so it has no size and no colour to draw with. A graph that is "
                + "drawn has to say so when it is compiled, because that is what makes it allocate the attributes "
                + "drawing reads."
            );
        }

        var particles = system.Particles;
        var count = Math.Min(particles.Count, vertices.Length / VerticesPerParticle);

        LastCount = count;

        if (count == 0) {
            return 0;
        }

        Sort(particles, renderer, in camera, count);

        var positions = particles.Position;
        var sizes = particles.Size;
        var colours = particles.Colour;
        var hasRotation = particles.Has(VfxAttribute.Rotation);
        var rotations = hasRotation ? particles.Rotation : default;
        var hasVelocity = particles.Has(VfxAttribute.Velocity);
        var velocities = hasVelocity ? particles.Velocity : default;

        for (var slot = 0; slot < count; slot++) {
            var index = order[slot];
            var centre = positions[index];
            var half = sizes[index] * 0.5f;

            var (right, up) = Basis(renderer, in camera, centre, hasVelocity ? velocities[index] : Vector3.Zero, half);

            if (hasRotation) {
                (right, up) = Roll(right, up, rotations[index]);
            }

            var colour = colours[index];
            var corner = slot * VerticesPerParticle;

            // Counter-clockwise from the bottom left, which is what WriteQuadIndices assumes and what
            // every texture atlas in existence is laid out for.
            vertices[corner] = new(centre - right - up, new(0f, 0f), colour);
            vertices[corner + 1] = new(centre + right - up, new(1f, 0f), colour);
            vertices[corner + 2] = new(centre + right + up, new(1f, 1f), colour);
            vertices[corner + 3] = new(centre - right + up, new(0f, 1f), colour);
        }

        return count;
    }

    /// <summary>The half-extent vectors of one particle's quad.</summary>
    /// <remarks>
    ///     <para>
    ///         An aligned billboard keeps one axis fixed — the velocity, or a world axis — and turns
    ///         about it to face the camera. The vector it turns to face is the one from the particle
    ///         to the camera, not the camera's forward: under perspective those differ by more than a
    ///         little at the edges of the view, and using forward makes a wide effect visibly lean.
    ///     </para>
    ///     <para>
    ///         When the fixed axis points straight at the camera there is no way to turn about it and
    ///         the cross product vanishes. Falling back to the camera's own right is what stops a
    ///         streak seen end-on from collapsing to a line of zero width.
    ///     </para>
    /// </remarks>
    static (Vector3 Right, Vector3 Up) Basis(VfxRenderer renderer, in VfxCamera camera, Vector3 centre, Vector3 velocity, float half) {
        switch (renderer.Alignment) {
            case VfxBillboardAlignment.Velocity: {
                var speed = velocity.Length();

                // A particle that is not moving has no direction to be stretched along, so it falls
                // back to facing the camera rather than collapsing to nothing.
                if (speed < 1e-6f) {
                    break;
                }

                var along = velocity / speed;

                return (Across(along, camera, centre) * half, along * (half + (speed * renderer.Stretch * 0.5f)));
            }

            case VfxBillboardAlignment.FixedAxis: {
                var axis = renderer.Axis.LengthSquared() > 1e-12f ? Vector3.Normalize(renderer.Axis) : Vector3.UnitY;

                return (Across(axis, camera, centre) * half, axis * half);
            }

            default: {
                break;
            }
        }

        return (camera.Right * half, camera.Up * half);
    }

    /// <summary>A unit vector square to an axis and to the direction the camera is being faced from.</summary>
    static Vector3 Across(Vector3 axis, in VfxCamera camera, Vector3 centre) {
        var across = Vector3.Cross(axis, camera.Position - centre);

        return across.LengthSquared() > 1e-12f ? Vector3.Normalize(across) : camera.Right;
    }

    /// <summary>Rolls a quad's basis about its own normal.</summary>
    static (Vector3 Right, Vector3 Up) Roll(Vector3 right, Vector3 up, float angle) {
        var cosine = MathF.Cos(angle);
        var sine = MathF.Sin(angle);

        return ((right * cosine) + (up * sine), (up * cosine) - (right * sine));
    }

    /// <summary>Fills the draw order, sorting it if the renderer asked.</summary>
    void Sort(ParticleBuffer particles, VfxRenderer renderer, in VfxCamera camera, int count) {
        if (order.Length < count) {
            // Grown to the buffer's whole capacity rather than to what is alive, so a system that
            // fills up does it once rather than on the frame it happens to be busiest.
            order = new int[particles.Capacity];
            keys = new float[particles.Capacity];
        }

        for (var index = 0; index < count; index++) {
            order[index] = index;
        }

        switch (renderer.Sort) {
            case VfxSortMode.ByDepth when particles.Has(VfxAttribute.Position): {
                var positions = particles.Position;

                for (var index = 0; index < count; index++) {
                    // Squared distance, descending — furthest first, which is what alpha blending
                    // needs. Squared because the ordering is the same and the square root is not.
                    keys[index] = -Vector3.DistanceSquared(positions[index], camera.Position);
                }

                Array.Sort(keys, order, 0, count);

                break;
            }

            case VfxSortMode.ByAge when particles.Has(VfxAttribute.Age): {
                var ages = particles.Age;

                for (var index = 0; index < count; index++) {
                    keys[index] = -ages[index];
                }

                Array.Sort(keys, order, 0, count);

                break;
            }

            default: {
                break;
            }
        }
    }
}
