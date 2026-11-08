// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>The image unrolled about its centre: angle across, distance down.</summary>
[DoesNotTile("Down the image is the distance from the centre, so the top row is one point and the bottom row a circle.")]
[Node("Space/Cartesian to Polar", Preview = true, Summary = "Unrolls the image about its centre: angle across, distance down.")]
sealed partial class CartesianToPolarNode : TextureNode {
    /// <summary>What to unroll.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>A whole turn from left to right, the centre at the top and the inscribed circle at the bottom.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(emitter, (target, source) => Remap.Polar(target, source, toPolar: true));
}

/// <summary>An unrolled image rolled back up about the centre.</summary>
[DoesNotTile("Its left and right edges are opposite sides of a circle, so they read opposite ends of the input.")]
[Node("Space/Polar to Cartesian", Preview = true, Summary = "Rolls an angle-across, distance-down image up about the centre.")]
sealed partial class PolarToCartesianNode : TextureNode {
    /// <summary>A whole turn from left to right, the centre at the top and the inscribed circle at the bottom.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The image rolled up. Outside the inscribed circle it reads the input's bottom row.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(emitter, (target, source) => Remap.Polar(target, source, toPolar: false));
}

/// <summary>The image's four corners moved to four points.</summary>
/// <remarks>
///     The map is bilinear, so straight edges stay straight and evenly divided along them. Outside the
///     quad is transparent black. The defaults are the image's own corners, which is the identity.
/// </remarks>
[Node("Space/Four-Corner Warp", Preview = true, Summary = "Moves the image's four corners to four points.")]
sealed partial class FourCornerWarpNode : TextureNode {
    /// <summary>What to warp.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Where the top-left corner goes, as a fraction of the image.</summary>
    [Input(Name = "Top Left X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar TopLeftX = 0f;

    /// <summary>Its vertical position.</summary>
    [Input(Name = "Top Left Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar TopLeftY = 0f;

    /// <summary>Where the top-right corner goes.</summary>
    [Input(Name = "Top Right X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar TopRightX = 1f;

    /// <summary>Its vertical position.</summary>
    [Input(Name = "Top Right Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar TopRightY = 0f;

    /// <summary>Where the bottom-right corner goes.</summary>
    [Input(Name = "Bottom Right X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar BottomRightX = 1f;

    /// <summary>Its vertical position.</summary>
    [Input(Name = "Bottom Right Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar BottomRightY = 1f;

    /// <summary>Where the bottom-left corner goes.</summary>
    [Input(Name = "Bottom Left X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar BottomLeftX = 0f;

    /// <summary>Its vertical position.</summary>
    [Input(Name = "Bottom Left Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar BottomLeftY = 1f;

    /// <summary>The warped image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(
            emitter,
            (target, source) => new TextureOp {
                Kernel = TextureRemapKernels.CornerWarp,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("topLeftX", emitter.Number("Top Left X")),
                    new("topLeftY", emitter.Number("Top Left Y")),
                    new("topRightX", emitter.Number("Top Right X")),
                    new("topRightY", emitter.Number("Top Right Y")),
                    new("bottomRightX", emitter.Number("Bottom Right X")),
                    new("bottomRightY", emitter.Number("Bottom Right Y")),
                    new("bottomLeftX", emitter.Number("Bottom Left X")),
                    new("bottomLeftY", emitter.Number("Bottom Left Y"))
                ]
            }
        );
}

/// <summary>The image turned about a centre by an angle a map scales per texel.</summary>
[DoesNotTile("It turns the image about one centre, and a turned lattice does not meet itself at the edge.")]
[Node("Space/Rotate by Map", Preview = true, Summary = "Turns the image about a centre by an angle read per texel from a map.")]
sealed partial class RotateByMapNode : TextureNode {
    /// <summary>What to turn.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How far each texel turns, as a fraction of <see cref="Angle" />. Read for its red channel.</summary>
    [Input(Name = "Rotation")]
    public Image Rotation;

    /// <summary>The turn where the map is one, in radians.</summary>
    [Input(SoftMinimum = -3.14159f, SoftMaximum = 3.14159f)]
    public Scalar Angle = 0.785398f;

    /// <summary>The centre, as a fraction of the width.</summary>
    [Input(Name = "Centre X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreX = 0.5f;

    /// <summary>The centre, as a fraction of the height.</summary>
    [Input(Name = "Centre Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreY = 0.5f;

    /// <summary>The turned image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var rotation = emitter.ReadGrey("Rotation");
        var target = emitter.Write("Out");

        if (source < 0 || rotation < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureRemapKernels.RotateByMap,
                Output = target,
                Inputs = [source, rotation],
                Parameters = [
                    new("angle", emitter.Number(nameof(Angle))),
                    new("centreX", emitter.Number("Centre X")),
                    new("centreY", emitter.Number("Centre Y"))
                ]
            }
        );
    }
}

/// <summary>The image twisted about a centre, most at the centre and not at all at a radius.</summary>
[Node("Space/Swirl", Preview = true, Summary = "Twists the image about a centre, fading out at a radius.")]
sealed partial class SwirlNode : TextureNode {
    /// <summary>What to twist.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How far the centre turns, in radians. Negative turns the other way.</summary>
    [Input(SoftMinimum = -6f, SoftMaximum = 6f)]
    public Scalar Angle = 3f;

    /// <summary>Where the twist ends, as a fraction of the width. Everything beyond is unchanged.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Radius = 0.4f;

    /// <summary>The centre, as a fraction of the width.</summary>
    [Input(Name = "Centre X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreX = 0.5f;

    /// <summary>The centre, as a fraction of the height.</summary>
    [Input(Name = "Centre Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreY = 0.5f;

    /// <summary>The twisted image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(
            emitter,
            (target, source) => new TextureOp {
                Kernel = TextureRemapKernels.Swirl,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("angle", emitter.Number(nameof(Angle))),
                    new("radius", emitter.Number(nameof(Radius))),
                    new("centreX", emitter.Number("Centre X")),
                    new("centreY", emitter.Number("Centre Y"))
                ]
            }
        );
}

/// <summary>One wedge of the image repeated round a centre, mirrored every other time.</summary>
[DoesNotTile("Its wedges fan out from one centre, so the image's far edges are different wedges at different radii.")]
[Node("Space/Wedge Symmetry", Preview = true, Summary = "Repeats one wedge round a centre, mirrored like a kaleidoscope.")]
sealed partial class WedgeSymmetryNode : TextureNode {
    /// <summary>What to repeat.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many wedges make the circle.</summary>
    [Input(Minimum = 1f, Maximum = 64f)]
    public Int Wedges = 6;

    /// <summary>Where the first wedge starts, in radians from the right.</summary>
    [Input(SoftMinimum = -3.14159f, SoftMaximum = 3.14159f)]
    public Scalar Rotation = 0f;

    /// <summary>The centre, as a fraction of the width.</summary>
    [Input(Name = "Centre X", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreX = 0.5f;

    /// <summary>The centre, as a fraction of the height.</summary>
    [Input(Name = "Centre Y", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar CentreY = 0.5f;

    /// <summary>The symmetric image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        Smoothing.Filter(
            emitter,
            (target, source) => new TextureOp {
                Kernel = TextureRemapKernels.WedgeSymmetry,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("wedges", emitter.Integer(nameof(Wedges))),
                    new("rotation", emitter.Number(nameof(Rotation))),
                    new("centreX", emitter.Number("Centre X")),
                    new("centreY", emitter.Number("Centre Y"))
                ]
            }
        );
}

/// <summary>The image cropped to the box its content occupies, and scaled to fill.</summary>
/// <remarks>
///     ⚠ <b>It reads every texel of its input before it writes one</b>, as <c>Colour/Auto Levels</c> does:
///     the box is a reduction ladder down to one texel, at 32 bits so that its edges land on texels at
///     4K. An image with no content is passed through unchanged.
/// </remarks>
[Node("Space/Trim to Content", Preview = true, Summary = "Crops to the box the content occupies, and scales it to fill.")]
sealed partial class TrimToContentNode : TextureNode {
    /// <summary>What to trim.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>
    ///     What a texel's value must exceed to count as content: its alpha, or else the largest of its colour
    ///     channels.
    /// </summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Threshold = 0f;

    /// <summary>What is kept round the content, as a fraction of the image.</summary>
    [Input(Minimum = 0f, SoftMaximum = 0.25f)]
    public Scalar Margin = 0f;

    /// <summary>Whether content is told by alpha rather than by colour.</summary>
    [Input(Name = "Use Alpha")]
    public Bool UseAlpha = false;

    /// <summary>Whether the box fills the image on both axes. Off keeps the content's proportions.</summary>
    [Input]
    public Bool Stretch = false;

    /// <summary>The trimmed image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        var size = emitter.SizeOf(source);
        var level = emitter.LevelOf(source);
        var levels = TextureAdjust.ReductionLevels(size.X, size.Y);
        var scratch = ImmutableArray.CreateBuilder<int>(levels.Length);

        foreach (var rung in levels) {
            scratch.Add(emitter.Scratch(TextureFormat.Rgba32Float, level + rung));
        }

        var ladder = scratch.ToImmutable();
        var threshold = emitter.Number(nameof(Threshold));
        var alpha = emitter.Flag("Use Alpha") ? 1f : 0f;

        emitter.Dispatch(
            [
                .. TextureAdjust.Reduction(source, ladder, size.X, size.Y, TextureRemapKernels.BoundsReduce)
                    .Select(op => op with { Parameters = [.. op.Parameters, new("threshold", threshold), new("useAlpha", alpha)] })
            ]
        );

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureRemapKernels.Trim,
                Output = target,
                Inputs = [source, ladder[^1]],
                ReadsOtherExtents = true,
                OtherExtentInputs = [ladder[^1]],
                DependsOnEveryTexel = true,
                Parameters = [
                    new("margin", emitter.Number(nameof(Margin))),
                    new("stretch", emitter.Flag(nameof(Stretch)) ? 1f : 0f)
                ]
            }
        );
    }
}

/// <summary>What the remap nodes share.</summary>
static class Remap {
    /// <summary>The polar op, either way.</summary>
    /// <param name="target">The image written.</param>
    /// <param name="source">The image read.</param>
    /// <param name="toPolar">Whether square goes to polar rather than back.</param>
    /// <returns>The op.</returns>
    public static TextureOp Polar(int target, int source, bool toPolar) =>
        new() {
            Kernel = TextureRemapKernels.Polar,
            Output = target,
            Inputs = [source],
            Parameters = [new("toPolar", toPolar ? 1f : 0f)]
        };
}
