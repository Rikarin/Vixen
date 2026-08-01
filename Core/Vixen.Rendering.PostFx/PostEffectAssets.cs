// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.PostFx;

/// <summary>The dual-filter bloom chain.</summary>
/// <remarks>
///     A node rather than a list of passes, because the chain's shape follows from its depth and the
///     frame's size — nine passes and nine textures out of one line, and a document that spelled them
///     out would have to be rewritten to change the resolution.
/// </remarks>
[DataContract("Bloom")]
public sealed record BloomAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The shader to run, in its permuted modes.</summary>
    public string Shader { get; init; } = "Bloom";

    /// <summary>The texture the chain reads.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Bloom";

    /// <summary>How many levels the pyramid has, the first at half resolution.</summary>
    public int Levels { get; init; } = 5;

    /// <summary>The format every level has.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>Luminance above which a pixel contributes.</summary>
    public float Threshold { get; init; } = 1f;

    /// <summary>How soft that threshold is.</summary>
    public float Knee { get; init; } = 0.5f;

    /// <summary>The upsample tent's radius in texels.</summary>
    public float FilterRadius { get; init; } = 1f;

    /// <summary>How much of each level is added on the way up.</summary>
    public float Intensity { get; init; } = 1f;
}

/// <summary>The pass a frame ends with, and the grade that goes with it.</summary>
/// <remarks>
///     A node rather than a <c>!FullScreen</c> with the shader named by hand, which is what every host
///     wrote before this: five parameters and three bindings spelled out per project, and no way for a
///     document to say "grade with this table".
/// </remarks>
[DataContract("Tonemap")]
public sealed record TonemapAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The linear HDR colour it maps.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>A 3D colour lookup table, or empty for none.</summary>
    public string Lut { get; init; } = "";

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "Tonemapped";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba8UNormSrgb;

    /// <summary>Which curve maps the range: 0 Reinhard, 1 ACES, 2 AgX, 3 Uncharted.</summary>
    public int Operator { get; init; } = 1;

    /// <summary>Whether the result is encoded to sRGB here rather than by the target's format.</summary>
    public bool EncodeSrgb { get; init; }

    /// <summary>What the scene's radiance is multiplied by before the curve.</summary>
    public float Exposure { get; init; } = 1f;

    /// <summary>The radiance that maps to white.</summary>
    public float WhitePoint { get; init; } = 4f;

    /// <summary>Contrast, around middle grey.</summary>
    public float Contrast { get; init; } = 1f;

    /// <summary>Saturation, 0 for greyscale.</summary>
    public float Saturation { get; init; } = 1f;

    /// <summary>White balance, in mireds away from neutral.</summary>
    public float Temperature { get; init; }
}

/// <summary>The screen-space pass that marches the distance field for occlusion and sun shadow.</summary>
/// <remarks>
///     <para>
///         [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L1's consumer.
///         <c>GlobalDistanceField</c> composites the clipmap and this reads it — and ⚠ <b>the two have
///         to name the same shader</b>, because that name is the compose-slot prefix one writes its
///         bindings under and the other reads them from. They are separate strings on purpose: a frame
///         may march a field a different node composited, or none at all.
///     </para>
///     <para>
///         <see cref="Source" /> left alone answers "nothing is near" — fully open, fully lit — which
///         is the honest default for a project with no clipmap, and is why this node is safe to leave
///         in a document that a given build has no field for.
///     </para>
/// </remarks>
[DataContract("DistanceFieldAo")]
public sealed record DistanceFieldAoAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The depth it reconstructs world positions from.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The normals it orients the occlusion integral by.</summary>
    public string Normals { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "AmbientOcclusion";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>The shader behind the field slot — what the march actually reads.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Whether to trace a sun shadow alongside the occlusion.</summary>
    /// <remarks>
    ///     A permutation rather than a branch, so off means the march is not compiled and the pass
    ///     carries none of the sun's uniforms either.
    /// </remarks>
    public bool SunShadow { get; init; } = true;
}

/// <summary>The screen-space pass that reads the irradiance field into the ambient term.</summary>
/// <remarks>
///     [19](../../../docs/plan/19-lighting-and-global-illumination.md) § L2's consumer, paired with
///     <c>IrradianceField</c> exactly as <see cref="DistanceFieldAoAsset" /> is paired with
///     <c>GlobalDistanceField</c>. Its default <c>Source</c> answers no indirect light <i>and</i> an
///     unshadowed sun — two different right answers rather than one convenient zero, because
///     answering zero for the second would put every surface in the world into shadow.
/// </remarks>
[DataContract("IndirectDiffuse")]
public sealed record IndirectDiffuseAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The depth it reconstructs world positions from.</summary>
    public string Depth { get; init; } = string.Empty;

    /// <summary>The normals it asks the probe about.</summary>
    public string Normals { get; init; } = string.Empty;

    /// <summary>The name the result is published under.</summary>
    public string Output { get; init; } = "IndirectDiffuse";

    /// <summary>The format of the target it declares.</summary>
    public PixelFormat Format { get; init; } = PixelFormat.Rgba16Float;

    /// <summary>The shader behind the field slot — what the lookup actually reads.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>A multiplier on the result.</summary>
    /// <remarks>
    ///     Physically one. It exists because artists ask for it, and because a value that is not one
    ///     is then visible in a capture rather than folded into the field, where it would quietly
    ///     corrupt what the next bounce reads.
    /// </remarks>
    public float Intensity { get; init; } = 1f;
}

/// <summary>Builds the effect set's node kinds from a compositor document.</summary>
/// <remarks>
///     <para>
///         What a host registers on a <see cref="CompositorBuilder" /> so a document can name
///         <c>!Bloom</c> or <c>!Tonemap</c>. The builder cannot know these types — this project is
///         downstream of it, and a switch case here would be a cycle — so the knowledge travels the
///         only direction it can.
///     </para>
///     <para>
///         One factory for the whole set rather than one per effect, because what it does is a switch
///         over asset types and a list of single-case factories would be the same switch spread over
///         seven files.
///     </para>
/// </remarks>
public sealed class PostEffectFactory : ISceneRendererFactory {
    /// <inheritdoc />
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        return declared switch {
            BloomAsset bloom => Bloom(bloom, builder),
            TonemapAsset tonemap => Tonemap(tonemap, builder),
            DistanceFieldAoAsset occlusion => Occlusion(occlusion, builder),
            IndirectDiffuseAsset indirect => Indirect(indirect, builder),
            _ => null
        };
    }

    static BloomRenderer Bloom(BloomAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            ShaderName = declared.Shader,
            Source = declared.Source,
            Output = declared.Output,
            Levels = declared.Levels,
            Format = declared.Format,
            Threshold = declared.Threshold,
            Knee = declared.Knee,
            FilterRadius = declared.FilterRadius,
            Intensity = declared.Intensity,
            Modules = builder.Modules,
            Device = builder.Device,
            Descriptors = builder.Descriptors,
            Samplers = builder.Samplers
        };

    static DistanceFieldAoRenderer Occlusion(DistanceFieldAoAsset declared, CompositorBuilder builder) {
        var node = new DistanceFieldAoRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Normals = declared.Normals,
            Output = declared.Output,
            Format = declared.Format,
            SunShadow = declared.SunShadow,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

        // Empty leaves the renderer's own default — the shader that answers "nothing is near" — rather
        // than naming a slot nothing fills. See the asset.
        if (declared.Source is { Length: > 0 } source) {
            node.Source = source;
        }

        return node;
    }

    static IndirectDiffuseRenderer Indirect(IndirectDiffuseAsset declared, CompositorBuilder builder) {
        var node = new IndirectDiffuseRenderer {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Depth = declared.Depth,
            Normals = declared.Normals,
            Output = declared.Output,
            Format = declared.Format,
            Intensity = declared.Intensity,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };

        if (declared.Source is { Length: > 0 } source) {
            node.Source = source;
        }

        return node;
    }

    static TonemapRenderer Tonemap(TonemapAsset declared, CompositorBuilder builder) =>
        new() {
            Name = declared.Name,
            Enabled = declared.Enabled,
            Source = declared.Source,
            Lut = declared.Lut,
            Output = declared.Output,
            Format = declared.Format,
            Operator = declared.Operator,
            EncodeSrgb = declared.EncodeSrgb,
            Exposure = declared.Exposure,
            WhitePoint = declared.WhitePoint,
            Contrast = declared.Contrast,
            Saturation = declared.Saturation,
            Temperature = declared.Temperature,
            Modules = builder.Modules,
            Device = builder.Device,
            Allocator = builder.Descriptors,
            Samplers = builder.Samplers
        };
}
