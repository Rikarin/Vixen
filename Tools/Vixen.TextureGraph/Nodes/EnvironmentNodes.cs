// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Which space a normal map is in, for <c>Surface/Environment Light</c>.</summary>
enum TextureNormalSpace {
    /// <summary>Tangent space, of a texture lying flat under the sky: blue faces up.</summary>
    Tangent,

    /// <summary>World space, encoded 0..1, as a bake's world normal is.</summary>
    World
}

/// <summary>A normal map lit by an environment picture — Patina #119's lighting baked from an environment.</summary>
/// <remarks>
///     <para>
///         <b>Three steps.</b> The environment, an equirectangular picture of the surroundings, is shrunk by a box to
///         at most 32 texels a side; the diffuse light it casts is summed for every direction a surface can face,
///         into a map as small; and each texel reads the light for the way its normal faces. A uniform white
///         environment lights every texel white, so the result multiplies straight into a base colour.
///     </para>
///     <para>
///         ⚠ <b>Rolling the environment turns it, it does not move the picture.</b> The environment is looked up
///         by direction, so the node tiles when its normal does and does not roll with its environment.
///     </para>
/// </remarks>
[Node(
    "Surface/Environment Light",
    Preview = true,
    Summary = "The diffuse light an environment picture casts on a normal map, to bake into a colour."
)]
sealed partial class EnvironmentLightNode : TextureNode {
    /// <summary>The longest side of the shrunk environment and of its irradiance.</summary>
    const int Small = 32;

    /// <summary>The normal map to light.</summary>
    [Input(Name = "Normal")]
    public Image Normal;

    /// <summary>The surroundings, as an equirectangular picture: once round the horizon, straight up at the top.</summary>
    [Input(Name = "Environment")]
    public Image Environment;

    /// <summary>Which space the normal map is in.</summary>
    [Setting(
        AcceptedFrom = typeof(TextureNormalSpace),
        Summary = "Tangent for a texture lying flat under the sky; World for a bake's world normal."
    )]
    public string Space = "Tangent";

    /// <summary>How far the environment is turned about the vertical, in radians.</summary>
    [Input(
        SoftMinimum = -MathF.PI,
        SoftMaximum = MathF.PI,
        Summary = "How far the environment is turned about the vertical, in radians."
    )]
    public Scalar Rotation = 0f;

    /// <summary>What the light is multiplied by.</summary>
    [Input(Minimum = 0f, SoftMaximum = 4f, Summary = "What the light is multiplied by.")]
    public Scalar Intensity = 1f;

    /// <summary>The light each texel receives.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var space = TextureSettings.Enum(emitter, nameof(Space), TextureNormalSpace.Tangent);
        var normal = emitter.Read("Normal", TextureChannels.Colour);
        var environment = emitter.Read("Environment", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (normal < 0 || environment < 0) {
            return;
        }

        var size = emitter.SizeOf(target);
        var shrink = 0;

        while (Math.Max(size.X, size.Y) >> shrink > Small) {
            shrink++;
        }

        var level = emitter.LevelOf(target) + shrink;
        var small = emitter.Scratch(DeepScratch.Format(emitter), level);
        var irradiance = emitter.Scratch(DeepScratch.Format(emitter), level);
        var extent = Math.Max(Math.Max(size.X, size.Y) >> shrink, 1);

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Resample,
                Output = small,
                Inputs = [environment],
                ReadsOtherExtents = true,
                EmittedForExtent = extent,
                Parameters = [new("filter", (float)TextureFilter.Box)]
            }
        );
        emitter.Dispatch(
            new TextureOp {
                Kernel = TexturePhotoKernels.EnvironmentIrradiance,
                Output = irradiance,
                Inputs = [small],
                DependsOnEveryTexel = true,
                EmittedForExtent = extent,
                Parameters = []
            }
        );
        emitter.Dispatch(
            new TextureOp {
                Kernel = TexturePhotoKernels.EnvironmentLight,
                Output = target,
                Inputs = [normal, irradiance],
                ReadsOtherExtents = true,
                OtherExtentInputs = [irradiance],
                DependsOnEveryTexel = true,
                Parameters = [
                    new("rotation", emitter.Number(nameof(Rotation))),
                    new("intensity", Math.Max(emitter.Number(nameof(Intensity)), 0f)),
                    new("world", space == TextureNormalSpace.World ? 1f : 0f)
                ]
            }
        );
    }
}
