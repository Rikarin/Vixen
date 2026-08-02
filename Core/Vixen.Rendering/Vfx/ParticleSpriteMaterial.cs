// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Materials;
using Vixen.Shaders.Generated;

namespace Vixen.Rendering.Vfx;

/// <summary>The material a particle is drawn with, with every parameter its shader declares.</summary>
/// <remarks>
///     <para>
///         <b>One place, because a host that builds this by hand and forgets a parameter gets an
///         effect that simulates, expands, uploads, binds a material set and draws — and produces
///         nothing on the screen.</b> Every counter reads healthy. That is what happened when the
///         embers stopped appearing.
///     </para>
///     <para>
///         ⚠ <b>Setting every parameter is belt and braces, not a workaround.</b> It used to be
///         necessary: Raven reported a declared default only for scalars, because a scalar's is a
///         literal and a vector's is a construction — so <c>ParticleSpriteKeys.Tint</c> carried
///         nothing and <c>EffectConstants</c> wrote <c>(0, 0, 0, 0)</c>, which is black at zero alpha
///         and invisible under an additive blend. <c>ConstantEvaluator</c> folds a vector
///         construction now and the key carries its <c>float4(1f, 1f, 1f, 1f)</c>, so an unset tint
///         would come out right on its own.
///     </para>
///     <para>
///         It is still written out here, because what this method is <em>for</em> is being the one
///         answer to "what does a particle material start as" — and a method that set only the
///         parameters the reflection cannot supply would be a list of the compiler's gaps rather
///         than a description of a material.
///     </para>
/// </remarks>
public static class ParticleSpriteMaterial {
    /// <summary>The tint a particle keeps its own colour under.</summary>
    /// <remarks>
    ///     The value <c>ParticleSprite.rvn</c> declares, and <c>ParticleSpriteKeys.Tint</c> now
    ///     carries it too — so this is a second spelling of one number. Kept because it is what
    ///     <see cref="Default" /> means rather than what the compiler happens to report, and named so
    ///     that a project reaching for "no tint" has something to reach for.
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
    ///         default</b> — which, since the compiler learned to fold a vector construction, is all
    ///         of them. See the type's remarks for why that is still worth writing out.
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
