// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Materials;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Vfx;

/// <summary>The material a particle is drawn with, with every parameter its shader declares.</summary>
/// <remarks>
///     <para>
///         <b>One place, because the failure it prevents is invisible.</b> A host that builds this by
///         hand and forgets a parameter gets an effect that simulates, expands, uploads, binds a
///         material set and draws — and produces nothing on the screen. Every counter reads healthy.
///     </para>
///     <para>
///         ⚠ <b>A shader's declared default reaches the GPU through the generated key and nowhere
///         else, and for a <em>vector</em> it does not reach it at all.</b> <c>EffectConstants</c>
///         fills each member of a block from the host's value, or from
///         <c>ParameterKey.DefaultBytes</c> when there is none — and the Raven reflection records a
///         default only for scalars. <c>ParticleSpriteKeys.Emissive</c> carries its <c>1f</c> and
///         <c>ParticleSpriteKeys.Tint</c> carries nothing, so an unset tint is written as
///         <c>(0, 0, 0, 0)</c>: black, alpha zero, and additively blended that is perfectly
///         invisible.
///     </para>
///     <para>
///         <c>Bloom.texelSize</c> has the same gap and has never shown it, because
///         <c>BloomRenderer</c> writes that parameter every frame. This is the first shader with a
///         vector parameter a host was expected to leave alone, which is why the gap surfaced here
///         and as "the lamps stopped emitting".
///     </para>
/// </remarks>
public static class ParticleSpriteMaterial {
    /// <summary>What an unset tint means, spelled out because the reflection cannot carry it.</summary>
    /// <remarks>
    ///     The value <c>ParticleSprite.rvn</c> declares. Written here as well because the two cannot
    ///     be joined automatically — see the type's remarks — and a shader whose declaration changed
    ///     without this changing would be a shader whose default is a lie.
    /// </remarks>
    public static Vector4 NeutralTint => new(1f, 1f, 1f, 1f);

    /// <summary>A material that draws particles in their own colour, at their own brightness.</summary>
    /// <returns>The material.</returns>
    /// <remarks>
    ///     <para>
    ///         <b><c>PassComposition()</c> and not a compiled material.</b> <c>ParticleSprite</c>
    ///         declares no compose slots — there is no surface and no shading model in it — but a
    ///         compilation is the whole library and RVN2073 refuses any slot any shader in it left
    ///         unbound. So it still names the defaults, exactly as <c>FullScreenRenderer</c> does.
    ///     </para>
    ///     <para>
    ///         <b>Every parameter, including the ones whose generated key already carries a
    ///         default.</b> Setting only the ones that need it would make this a list of which
    ///         parameters the reflection happens to handle, which is not a fact about particles and
    ///         would go stale the moment the reflection writer learns vectors.
    ///     </para>
    ///     <para>
    ///         A project tints, brightens or sharpens by setting these on the returned material — see
    ///         <c>WorldRenderer.ParticleMaterial</c>, which is the instance a scene's emitters are
    ///         drawn with.
    ///     </para>
    /// </remarks>
    public static Material Default() {
        var material = new Material(ParticleSpriteKeys.ShaderName) {
            Composition = MaterialCompiler.PassComposition()
        };

        material.Parameters.Set(ParticleSpriteKeys.Tint, NeutralTint);
        material.Parameters.Set(ParticleSpriteKeys.Emissive, 1f);
        material.Parameters.Set(ParticleSpriteKeys.EdgeSharpness, 1.6f);

        return material;
    }
}
