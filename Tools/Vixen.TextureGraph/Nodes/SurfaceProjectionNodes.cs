// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>A picture projected through a box placed on the model: a decal, perhaps warped by a grid.</summary>
/// <remarks>
///     <para>
///         <b>Patina #133's decal and #134's warp, as a node</b>; <c>LayerStackGraph.Surfaced</c> is the stack's wiring.
///         Every length is in the model's units, and the box the <c>position</c> bake is normalised in turns a texel's
///         position back into them, so a square decal stays square on a model whose bounds are not a cube.
///     </para>
///     <para>
///         ⚠ <b>Outside the box, and on a face turned away from it, the result is transparent</b>, so a layer reading
///         it composites nothing there. The picture's own alpha is kept, times the fade through the box's depth.
///     </para>
///     <para>
///         ⚠ <b>Not a layer filter</b>, though its first image is a picture: it needs a box placed on the model, which a
///         filter over the layers beneath has no way to say. A fill's <c>Decal</c> projection is how a stack uses it.
///     </para>
/// </remarks>
[DoesNotTile("Its picture is a projection through mesh positions, so its edges are wherever the atlas put them.")]
[Node(
    "Space/Decal",
    Adjustment = false,
    Preview = true,
    Summary = "A picture projected through a box placed on a mesh, nothing outside it, perhaps warped by a grid."
)]
sealed partial class DecalNode : TextureNode {
    /// <summary>The picture to project.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>§ D12's <c>position</c> bake.</summary>
    [Input(Name = "Position")]
    public Image Position;

    /// <summary>§ D12's <c>world</c> bake. ⚠ The world normal, not the tangent-space one.</summary>
    [Input(Name = "Normal")]
    public Image Normal;

    /// <summary>The box's middle, along the first axis, in the model's units.</summary>
    [Input(Name = "Centre X")]
    public Scalar CentreX = 0.5f;

    /// <summary>The box's middle, along the second axis.</summary>
    [Input(Name = "Centre Y")]
    public Scalar CentreY = 0.5f;

    /// <summary>The box's middle, along the third axis.</summary>
    [Input(Name = "Centre Z")]
    public Scalar CentreZ = 0.5f;

    /// <summary>The way the decal faces, out of the surface: its first coordinate.</summary>
    [Input(Name = "Facing X", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar FacingX = 0f;

    /// <summary>The way the decal faces: its second coordinate.</summary>
    [Input(Name = "Facing Y", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar FacingY = 0f;

    /// <summary>The way the decal faces: its third coordinate.</summary>
    [Input(Name = "Facing Z", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar FacingZ = 1f;

    /// <summary>Up on the picture, squared to the facing: its first coordinate.</summary>
    [Input(Name = "Up X", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar UpX = 0f;

    /// <summary>Up on the picture: its second coordinate.</summary>
    [Input(Name = "Up Y", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar UpY = 1f;

    /// <summary>Up on the picture: its third coordinate.</summary>
    [Input(Name = "Up Z", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar UpZ = 0f;

    /// <summary>How wide the picture lands, in the model's units.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Width = 0.25f;

    /// <summary>How tall it lands.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Height = 0.25f;

    /// <summary>How deep the box reaches, through the surface and out of it.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Depth = 0.25f;

    /// <summary>How far a face may turn from the decal and still take it, in radians.</summary>
    [Input(Minimum = 0f, Maximum = MathF.PI)]
    public Scalar Angle = 1.3f;

    /// <summary>How much of the depth, from each face inwards, the picture fades over.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Fade = 0.25f;

    /// <summary>The least corner of the box the position bake is normalised in: its first coordinate.</summary>
    [Input(Name = "Box Min X")]
    public Scalar BoxMinX = 0f;

    /// <summary>The least corner: its second coordinate.</summary>
    [Input(Name = "Box Min Y")]
    public Scalar BoxMinY = 0f;

    /// <summary>The least corner: its third coordinate.</summary>
    [Input(Name = "Box Min Z")]
    public Scalar BoxMinZ = 0f;

    /// <summary>The greatest corner: its first coordinate.</summary>
    [Input(Name = "Box Max X")]
    public Scalar BoxMaxX = 1f;

    /// <summary>The greatest corner: its second coordinate.</summary>
    [Input(Name = "Box Max Y")]
    public Scalar BoxMaxY = 1f;

    /// <summary>The greatest corner: its third coordinate.</summary>
    [Input(Name = "Box Max Z")]
    public Scalar BoxMaxZ = 1f;

    /// <summary>Whether the picture repeats past its edges.</summary>
    [Setting(
        Name = "Tiling",
        Summary = "Clamp leaves nothing past the picture's edges; Tile repeats it.",
        Accepted = ["Clamp", "Tile"]
    )]
    public string Tiling = "Clamp";

    /// <summary>How the picture is read between its pixels.</summary>
    [Setting(
        Name = "Sampling",
        Summary = "Bilinear mixes four pixels; Point takes the nearest.",
        Accepted = ["Bilinear", "Point"]
    )]
    public string Sampling = "Bilinear";

    /// <summary>The warp's grid as eighteen numbers, or empty for none.</summary>
    [Setting(
        Name = "Warp",
        Summary = "Where each point of a 3 by 3 grid over the picture lands on the decal's square, row by row from "
            + "the top left, as eighteen numbers 0 to 1 across and down. Empty bends nothing."
    )]
    public string Warp = "";

    /// <summary>The projected picture, in the atlas, transparent where it does not land.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var position = emitter.Read("Position");
        var normal = emitter.Read("Normal");
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0 || position < 0 || normal < 0) {
            return;
        }

        var size = new Vector3(
            emitter.Number(nameof(Width)),
            emitter.Number(nameof(Height)),
            emitter.Number(nameof(Depth))
        );

        if (!(size.X > 0f && size.Y > 0f && size.Z > 0f)) {
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                "A decal's width, height and depth are above zero.",
                nameof(Width)
            );

            return;
        }

        var frame = SurfaceFrames.Decal(
            new(emitter.Number("Centre X"), emitter.Number("Centre Y"), emitter.Number("Centre Z")),
            new(emitter.Number("Facing X"), emitter.Number("Facing Y"), emitter.Number("Facing Z")),
            new(emitter.Number("Up X"), emitter.Number("Up Y"), emitter.Number("Up Z")),
            size
        );

        if (frame is not { } placed) {
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                "A decal faces some way: its facing has no length.",
                "Facing X"
            );

            return;
        }

        var warp = SurfaceFrames.Numbers(emitter.Text("Warp"));

        if (warp is null || (warp.Length != 0 && warp.Length != TextureProjections.Unwarped.Length)) {
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                $"'Warp' holds '{emitter.Text("Warp")}', and a warp is eighteen numbers or none.",
                "Warp"
            );

            return;
        }

        emitter.Dispatch(
            TextureProjections.Decal(
                target,
                source,
                position,
                normal,
                SurfaceFrames.Box(emitter),
                placed,
                MathF.Cos(emitter.Number(nameof(Angle))),
                emitter.Number(nameof(Fade)),
                string.Equals(emitter.Text("Tiling").Trim(), "Tile", StringComparison.OrdinalIgnoreCase),
                string.Equals(emitter.Text("Sampling").Trim(), "Point", StringComparison.OrdinalIgnoreCase),
                warp
            )
        );
    }
}

/// <summary>A stroke or a ribbon along a polyline placed on the model.</summary>
/// <remarks>
///     <para>
///         <b>Patina #130's path and #131's ribbon, as a node</b>; <c>LayerStackGraph.Surfaced</c> samples the curve the
///         artist placed into the polyline this reads, through <c>PaintPath</c>'s centripetal Catmull-Rom. Each texel is
///         covered by how far its place on the model is from the polyline against the width there; a ribbon reads its
///         picture along the polyline, a stroke where the texel is.
///     </para>
///     <para>
///         ⚠ <b>The points are a setting, seven numbers to a point</b> — a place, a normal and a pressure — and become a
///         32-bit table the kernel reads, so a path is in the document and compiles with no host to resolve it.
///     </para>
///     <para>
///         ⚠ <b>Not a layer filter</b>, for the decal's reason: a fill's <c>Path</c> projection is how a stack uses it.
///     </para>
/// </remarks>
[DoesNotTile("Its coverage is a distance through mesh positions, so its edges are wherever the atlas put them.")]
[Node(
    "Space/Surface Path",
    Adjustment = false,
    Preview = true,
    Summary = "A stroke along a polyline placed on a mesh, or a picture laid along it as a ribbon."
)]
sealed partial class SurfacePathNode : TextureNode {
    /// <summary>The picture: shown through the path, or laid along it.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>§ D12's <c>position</c> bake.</summary>
    [Input(Name = "Position")]
    public Image Position;

    /// <summary>§ D12's <c>world</c> bake. ⚠ The world normal, not the tangent-space one.</summary>
    [Input(Name = "Normal")]
    public Image Normal;

    /// <summary>Half the path's width at full pressure, in the model's units.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.1f)]
    public Scalar Radius = 0.01f;

    /// <summary>How much of the width is at full strength.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Hardness = 0.5f;

    /// <summary>How far a face may turn from the path and still take it, in radians.</summary>
    [Input(Minimum = 0f, Maximum = MathF.PI)]
    public Scalar Angle = 1.3f;

    /// <summary>How far along the path a ribbon's picture repeats, or zero to keep its shape.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Repeat = 0f;

    /// <summary>The least corner of the box the position bake is normalised in: its first coordinate.</summary>
    [Input(Name = "Box Min X")]
    public Scalar BoxMinX = 0f;

    /// <summary>The least corner: its second coordinate.</summary>
    [Input(Name = "Box Min Y")]
    public Scalar BoxMinY = 0f;

    /// <summary>The least corner: its third coordinate.</summary>
    [Input(Name = "Box Min Z")]
    public Scalar BoxMinZ = 0f;

    /// <summary>The greatest corner: its first coordinate.</summary>
    [Input(Name = "Box Max X")]
    public Scalar BoxMaxX = 1f;

    /// <summary>The greatest corner: its second coordinate.</summary>
    [Input(Name = "Box Max Y")]
    public Scalar BoxMaxY = 1f;

    /// <summary>The greatest corner: its third coordinate.</summary>
    [Input(Name = "Box Max Z")]
    public Scalar BoxMaxZ = 1f;

    /// <summary>Stroke or Ribbon.</summary>
    [Setting(
        Name = "Mode",
        Summary = "Stroke shows the picture through the path where it lies on the atlas; Ribbon lays it along the path.",
        Accepted = ["Stroke", "Ribbon"]
    )]
    public string Mode = "Stroke";

    /// <summary>The polyline, as text.</summary>
    [Setting(
        Name = "Points",
        Summary = "The polyline, one point to each part between semicolons: its place, its normal and its pressure, "
            + "seven numbers in the model's units."
    )]
    public string Points = "";

    /// <summary>The path, in the atlas, transparent where it does not reach.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var position = emitter.Read("Position");
        var normal = emitter.Read("Normal");
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0 || position < 0 || normal < 0) {
            return;
        }

        var numbers = SurfaceFrames.Numbers(emitter.Text("Points").Replace(';', ' '));

        if (numbers is null || numbers.Length % 7 != 0 || numbers.Length / 7 > TextureSurfacePaths.MaximumPoints) {
            emitter.Report(
                TextureDiagnostics.SettingNotAccepted,
                $"'Points' is seven numbers to a point and at most {TextureSurfacePaths.MaximumPoints} points.",
                "Points"
            );

            return;
        }

        var radius = emitter.Number(nameof(Radius));

        if (!(radius > 0f)) {
            emitter.Report(TextureDiagnostics.SettingNotAccepted, "A path's radius is above zero.", nameof(Radius));

            return;
        }

        List<Vector3> places = [];
        List<Vector3> normals = [];
        List<float> pressures = [];

        for (var at = 0; at < numbers.Length; at += 7) {
            places.Add(new(numbers[at], numbers[at + 1], numbers[at + 2]));
            normals.Add(new(numbers[at + 3], numbers[at + 4], numbers[at + 5]));
            pressures.Add(Math.Clamp(numbers[at + 6], 0f, 1f));
        }

        // An empty path covers nothing and still compiles: one point at no pressure, and a reach no texel is inside.
        var empty = places.Count == 0;

        if (empty) {
            places.Add(default);
            normals.Add(default);
            pressures.Add(0f);
        }

        var least = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        var most = new Vector3(float.MinValue, float.MinValue, float.MinValue);

        foreach (var place in places) {
            least = new(MathF.Min(least.X, place.X), MathF.Min(least.Y, place.Y), MathF.Min(least.Z, place.Z));
            most = new(MathF.Max(most.X, place.X), MathF.Max(most.Y, place.Y), MathF.Max(most.Z, place.Z));
        }

        var grown = empty ? -1f : radius;
        var table = emitter.External(
            TextureFormat.Rgba32Float,
            TextureChannels.Colour,
            places.Count,
            2,
            TextureSurfacePaths.Table(places, normals, pressures)
        );

        if (table < 0) {
            return;
        }

        var op = TextureProjections.SurfacePath(
            target,
            source,
            position,
            normal,
            table,
            places.Count,
            SurfaceFrames.Box(emitter),
            least - new Vector3(grown, grown, grown),
            most + new Vector3(grown, grown, grown),
            radius,
            emitter.Number(nameof(Hardness)),
            MathF.Cos(emitter.Number(nameof(Angle))),
            string.Equals(emitter.Text("Mode").Trim(), "Ribbon", StringComparison.OrdinalIgnoreCase),
            emitter.Number(nameof(Repeat))
        );

        // ⚠ The table is read by column and not at the coordinate being written, so its extent is its own.
        emitter.Dispatch(op with { ReadsOtherExtents = true, OtherExtentInputs = [table] });
    }
}

/// <summary>What the decal and path nodes share: a box from six ports, numbers from a setting, a square frame.</summary>
static class SurfaceFrames {
    /// <summary>The box the position bake is normalised in, from the six box ports.</summary>
    /// <param name="emitter">The node being compiled.</param>
    /// <returns>The box.</returns>
    public static TextureModelBox Box(TextureEmitter emitter) {
        var least = new Vector3(emitter.Number("Box Min X"), emitter.Number("Box Min Y"), emitter.Number("Box Min Z"));
        var most = new Vector3(emitter.Number("Box Max X"), emitter.Number("Box Max Y"), emitter.Number("Box Max Z"));

        return new(least, most - least);
    }

    /// <summary>Numbers written with spaces or commas between them, or null where one does not read.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The numbers, empty for empty text.</returns>
    public static float[]? Numbers(string text) {
        var parts = text.Split([' ', ',', '\n', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
        var numbers = new float[parts.Length];

        for (var at = 0; at < parts.Length; at++) {
            if (!float.TryParse(parts[at], CultureInfo.InvariantCulture, out numbers[at])) {
                return null;
            }
        }

        return numbers;
    }

    /// <summary>A decal's box with square unit axes, or null when its facing has no length.</summary>
    /// <param name="centre">The box's middle.</param>
    /// <param name="facing">The way it faces, any length.</param>
    /// <param name="up">Up on the picture, any length and not necessarily square to the facing.</param>
    /// <param name="size">Its width, height and depth.</param>
    /// <returns>The frame.</returns>
    /// <remarks>
    ///     Up is squared to the facing; an up along the facing falls back to whichever world axis is not, so a decal
    ///     looking straight down still has a way up.
    /// </remarks>
    public static TextureDecalFrame? Decal(Vector3 centre, Vector3 facing, Vector3 up, Vector3 size) {
        if (!(facing.LengthSquared() > 1e-12f)) {
            return null;
        }

        var forward = Vector3.Normalize(facing);
        var squared = up - (forward * Vector3.Dot(up, forward));

        if (!(squared.LengthSquared() > 1e-8f)) {
            var axis = MathF.Abs(forward.Y) < 0.9f ? new Vector3(0f, 1f, 0f) : new Vector3(0f, 0f, -1f);

            squared = axis - (forward * Vector3.Dot(axis, forward));
        }

        var upward = Vector3.Normalize(squared);
        var right = Vector3.Cross(upward, forward);

        return new(centre, right, upward, forward, size);
    }
}
