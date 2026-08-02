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
