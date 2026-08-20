// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Materials;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Vfx;

/// <summary>The material a mesh particle is drawn with, with every parameter its shader declares.</summary>
/// <remarks>
///     <para>
///         <b><see cref="ParticleSpriteMaterial" />'s counterpart for the renderer that draws geometry
///         rather than quads</b>, and it exists for the same reason: a host that builds this by hand
///         and forgets a parameter gets an effect that simulates, expands its instances, uploads them,
///         binds two vertex buffers and draws — and produces nothing on the screen, with every counter
///         reading healthy.
///     </para>
///     <para>
///         ⚠ <b>A different material from the sprite one, and it has to be.</b> The two shaders read
///         different vertex inputs: <c>ParticleSprite</c> takes a position, a texture coordinate and a
///         colour out of one buffer, and <c>ParticleMesh</c> takes a position out of the mesh's buffer
///         and a transform out of an instance stream. So a mesh effect drawn with the sprite material
///         is a pipeline whose layout has nothing to bind the instance stream to — which is why
///         <c>VfxExtractionSystem.MeshMaterial</c> is its own property rather than a fallback to
///         <c>Material</c>.
///     </para>
/// </remarks>
public static class ParticleMeshMaterial {
    /// <summary>The tint a particle keeps its own colour under.</summary>
    /// <inheritdoc cref="ParticleSpriteMaterial.NeutralTint" path="/remarks" />
    public static Vector4 NeutralTint => new(1f, 1f, 1f, 1f);

    /// <summary>A material that draws mesh particles in their own colour, at their own brightness.</summary>
    /// <returns>The material.</returns>
    /// <remarks>
    ///     <b><c>PassComposition()</c> and not a compiled material</b>, on
    ///     <see cref="ParticleSpriteMaterial.Default" />'s terms: <c>ParticleMesh</c> declares no
    ///     compose slots — there is no surface and no shading model in it — but a compilation is the
    ///     whole library and RVN2073 refuses any slot any shader in it left unbound.
    /// </remarks>
    public static Material Default() {
        var material = new Material(ParticleMeshKeys.ShaderName) {
            Composition = MaterialCompiler.PassComposition()
        };

        material.Parameters.Set(ParticleMeshKeys.Tint, NeutralTint);
        material.Parameters.Set(ParticleMeshKeys.Emissive, 1f);

        return material;
    }
}
