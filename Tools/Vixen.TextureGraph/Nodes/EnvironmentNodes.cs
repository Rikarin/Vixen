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

/// <summary>A clear sky as an equirectangular panorama — Patina #180's sky model.</summary>
/// <remarks>
///     <para>
///         <b>Preetham, Shirley and Smits' analytic daylight</b>: a turbidity and a sun position decide the sky's
///         luminance and colour everywhere, with a sun disc on top and a ground colour below the horizon. The
///         picture is a panorama, once round the horizon across its width and straight up at the top, so a graph
///         twice as wide as it is tall gives every direction square texels.
///     </para>
///     <para>
///         ⚠ <b>The reddening of a low sun is an approximation</b>, an extinction per channel along the air the sun
///         crosses chosen to look right; Preetham's model describes the sky and not the sun.
///     </para>
/// </remarks>
[DoesNotTile(
    "A panorama wraps round the horizon across its width, but its top row is the zenith and its bottom the ground."
)]
[Node("Environment/Sky", Preview = true, Summary = "A clear daylight sky with a sun, as a panorama to light a scene by.")]
sealed partial class SkyNode : TextureNode {
    /// <summary>The sun's azimuth, in radians.</summary>
    [Input(
        Name = "Sun Azimuth",
        SoftMinimum = -MathF.PI,
        SoftMaximum = MathF.PI,
        Summary = "Where round the horizon the sun stands, in radians: nought is the picture's centre."
    )]
    public Scalar SunAzimuth = 0f;

    /// <summary>The sun's elevation, in radians.</summary>
    [Input(
        Name = "Sun Elevation",
        Minimum = 0f,
        Maximum = 1.5707964f,
        Summary = "How high the sun stands above the horizon, in radians: 1.57 is overhead."
    )]
    public Scalar SunElevation = 0.6f;

    /// <summary>How hazy the air is.</summary>
    [Input(Minimum = 1.7f, Maximum = 10f, Summary = "How hazy the air is: 2 is a very clear day, 10 a hazy one.")]
    public Scalar Turbidity = 3f;

    /// <summary>What the sky's luminance is multiplied by.</summary>
    [Input(
        Minimum = 0f,
        SoftMaximum = 1f,
        Summary = "What the sky's luminance, in thousands of candelas per square metre, is multiplied by."
    )]
    public Scalar Intensity = 0.1f;

    /// <summary>The sun disc's angular radius.</summary>
    [Input(Name = "Sun Size", Minimum = 0f, SoftMaximum = 0.2f, Summary = "The sun's radius, in radians.")]
    public Scalar SunSize = 0.02f;

    /// <summary>The sun disc's brightness.</summary>
    [Input(
        Name = "Sun Intensity",
        Minimum = 0f,
        SoftMaximum = 200f,
        Summary = "How bright the sun's disc is, as a multiple of the sky straight overhead."
    )]
    public Scalar SunIntensity = 50f;

    /// <summary>The colour below the horizon.</summary>
    [Input(Name = "Ground", Default = [0.1f, 0.09f, 0.08f, 1f])]
    public Float4 Ground;

    /// <summary>The sky.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var target = emitter.Write("Out", TextureChannels.Colour);

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureEnvironmentKernels.Sky,
                Output = target,
                Inputs = [],
                Parameters = [
                    new("azimuth", emitter.Number("Sun Azimuth")),
                    new("elevation", emitter.Number("Sun Elevation")),
                    new("turbidity", emitter.Number(nameof(Turbidity))),
                    new("intensity", emitter.Number(nameof(Intensity))),
                    new("sunSize", emitter.Number("Sun Size")),
                    new("sunIntensity", emitter.Number("Sun Intensity")),
                    new("groundRed", emitter.Number("Ground", 0)),
                    new("groundGreen", emitter.Number("Ground", 1)),
                    new("groundBlue", emitter.Number("Ground", 2))
                ]
            }
        );
    }
}

/// <summary>A panorama turned about the vertical, tipped and rolled — Patina #180's panorama rotate.</summary>
/// <remarks>
///     <b>Yaw alone is a shift along the width</b>, so a panorama turned only about the vertical keeps its rows and
///     still rolls with its input, which is what the tiling roll call holds. Pitch and roll move texels between
///     rows; a panorama does not tile top to bottom, so nothing is lost that was there.
/// </remarks>
[Node(
    "Environment/Panorama Rotate",
    Preview = true,
    Summary = "Turns a panorama about the vertical, tips its horizon or rolls it, to aim its light."
)]
sealed partial class PanoramaRotateNode : TextureNode {
    /// <summary>The panorama.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The turn about the vertical.</summary>
    [Input(
        SoftMinimum = -MathF.PI,
        SoftMaximum = MathF.PI,
        Summary = "How far the panorama turns about the vertical, in radians."
    )]
    public Scalar Yaw = 0f;

    /// <summary>The tip of the horizon.</summary>
    [Input(
        SoftMinimum = -MathF.PI,
        SoftMaximum = MathF.PI,
        Summary = "How far the horizon tips, toward or away from the picture's centre, in radians."
    )]
    public Scalar Pitch = 0f;

    /// <summary>The turn about the view.</summary>
    [Input(
        SoftMinimum = -MathF.PI,
        SoftMaximum = MathF.PI,
        Summary = "How far the panorama rolls about the view along the picture's centre, in radians."
    )]
    public Scalar Roll = 0f;

    /// <summary>The turned panorama.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureEnvironmentKernels.PanoramaTurn,
                Output = target,
                Inputs = [source],
                ReadsOtherExtents = true,
                Parameters = [
                    new("yaw", emitter.Number(nameof(Yaw))),
                    new("pitch", emitter.Number(nameof(Pitch))),
                    new("roll", emitter.Number(nameof(Roll)))
                ]
            }
        );
    }
}

/// <summary>A disc of light added to a panorama — Patina #180's panorama lights.</summary>
/// <remarks>
///     <para>
///         <b>A lamp, a window or a second sun</b>, at an azimuth and an elevation in <c>Environment/Sky</c>'s
///         convention, so a light placed where the sky's sun is lands on it. A hard-edged light adds its intensity
///         over a solid angle of 2π(1 − cos size).
///     </para>
///     <para>
///         ⚠ <b>Rolling the input does not move the light</b>, which stands at a direction and not under a texel, so
///         the node tiles when its input does and does not roll with it.
///     </para>
/// </remarks>
[Node(
    "Environment/Panorama Light",
    Preview = true,
    Summary = "Adds a round light to a panorama at a direction: a lamp, a window or a second sun."
)]
sealed partial class PanoramaLightNode : TextureNode {
    /// <summary>The panorama.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The light's azimuth.</summary>
    [Input(
        SoftMinimum = -MathF.PI,
        SoftMaximum = MathF.PI,
        Summary = "Where round the horizon the light stands, in radians: nought is the picture's centre."
    )]
    public Scalar Azimuth = 0f;

    /// <summary>The light's elevation.</summary>
    [Input(
        Minimum = -1.5707964f,
        Maximum = 1.5707964f,
        Summary = "How high the light stands above the horizon, in radians."
    )]
    public Scalar Elevation = 0.6f;

    /// <summary>The light's angular radius.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f, Summary = "The light's radius, in radians.")]
    public Scalar Size = 0.1f;

    /// <summary>How soft the edge is.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How much of the radius the edge fades over.")]
    public Scalar Softness = 0.2f;

    /// <summary>The light's colour.</summary>
    [Input(Name = "Colour", Default = [1f, 1f, 1f, 1f])]
    public Float4 Colour;

    /// <summary>What the colour is multiplied by.</summary>
    [Input(Minimum = 0f, SoftMaximum = 100f, Summary = "How bright the light is.")]
    public Scalar Intensity = 10f;

    /// <summary>The panorama with the light.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureEnvironmentKernels.PanoramaLight,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("azimuth", emitter.Number(nameof(Azimuth))),
                    new("elevation", emitter.Number(nameof(Elevation))),
                    new("size", emitter.Number(nameof(Size))),
                    new("softness", emitter.Number(nameof(Softness))),
                    new("red", emitter.Number("Colour", 0)),
                    new("green", emitter.Number("Colour", 1)),
                    new("blue", emitter.Number("Colour", 2)),
                    new("intensity", emitter.Number(nameof(Intensity)))
                ]
            }
        );
    }
}

/// <summary>A panorama's ground below a horizon replaced by a colour — Patina #180's horizon tools.</summary>
/// <remarks>
///     A photographed panorama's lower half is a tripod, a street or a floor that a material preview does not want
///     reflected. Below the horizon, raised or lowered by <c>Height</c>, the ground fades to one colour over a band
///     <c>Width</c> wide; at the horizon itself it is half and half.
/// </remarks>
[DoesNotTile("A horizon is one row of a panorama, and the ground below it does not meet the sky above at the edges.")]
[Node(
    "Environment/Horizon",
    Preview = true,
    Summary = "Replaces a panorama's ground below the horizon with a colour, faded in over a band."
)]
sealed partial class HorizonNode : TextureNode {
    /// <summary>The panorama.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The horizon's elevation.</summary>
    [Input(
        SoftMinimum = -0.5f,
        SoftMaximum = 0.5f,
        Summary = "Where the horizon is, in radians above the picture's middle row; below nought lowers it."
    )]
    public Scalar Height = 0f;

    /// <summary>The band the ground fades in over.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.5f, Summary = "How wide the band the ground fades in over is, in radians.")]
    public Scalar Width = 0.05f;

    /// <summary>How much of the ground replaces the panorama.</summary>
    [Input(Minimum = 0f, Maximum = 1f, Summary = "How much of the ground colour replaces the panorama.")]
    public Scalar Amount = 1f;

    /// <summary>The ground's colour.</summary>
    [Input(Name = "Ground", Default = [0.1f, 0.09f, 0.08f, 1f])]
    public Float4 Ground;

    /// <summary>The panorama with its ground replaced.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureEnvironmentKernels.PanoramaHorizon,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("height", emitter.Number(nameof(Height))),
                    new("width", emitter.Number(nameof(Width))),
                    new("amount", emitter.Number(nameof(Amount))),
                    new("red", emitter.Number("Ground", 0)),
                    new("green", emitter.Number("Ground", 1)),
                    new("blue", emitter.Number("Ground", 2))
                ]
            }
        );
    }
}

/// <summary>A picture brightened or darkened by stops, as a camera's exposure does — Patina #180.</summary>
/// <remarks>
///     <b>Each stop doubles the light</b>: red, green and blue are multiplied by two to the power of
///     <c>Stops</c>, and alpha is kept. Nothing clamps, so an environment's sun stays brighter than white in a graph
///     of 16 or 32 bits. It is <c>Colour/Channel Mix</c>'s kernel with only its diagonal set.
/// </remarks>
[Node(
    "Colour/Exposure",
    Preview = true,
    Summary = "Brightens or darkens a picture by stops: each stop doubles or halves the light."
)]
sealed partial class ExposureNode : TextureNode {
    /// <summary>The picture.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many stops brighter.</summary>
    [Input(SoftMinimum = -8f, SoftMaximum = 8f, Summary = "How many stops brighter: 1 doubles the light, -1 halves it.")]
    public Scalar Stops = 0f;

    /// <summary>The exposed picture.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        var gain = MathF.Pow(2f, emitter.Number(nameof(Stops)));

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourOperators.ChannelMix,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("redFromRed", gain),
                    new("redFromGreen", 0f),
                    new("redFromBlue", 0f),
                    new("redOffset", 0f),
                    new("greenFromRed", 0f),
                    new("greenFromGreen", gain),
                    new("greenFromBlue", 0f),
                    new("greenOffset", 0f),
                    new("blueFromRed", 0f),
                    new("blueFromGreen", 0f),
                    new("blueFromBlue", gain),
                    new("blueOffset", 0f)
                ]
            }
        );
    }
}
