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

/// <summary>One particle's worth of per-instance data, for a mesh renderer.</summary>
/// <param name="Row0">The first row of the world matrix: the x axis, scaled, and the x translation.</param>
/// <param name="Row1">The second.</param>
/// <param name="Row2">The third.</param>
/// <param name="Colour">The particle's colour.</param>
/// <remarks>
///     <para>
///         <b>Three rows, not four.</b> The fourth row of an affine transform is always (0, 0, 0, 1)
///         and uploading it is sixteen bytes an instance to say so. Three <c>float4</c>s is what every
///         instanced renderer uses and what the vertex shader reassembles.
///     </para>
///     <para>
///         Rows rather than columns because that is the packing that puts the translation in the
///         <c>w</c> lanes, where one <c>dot</c> per axis reconstructs a transformed position. The
///         convention has to be written down somewhere and this is the somewhere.
///     </para>
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ParticleInstance(Vector4 Row0, Vector4 Row1, Vector4 Row2, Vector4 Colour);

/// <summary>Where the camera is and how it is turned, as the expansion needs it.</summary>
/// <param name="Position">Where the camera is, for depth sorting.</param>
/// <param name="Right">Its right vector, normalised.</param>
/// <param name="Up">Its up vector, normalised.</param>
/// <remarks>
///     Three vectors rather than a view matrix, because that is all the expansion uses and taking the
///     matrix would mean this module had an opinion about which convention it was in.
/// </remarks>
public readonly record struct VfxCamera(Vector3 Position, Vector3 Right, Vector3 Up) {
    /// <summary>The basis for a camera at a point, looking a way, with an idea of which way is up.</summary>
    /// <param name="position">Where the camera is.</param>
    /// <param name="forward">Which way it looks. Need not be normalised.</param>
    /// <param name="up">Roughly which way is up. Need not be square to <paramref name="forward" />.</param>
    /// <returns>The camera.</returns>
    /// <remarks>
    ///     The same derivation as <c>Matrix4x4.LookAt</c>, and deliberately so: a billboard built from
    ///     a basis that disagreed with the view matrix by a sign would be mirrored, which is a thing
    ///     nobody notices on a round puff of smoke and everybody notices on a number. Right-handed,
    ///     with the camera looking down its own -Z.
    /// </remarks>
    public static VfxCamera Looking(Vector3 position, Vector3 forward, Vector3 up) {
        var back = Vector3.Normalize(-forward);
        var right = Vector3.Normalize(Vector3.Cross(up, back));

        return new(position, right, Vector3.Cross(back, right));
    }
}

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
    ulong[] strands = [];
    int[] order = [];

    /// <summary>How many vertices one particle needs.</summary>
    public const int VerticesPerParticle = 4;

    /// <summary>How many indices one particle needs.</summary>
    public const int IndicesPerParticle = 6;

    /// <summary>How many vertices one particle of a ribbon needs: one each side of the strip.</summary>
    public const int VerticesPerRibbonParticle = 2;

    /// <summary>How many indices one length of ribbon needs — two triangles between two particles.</summary>
    public const int IndicesPerRibbonSegment = 6;

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

        var renderer = Renderer(system);
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

    /// <summary>Expands a system's live particles into per-instance transforms.</summary>
    /// <param name="system">The system.</param>
    /// <param name="camera">Where the camera is, for the sort and for a camera-facing orientation.</param>
    /// <param name="instances">Where to write. A shorter span writes as many particles as fit.</param>
    /// <returns>How many instances were written.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The graph declared no renderer.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The mesh's local +Y is the axis that gets aligned</b>, which is the same axis a
    ///         velocity-aligned billboard stretches along. One convention across both renderers is
    ///         worth more than each being locally reasonable, and a shard model built the other way up
    ///         is a rotation in the asset rather than a flag here.
    ///     </para>
    ///     <para>
    ///         Scale is uniform and comes from <see cref="VfxAttribute.Size" />, so a mesh particle
    ///         means the same thing a billboard one does. Non-uniform scale would need three lanes and
    ///         a custom attribute, which is now a thing an author can declare.
    ///     </para>
    /// </remarks>
    public int BuildInstances(VfxSystem system, in VfxCamera camera, Span<ParticleInstance> instances) {
        ArgumentNullException.ThrowIfNull(system);

        var renderer = Renderer(system);
        var particles = system.Particles;
        var count = Math.Min(particles.Count, instances.Length);

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
            var scale = sizes[index];

            var up = renderer.Alignment switch {
                VfxBillboardAlignment.Velocity when hasVelocity && velocities[index].LengthSquared() > 1e-12f =>
                    Vector3.Normalize(velocities[index]),
                VfxBillboardAlignment.FixedAxis when renderer.Axis.LengthSquared() > 1e-12f =>
                    Vector3.Normalize(renderer.Axis),
                _ => Vector3.UnitY
            };

            var right = Across(up, in camera, centre);
            var forward = Vector3.Cross(right, up);

            if (hasRotation) {
                (right, forward) = Roll(right, forward, rotations[index]);
            }

            right *= scale;
            var scaledUp = up * scale;
            forward *= scale;

            instances[slot] = new(
                new(right.X, scaledUp.X, forward.X, centre.X),
                new(right.Y, scaledUp.Y, forward.Y, centre.Y),
                new(right.Z, scaledUp.Z, forward.Z, centre.Z),
                colours[index]
            );
        }

        return count;
    }

    /// <summary>Joins the particles of each ribbon into a strip.</summary>
    /// <param name="system">The system.</param>
    /// <param name="camera">Where the camera is, for which way each length of ribbon faces.</param>
    /// <param name="vertices">
    ///     Where to write. Needs <see cref="VerticesPerRibbonParticle" /> per particle.
    /// </param>
    /// <param name="indices">Where the triangles go. Zero-based, so a caller offsets them if it must.</param>
    /// <param name="indexCount">How many indices were written.</param>
    /// <returns>How many particles were covered.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="system" /> is null.</exception>
    /// <exception cref="InvalidOperationException">The graph declared no renderer, or no ribbon slot.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The indices are built every frame and the vertex ones are not</b>, which is the whole
    ///         difference between this and a billboard. A quad's two triangles never depend on anything
    ///         but the count; a strip's depend on where each ribbon ends, and a ribbon ends wherever a
    ///         particle died. So there is no pattern to build once.
    ///     </para>
    ///     <para>
    ///         <b>A ribbon of one particle draws nothing.</b> A strip needs two points to have a
    ///         direction, and a single particle has no tangent — so it contributes its vertices and no
    ///         triangles, which is what makes a trail appear as its second particle is born rather than
    ///         as a degenerate sliver.
    ///     </para>
    /// </remarks>
    public int BuildRibbons(
        VfxSystem system,
        in VfxCamera camera,
        Span<ParticleVertex> vertices,
        Span<uint> indices,
        out int indexCount
    ) {
        ArgumentNullException.ThrowIfNull(system);

        var renderer = Renderer(system);
        var particles = system.Particles;

        if ((uint)renderer.RibbonSlot >= (uint)particles.CustomCount) {
            throw new InvalidOperationException(
                $"The ribbon renderer names custom attribute slot {renderer.RibbonSlot}, and the graph declares "
                + $"{particles.CustomCount}. A ribbon has to say which attribute holds its strip."
            );
        }

        indexCount = 0;

        var count = Math.Min(particles.Count, vertices.Length / VerticesPerRibbonParticle);

        LastCount = count;

        if (count == 0) {
            return 0;
        }

        Strand(particles, renderer, count);

        var positions = particles.Position;
        var sizes = particles.Size;
        var colours = particles.Colour;
        var strips = particles.Custom(renderer.RibbonSlot);
        var lanes = particles.Lanes(renderer.RibbonSlot);

        var start = 0;

        while (start < count) {
            var end = start + 1;

            while (end < count && strips[order[end] * lanes] == strips[order[start] * lanes]) {
                end++;
            }

            Strip(positions, sizes, colours, in camera, vertices, indices, start, end, ref indexCount);
            start = end;
        }

        return count;
    }

    /// <summary>One ribbon, from its oldest particle to its newest.</summary>
    void Strip(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<float> sizes,
        ReadOnlySpan<Vector4> colours,
        in VfxCamera camera,
        Span<ParticleVertex> vertices,
        Span<uint> indices,
        int start,
        int end,
        ref int indexCount
    ) {
        var length = end - start;

        for (var offset = 0; offset < length; offset++) {
            var index = order[start + offset];
            var centre = positions[index];

            // The tangent is towards the next particle, and the last one borrows the previous
            // length's — a ribbon's end has no next point, and reusing the direction it arrived by is
            // what keeps the final segment from twisting. A ribbon of one has neither, and takes the
            // fallback below rather than reaching for a neighbour it does not have.
            var tangent = length == 1
                ? Vector3.Zero
                : offset + 1 < length
                    ? positions[order[start + offset + 1]] - centre
                    : centre - positions[order[start + offset - 1]];

            var along = tangent.LengthSquared() > 1e-12f ? Vector3.Normalize(tangent) : Vector3.UnitY;
            var side = Across(along, in camera, centre) * (sizes[index] * 0.5f);

            var colour = colours[index];

            // u runs along the ribbon so a texture stretches from end to end, which is what a trail
            // wants; a single-particle ribbon has no length to run over and takes zero.
            var u = length > 1 ? (float)offset / (length - 1) : 0f;
            var corner = (start + offset) * VerticesPerRibbonParticle;

            vertices[corner] = new(centre - side, new(u, 0f), colour);
            vertices[corner + 1] = new(centre + side, new(u, 1f), colour);
        }

        for (var offset = 0; offset + 1 < length; offset++) {
            if (indexCount + IndicesPerRibbonSegment > indices.Length) {
                return;
            }

            var here = (uint)((start + offset) * VerticesPerRibbonParticle);
            var next = here + VerticesPerRibbonParticle;

            indices[indexCount++] = here;
            indices[indexCount++] = next;
            indices[indexCount++] = next + 1;
            indices[indexCount++] = here;
            indices[indexCount++] = next + 1;
            indices[indexCount++] = here + 1;
        }
    }

    /// <summary>Orders the particles by ribbon, and within a ribbon by age, oldest first.</summary>
    /// <remarks>
    ///     <para>
    ///         One sort, on a composite key, rather than two passes — <c>Array.Sort</c> is an introsort
    ///         and is not stable, so sorting by age and then by strip would shuffle the ages back.
    ///     </para>
    ///     <para>
    ///         The key packs the strip above the age in a <c>ulong</c>, through the standard
    ///         float-to-sortable-integer flip. That is exact for every float including negatives and
    ///         needs no comparison delegate, which is the difference between allocating nothing per
    ///         frame and allocating a closure.
    ///     </para>
    /// </remarks>
    void Strand(ParticleBuffer particles, VfxRenderer renderer, int count) {
        if (order.Length < count) {
            order = new int[particles.Capacity];
            keys = new float[particles.Capacity];
        }

        if (strands.Length < count) {
            strands = new ulong[particles.Capacity];
        }

        var strips = particles.Custom(renderer.RibbonSlot);
        var lanes = particles.Lanes(renderer.RibbonSlot);
        var ages = particles.Has(VfxAttribute.Age) ? particles.Age : default;
        var hasAge = particles.Has(VfxAttribute.Age);

        for (var index = 0; index < count; index++) {
            order[index] = index;

            // Descending age within a strip: the oldest particle is the tail the ribbon runs from.
            var age = hasAge ? -ages[index] : 0f;

            strands[index] = ((ulong)Sortable(strips[index * lanes]) << 32) | Sortable(age);
        }

        Array.Sort(strands, order, 0, count);
    }

    /// <summary>A float as an unsigned integer whose order is the float's.</summary>
    /// <remarks>
    ///     The standard flip: a non-negative float's bits already compare correctly as an integer once
    ///     the sign bit is set, and a negative one's compare backwards, so they are inverted. Exact
    ///     for every finite value, which is what lets a sort key be an integer and a sort be a plain
    ///     <c>Array.Sort</c>.
    /// </remarks>
    static uint Sortable(float value) {
        var bits = (uint)BitConverter.SingleToInt32Bits(value);

        return (bits & 0x80000000u) != 0 ? ~bits : bits | 0x80000000u;
    }

    /// <summary>The renderer a system draws with, or an explanation of why it cannot be drawn.</summary>
    static VfxRenderer Renderer(VfxSystem system) =>
        system.Graph.Renderer ?? throw new InvalidOperationException(
            "The graph declares no renderer, so it has no size and no colour to draw with. A graph that is "
            + "drawn has to say so when it is compiled, because that is what makes it allocate the attributes "
            + "drawing reads."
        );

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
