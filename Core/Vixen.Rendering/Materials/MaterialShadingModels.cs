// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Rendering.Materials;

/// <summary>Lambert plus a GGX microfacet lobe: the default.</summary>
/// <remarks>
///     Every other model here is a change to this one, which is why it has no parameters: what it
///     does is decided entirely by the surface it is given.
/// </remarks>
[DataContract("StandardShading")]
public sealed record StandardShading : IMaterialShading {
    /// <inheritdoc />
    public string ShaderName => "StandardShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
    }
}

/// <summary>Oren-Nayar's rough diffuse under the standard lobe: clay, plaster, concrete.</summary>
/// <remarks>
///     <para>
///         The same GGX specular as <see cref="StandardShading" /> and a different diffuse BRDF,
///         which is the whole difference. Lambert is a smooth Lambertian plane; this is a surface of
///         Lambertian microfacets, which flattens toward the light instead of falling off as the
///         cosine — the thing that stops dry unfinished materials reading as plastic. It reads the
///         roughness the surface already writes and has no parameters of its own.
///     </para>
///     <para>
///         ⚠ <b>Named after the model rather than after what it is for, unlike its neighbours.</b>
///         A clear coat is a second lobe and a sheen is a rim, so those names say what is added; this
///         one swaps the diffuse term and nothing else.
///     </para>
/// </remarks>
[DataContract("OrenNayarShading")]
public sealed record OrenNayarShading : IMaterialShading {
    /// <inheritdoc />
    public string ShaderName => "OrenNayarShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
    }
}

/// <summary>Burley's (Disney's) diffuse under the standard lobe: skin, unfinished wood, cloth.</summary>
/// <remarks>
///     What it buys over Lambert is the retroreflective brightening rough dielectrics have at grazing
///     angles. ⚠ Distinct from <see cref="SheenShading" />, which adds a lobe over the base and takes
///     that lobe's energy out of it: this <em>is</em> the base, and the rim is the diffuse term's own.
/// </remarks>
[DataContract("BurleyShading")]
public sealed record BurleyShading : IMaterialShading {
    /// <inheritdoc />
    public string ShaderName => "BurleyShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
    }
}

/// <summary>A GGX lobe stretched along the tangent: brushed metal, vinyl, satin.</summary>
/// <remarks>Reads the channel <see cref="AnisotropyFeature" /> writes; pair the two.</remarks>
[DataContract("AnisotropicShading")]
public sealed record AnisotropicShading : IMaterialShading {
    /// <inheritdoc />
    public string ShaderName => "AnisotropicShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
    }
}

/// <summary>A second, smoother specular lobe over the base one.</summary>
/// <remarks>Reads what <see cref="ClearCoatFeature" /> writes.</remarks>
[DataContract("ClearCoatShading")]
public sealed record ClearCoatShading : IMaterialShading {
    /// <inheritdoc />
    public string ShaderName => "ClearCoatShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
    }
}

/// <summary>A retroreflective rim over the base lobe, energy taken from it.</summary>
/// <remarks>Reads what <see cref="SheenFeature" /> writes.</remarks>
[DataContract("SheenShading")]
public sealed record SheenShading : IMaterialShading {
    /// <inheritdoc />
    public string ShaderName => "SheenShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
    }
}

/// <summary>
///     Wrapped diffuse and back-lighting: skin, wax, leaves, thin cloth.
/// </summary>
/// <remarks>
///     The parameters here describe the <em>model</em> rather than the surface, which is the split
///     the two slots exist for: how far light wraps past the terminator is one number for the whole
///     material, where how thick the surface is varies over it and belongs to
///     <see cref="SubsurfaceFeature" />.
/// </remarks>
[DataContract("SubsurfaceShading")]
public sealed record SubsurfaceShading : IMaterialShading {
    /// <summary>How far past the terminator light wraps, 0..1. Zero is Lambert.</summary>
    public float Wrap { get; init; } = 0.5f;

    /// <summary>How tightly the back-lit lobe hugs the view direction.</summary>
    public float TransmissionPower { get; init; } = 4f;

    /// <inheritdoc />
    public string ShaderName => "SubsurfaceShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("wrap", Wrap);
        context.Set("transmissionPower", TransmissionPower);
    }
}

/// <summary>Kajiya–Kay along a strand: hair, fur, anything shaded off a tangent.</summary>
[DataContract("HairShading")]
public sealed record HairShading : IMaterialShading {
    /// <inheritdoc />
    public string ShaderName => "HairShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
    }
}

/// <summary>
///     Banded light and a hard-edged highlight: the non-PBR case.
/// </summary>
/// <remarks>
///     A shading model rather than a surface feature, and that is the point of it being here at all:
///     cel shading does not change what the surface is, so a stylised material keeps its base colour,
///     its normal map and its emission and changes only the response. It is the proof the model
///     admits something that is not a BRDF.
/// </remarks>
[DataContract("CelShading")]
public sealed record CelShading : IMaterialShading {
    /// <summary>How many bands the diffuse response has.</summary>
    public float Steps { get; init; } = 3f;

    /// <summary>Where the highlight switches on.</summary>
    public float SpecularThreshold { get; init; } = 0.4f;

    /// <summary>How soft a band edge is, as a fraction of a band. Zero is a hard step.</summary>
    public float Softness { get; init; } = 0.02f;

    /// <inheritdoc />
    public string ShaderName => "CelShading";

    /// <inheritdoc />
    public void Compile(MaterialCompilationContext context) {
        ArgumentNullException.ThrowIfNull(context);

        context.Set("steps", Steps);
        context.Set("specularThreshold", SpecularThreshold);
        context.Set("softness", Softness);
    }
}
