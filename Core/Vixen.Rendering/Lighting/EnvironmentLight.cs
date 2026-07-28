// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Shaders;

namespace Vixen.Rendering.Lighting;

/// <summary>
///     The light a whole environment casts: a prefiltered cube for reflections, nine coefficients
///     for everything else.
/// </summary>
/// <remarks>
///     <para>
///         The two halves of the split-sum approximation as a frame holds them, which is the answer
///         to "what does the sky contribute" for every surface that is not looking at a lamp. A scene
///         has one of these; a reflection probe is the same shape with bounds around it.
///     </para>
///     <para>
///         <strong>Both halves come from <see cref="EnvironmentBaker" />, and they have to come from
///         the same source.</strong> Coefficients projected from one environment and a chain
///         prefiltered from another give a surface whose reflection and whose ambient disagree, which
///         reads as a material with the wrong roughness rather than as two mismatched bakes.
///     </para>
/// </remarks>
public sealed class EnvironmentLight {
    /// <summary>The diffuse irradiance, as an order-2 projection of the environment's radiance.</summary>
    public ShCoefficients Irradiance { get; set; }

    /// <summary>The prefiltered radiance, one mip per roughness.</summary>
    public TextureViewHandle Prefiltered { get; set; }

    /// <summary>How the prefiltered cube is sampled.</summary>
    public SamplerHandle Sampler { get; set; }

    /// <summary>
    ///     How many levels the prefiltered chain has.
    /// </summary>
    /// <remarks>
    ///     What the shader turns a roughness into a level with, so it is the count of the chain that
    ///     was actually uploaded rather than the count the texture could hold. One too many and a
    ///     rough material samples a level that was never filled.
    /// </remarks>
    public int MipCount { get; set; } = 1;

    /// <summary>What both halves are multiplied by.</summary>
    /// <remarks>
    ///     An artistic control rather than a physical one, and the reason it multiplies the whole
    ///     ambient term rather than the coefficients: scaling the coefficients would make a baked
    ///     probe mean something different from the environment it was baked from.
    /// </remarks>
    public float Intensity { get; set; } = 1f;

    /// <summary>Writes this environment into the parameters a pass reads.</summary>
    /// <param name="parameters">Where the values go.</param>
    /// <param name="shaderName">
    ///     The pass. Every key a shader owns is qualified by it, so the same environment writes into
    ///     the forward pass and the deferred one under two sets of names.
    /// </param>
    /// <remarks>
    ///     By name rather than through the generated keys, because a scene has one environment and
    ///     several passes that read it — and the generated keys exist per shader. The names are the
    ///     generated ones, which a test asserts rather than trusts.
    /// </remarks>
    public void Apply(ParameterCollection parameters, string shaderName = "ForwardPlus") {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentException.ThrowIfNullOrEmpty(shaderName);

        parameters.Set(ParameterKeys.New<float>($"{shaderName}.environmentMipCount"), MipCount);
        parameters.Set(ParameterKeys.New<float>($"{shaderName}.ambientIntensity"), Intensity);

        var sh = Irradiance;

        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l00"), sh.L00);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l1m1"), sh.L1m1);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l10"), sh.L10);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l11"), sh.L11);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l2m2"), sh.L2m2);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l2m1"), sh.L2m1);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l20"), sh.L20);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l21"), sh.L21);
        parameters.Set(ParameterKeys.New<Vector3>($"{shaderName}.environmentSh.l22"), sh.L22);
    }
}
