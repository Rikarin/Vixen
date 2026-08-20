// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Graphics;
using Vixen.Vfx;

namespace Vixen.Rendering;

/// <summary>The vertex format <see cref="Features.ParticleRenderFeature" /> expands particles into.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ParticleVertex" />'s schema, and it lives here rather than beside the struct
///         because the struct's project has no graphics in it.</b> <c>Vixen.Vfx</c> references only the
///         core assemblies — no <see cref="VertexSchema" />, no <see cref="VertexFormat" /> — which is
///         deliberate and is what lets the expansion be tested without a device. So the description of
///         how those bytes reach a pipeline belongs on this side of that line, exactly as
///         <see cref="SurfaceVertex" />'s does.
///     </para>
///     <para>
///         <b>Its own layout index, not the surface one.</b>
///         <see cref="Features.ParticleRenderFeature.VertexLayout" /> defaults to 1 because entry 0 is
///         <see cref="SurfaceVertex.Schema" /> in every host that draws meshes — a particle put through
///         the surface layout would have its texture coordinate read as a normal, which is a pipeline
///         the driver accepts and a picture nobody can explain.
///     </para>
/// </remarks>
public static class ParticleVertices {
    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<ParticleVertex>();

    /// <summary>The three attributes under the names <c>ParticleSprite.rvn</c>'s vertex stage declares.</summary>
    /// <remarks>
    ///     The names are the contract, on <see cref="SurfaceVertex.Schema" />'s terms: a schema matches
    ///     a stage's inputs by name rather than by location, so <c>color</c> here and
    ///     <c>particleColor</c> in a stage is an attribute the pipeline refuses to bind — and what the
    ///     driver reports is that the layout is incomplete, not which one of the three it wanted.
    /// </remarks>
    public static VertexSchema Schema { get; } = new(
        SizeInBytes,
        new("position", VertexFormat.Float32X3, 0),
        new("texcoord", VertexFormat.Float32X2, 12),
        new("color", VertexFormat.Float32X4, 20)
    );
}

/// <summary>
///     The per-instance format a <see cref="VfxRendererKind.Mesh" /> effect's particles arrive in.
/// </summary>
/// <remarks>
///     <para>
///         <b><see cref="ParticleInstance" />'s schema, here rather than beside the struct for
///         <see cref="ParticleVertices" />' reason</b> — <c>Vixen.Vfx</c> has no graphics in it.
///     </para>
///     <para>
///         <b>The half of the layout that is not the mesh's.</b> A mesh particle draws the mesh's own
///         vertices through the mesh's own schema, and everything that makes one particle different
///         from another is here: three rows of an affine transform and a colour. So this is never a
///         layout index of its own — it is <see cref="VertexSchema.Instances" /> on whichever schema
///         describes the shape, and <see cref="MeshParticleVertices.Schema" /> is the pair the engine
///         registers.
///     </para>
///     <para>
///         ⚠ <b>Three rows and no fourth.</b> The last row of an affine transform is always
///         (0, 0, 0, 1), and the translation rides in the <c>w</c> lanes — see
///         <see cref="ParticleInstance" />, which is where the convention is written down.
///     </para>
/// </remarks>
public static class ParticleInstances {
    /// <summary>How many bytes one of these is.</summary>
    public static int SizeInBytes => Marshal.SizeOf<ParticleInstance>();

    /// <summary>The four attributes under the names <c>ParticleMesh.rvn</c>'s vertex stage declares.</summary>
    /// <inheritdoc cref="ParticleVertices.Schema" path="/remarks" />
    public static VertexSchema Schema { get; } = new(
        SizeInBytes,
        new("instanceRow0", VertexFormat.Float32X4, 0),
        new("instanceRow1", VertexFormat.Float32X4, 16),
        new("instanceRow2", VertexFormat.Float32X4, 32),
        new("instanceColor", VertexFormat.Float32X4, 48)
    );
}

/// <summary>
///     The layout a mesh drawn as particles is described by: a surface vertex and a particle instance.
/// </summary>
/// <remarks>
///     <para>
///         <b>One index, two buffers, two rates.</b> <c>ParticleRenderFeature.Draw</c> binds the mesh's
///         vertices at binding 0 and its own instance stream at binding 1, so the pipeline has to
///         declare both — and a layout index names a pipeline's whole vertex input, not one buffer of
///         it. This is that pair, so a host registers one entry rather than discovering that the table
///         cannot express the draw it is already making.
///     </para>
///     <para>
///         ⚠ <b>Its own entry, not <see cref="SurfaceVertex.Schema" /> with a stream added.</b> The same
///         mesh is drawn as an ordinary object through entry 0 — one buffer, one rate — and giving that
///         entry an instance stream would describe a second vertex buffer for every mesh in the scene,
///         which nothing binds.
///     </para>
/// </remarks>
public static class MeshParticleVertices {
    /// <summary>The mesh's own format, with the particle instance stream beside it.</summary>
    public static VertexSchema Schema { get; } = new(
        SurfaceVertex.SizeInBytes,
        [.. SurfaceVertex.Schema.Attributes]
    ) {
        Instances = ParticleInstances.Schema
    };
}
