// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>A noise of the model's own space, read through its position map.</summary>
/// <remarks>
///     <para>
///         <b>Reads the bake's <c>position</c> map, or whatever is wired into <c>Position</c></b> — Patina #179.
///         Every texel evaluates a 3D noise at the point of the model it covers, so two texels either side of
///         a UV seam that are one point get one value, and the material continues across the seam the way a
///         block it was carved from would. <c>Scale</c> is cells across the model's bounding box.
///     </para>
///     <para>
///         <b>
///             <c>Basis</c>
///         </b>
///         is <c>Gradient</c> (Perlin's), <c>Simplex</c>, <c>Worley</c> (the distance to
///         the nearest of a scatter of points: pits and pores) or <c>Value</c>;
///         <b>
///             <c>Profile</c>
///         </b>
///         is
///         <c>Source/Noise</c>'s, so <c>Ridged</c> gives creases and <c>Billow</c> puffs. Octaves are folded
///         before they are summed and the sum is normalised, so adding one adds detail and not brightness.
///     </para>
/// </remarks>
[Node(
    "Source/Volume Noise",
    Preview = true,
    Summary = "A 3D noise of the model's space, read through its position map."
)]
sealed partial class VolumeNoiseNode : TextureNode {
    /// <summary>Which lattice: <c>Gradient</c>, <c>Simplex</c>, <c>Worley</c> or <c>Value</c>.</summary>
    [Setting(AcceptedFrom = typeof(TextureVolumeBasis))]
    public string Basis = "Gradient";

    /// <summary>How each octave is folded: <c>Plain</c>, <c>Ridged</c> or <c>Billow</c>.</summary>
    [Setting(AcceptedFrom = typeof(TextureNoiseProfile))]
    public string Profile = "Plain";

    /// <summary>The position map. Unwired, the bake's own <c>position</c> mesh map.</summary>
    [Input(Name = "Position")]
    public Image Position;

    /// <summary>Cells across the model's bounding box.</summary>
    [Input(Minimum = 0f, SoftMaximum = 64f)]
    public Scalar Scale = 4f;

    /// <summary>How many octaves are summed.</summary>
    [Input(Minimum = 1f, Maximum = 8f)]
    public Int Octaves = 4;

    /// <summary>How much finer each octave is than the one before.</summary>
    [Input(Minimum = 1f, SoftMaximum = 4f)]
    public Scalar Lacunarity = 2f;

    /// <summary>How much weaker each octave is than the one before. Higher is rougher.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Gain = 0.5f;

    /// <summary>Another draw of the same noise; nought is the graph's own — <c>Source/Noise</c>'s <c>Seed</c>.</summary>
    [Input(Minimum = 0f, SoftMaximum = 100f, Summary = "Another draw of the same noise. 0 is the graph's own.")]
    public Int Seed = 0;

    /// <summary>A shift of the noise along the model's first axis, in cells.</summary>
    [Input(Name = "Offset X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar OffsetX = 0f;

    /// <summary>A shift along its second axis, in cells.</summary>
    [Input(Name = "Offset Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar OffsetY = 0f;

    /// <summary>A shift along its third axis, in cells.</summary>
    [Input(Name = "Offset Z", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar OffsetZ = 0f;

    /// <summary>The noise.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var basis = TextureSettings.Enum(emitter, nameof(Basis), TextureVolumeBasis.Gradient);
        var profile = TextureSettings.Enum(emitter, nameof(Profile), TextureNoiseProfile.Plain);
        var position = VolumePosition.Read(emitter, Binding);
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (position < 0) {
            return;
        }

        emitter.Dispatch(
            TextureVolumes.Noise(
                target,
                position,
                basis,
                profile,
                emitter.Number(nameof(Scale)),
                emitter.Integer(nameof(Octaves)),
                emitter.Number(nameof(Lacunarity)),
                emitter.Number(nameof(Gain)),
                (emitter.Number("Offset X"), emitter.Number("Offset Y"), emitter.Number("Offset Z")),
                emitter.Integer(nameof(Seed))
            )
        );
    }
}

/// <summary>A box or an ellipsoid in the model's own space, read through its position map.</summary>
/// <remarks>
///     <para>
///         <b>One inside the shape and nought outside it, fading over <c>Falloff</c></b> — Patina #179's
///         volume mask. Every coordinate is a fraction of the model's bounding box, the position map's own
///         units, so a centre of a half is the middle of the model and a size of one is all of it along that
///         axis. <c>Position</c> unwired reads the bake's own position map.
///     </para>
///     <para>
///         It is what a mask that should cover "the front of the helmet" or "the bottom third" is made of,
///         and it crosses UV seams as the model does.
///     </para>
///     <para>
///         <b>The default falloff is a tenth of the bounds</b>, a soft edge. A hard one is a step in the
///         position map's own units, so a position baked at sixteen bits moves it by a texel where the
///         eight-bit picture can see it.
///     </para>
/// </remarks>
[Node("Source/Volume Mask", Preview = true, Summary = "A box or an ellipsoid in the model's space, as a mask.")]
sealed partial class VolumeMaskNode : TextureNode {
    /// <summary>Which shape: <c>Box</c> or <c>Ellipsoid</c>.</summary>
    [Setting(AcceptedFrom = typeof(TextureVolumeShape))]
    public string Shape = "Box";

    /// <summary>The position map. Unwired, the bake's own <c>position</c> mesh map.</summary>
    [Input(Name = "Position")]
    public Image Position;

    /// <summary>The shape's centre along the model's first axis, as a fraction of its bounds.</summary>
    [Input(Name = "Centre X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreX = 0.5f;

    /// <summary>The same along the second axis.</summary>
    [Input(Name = "Centre Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreY = 0.5f;

    /// <summary>The same along the third axis.</summary>
    [Input(Name = "Centre Z", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreZ = 0.5f;

    /// <summary>The shape's extent along the first axis, as a fraction of the model's bounds.</summary>
    [Input(Name = "Size X", Minimum = 0f, SoftMaximum = 1f)]
    public Scalar SizeX = 0.5f;

    /// <summary>The same along the second axis.</summary>
    [Input(Name = "Size Y", Minimum = 0f, SoftMaximum = 1f)]
    public Scalar SizeY = 0.5f;

    /// <summary>The same along the third axis.</summary>
    [Input(Name = "Size Z", Minimum = 0f, SoftMaximum = 1f)]
    public Scalar SizeZ = 0.5f;

    /// <summary>How far outside the shape it fades to nought, as a fraction of the model's bounds. Zero is a hard edge.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.5f)]
    public Scalar Falloff = 0.1f;

    /// <summary>The mask.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var shape = TextureSettings.Enum(emitter, nameof(Shape), TextureVolumeShape.Box);
        var position = VolumePosition.Read(emitter, Binding);
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (position < 0) {
            return;
        }

        emitter.Dispatch(
            TextureVolumes.Mask(
                target,
                position,
                shape,
                (emitter.Number("Centre X"), emitter.Number("Centre Y"), emitter.Number("Centre Z")),
                (emitter.Number("Size X"), emitter.Number("Size Y"), emitter.Number("Size Z")),
                emitter.Number(nameof(Falloff))
            )
        );
    }
}

/// <summary>A ramp between two points of the model, read through its position map.</summary>
/// <remarks>
///     <b>
///         Nought at <c>Start</c>, one at <c>End</c>, linear along the line between them and constant
///         across it
///     </b>
///     — Patina #179's two-point position gradient. The defaults run from the bottom of the
///     model to its top. Unlike <c>Generators/Position Gradient</c>, which weighs the three axes, this one
///     is placed by its two ends, so a ramp along a diagonal or over only part of the model is two points
///     rather than arithmetic. Coordinates are fractions of the model's bounding box.
/// </remarks>
[Node("Source/Volume Gradient", Preview = true, Summary = "A ramp from one point of the model to another.")]
sealed partial class VolumeGradientNode : TextureNode {
    /// <summary>The position map. Unwired, the bake's own <c>position</c> mesh map.</summary>
    [Input(Name = "Position")]
    public Image Position;

    /// <summary>Where the ramp is nought, along the model's first axis.</summary>
    [Input(Name = "Start X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar StartX = 0.5f;

    /// <summary>The same along the second axis.</summary>
    [Input(Name = "Start Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar StartY = 0f;

    /// <summary>The same along the third axis.</summary>
    [Input(Name = "Start Z", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar StartZ = 0.5f;

    /// <summary>Where the ramp is one, along the model's first axis.</summary>
    [Input(Name = "End X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar EndX = 0.5f;

    /// <summary>The same along the second axis.</summary>
    [Input(Name = "End Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar EndY = 1f;

    /// <summary>The same along the third axis.</summary>
    [Input(Name = "End Z", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar EndZ = 0.5f;

    /// <summary>The ramp.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var position = VolumePosition.Read(emitter, Binding);
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (position < 0) {
            return;
        }

        emitter.Dispatch(
            TextureVolumes.Gradient(
                target,
                position,
                (emitter.Number("Start X"), emitter.Number("Start Y"), emitter.Number("Start Z")),
                (emitter.Number("End X"), emitter.Number("End Y"), emitter.Number("End Z"))
            )
        );
    }
}

/// <summary>Where the three volume nodes read the model's positions from.</summary>
/// <remarks>
///     <para>
///         <b>The wire when there is one, and the bake's own <c>position</c> map when there is not</b> — Patina
///         #179. A volume node has nothing to show without a position, and the position it wants is the one the
///         mesh being textured was baked with, so asking an author to wire a <c>Source/Mesh Map</c> each time was
///         a step that could only ever be done one way. Unwired, the node asks for <c>meshmap:position</c> exactly
///         as that node would, and reads it directly, the nearest texel where the sizes differ.
///     </para>
///     <para>
///         ⚠ <b>So an unwired volume node is a graph that reads a mesh map</b>, and a host that bakes it with no
///         mesh says so as it does for any mesh map — which is the truth: a volume with no model has no place.
///     </para>
/// </remarks>
static class VolumePosition {
    /// <summary>The image a volume node reads its positions from.</summary>
    /// <param name="emitter">The node's emitter.</param>
    /// <param name="binding">The node's binding, which says whether <c>Position</c> is wired.</param>
    /// <returns>Its index in the plan's image table, or −1 when the wire carries nothing usable.</returns>
    public static int Read(TextureEmitter emitter, NodeBinding binding) {
        if (binding.IsConnected("Position")) {
            return emitter.Read("Position", TextureChannels.Colour);
        }

        // ⚠ Read as it is, with no resample in between. A resample is an op of this node, and an op's seed
        // mixes in its ordinal within the node, so the noise behind one would draw another pattern from the
        // one it draws wired: wiring a Source/Mesh Map would change the noise. The kernels take the nearest
        // position texel instead, which is the same texel whenever the sizes agree. Half-floats, as Source/Mesh
        // Map declares a position: it is a measurement, and a bake may write sixteen bits of it.
        return emitter.External(
            TextureFormat.Rgba16Float,
            TextureChannels.Colour,
            TextureMeshMaps.Reference("position")
        );
    }
}
