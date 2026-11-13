// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Holes in a picture filled from what surrounds them — Patina #178's seam and occlusion removal.</summary>
/// <remarks>
///     <para>
///         <b>A pull-push.</b> The picture is weighed by the mask — kept where it is black, unknown where it is white —
///         and halved down to one texel, each level the weighted mean of what is known beneath it. The way back up
///         keeps every level's known texels and fills the rest from the level below, so a small hole is filled from
///         its rim and a large one from further away, smoothly, with no seam where the filling meets the picture.
///     </para>
///     <para>
///         What it is for: a strip over a scan's seams, a shadow or a hand that got into a photo, a logo to paint
///         out. The fill is smooth, so a large hole in a busy texture reads as a soft patch; follow it with
///         <c>Photo/Tile from Patches</c> or a clone of the texture when that matters. A grey mask blends the fill in
///         by its value.
///     </para>
///     <para>
///         ⚠ <b>Its op count is a function of the bake</b>, one level per halving, so every op of the chain carries
///         <see cref="TextureOp.EmittedForExtent" />. And the pyramid stands on the image's own grid, so rolling the
///         input by part of a block changes the fill a little: it tiles when its input does, and it does not roll.
///     </para>
/// </remarks>
[Node(
    "Photo/Fill Holes",
    Preview = true,
    Summary = "Fills the masked part of a picture from what surrounds it: seams, shadows and blemishes painted out."
)]
sealed partial class FillHolesNode : TextureNode {
    /// <summary>The most halvings, which takes a 4096 picture to one texel.</summary>
    const int MostLevels = 13;

    /// <summary>The picture.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Where to fill: white is filled, black is kept. A single channel.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>The picture with its holes filled.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input", TextureChannels.Colour);
        var mask = emitter.ReadGrey("Mask");
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0 || mask < 0) {
            return;
        }

        var size = emitter.SizeOf(target);
        var level = emitter.LevelOf(target);
        List<int> pyramid = [emitter.Scratch(DeepScratch.Format(emitter), level)];

        emitter.Dispatch(
            new TextureOp {
                Kernel = TexturePhotoKernels.FillSeed,
                Output = pyramid[0],
                Inputs = [source, mask],
                ReadsOtherExtents = true,
                Parameters = []
            }
        );

        // Halved to one texel: the pull. A box resample of premultiplied texels is the weighted mean.
        for (var step = 1; step <= MostLevels && Math.Max(size.X >> (step - 1), size.Y >> (step - 1)) > 1; step++) {
            var smaller = emitter.Scratch(DeepScratch.Format(emitter), level + step);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TextureColourKernels.Resample,
                    Output = smaller,
                    Inputs = [pyramid[^1]],
                    ReadsOtherExtents = true,
                    EmittedForExtent = Longer(emitter, smaller),
                    Parameters = [new("filter", (float)TextureFilter.Box)]
                }
            );
            pyramid.Add(smaller);
        }

        // And back up: the push. The last level of the pull stands in as the first filled one.
        var filled = pyramid[^1];

        // ⚠ From the level above the last, or from the only one: a 1×1 picture is pushed onto itself.
        for (var at = Math.Max(pyramid.Count - 2, 0); at >= 0; at--) {
            var last = at == 0;
            var into = last ? target : emitter.Scratch(DeepScratch.Format(emitter), level + at);

            emitter.Dispatch(
                new TextureOp {
                    Kernel = TexturePhotoKernels.FillPush,
                    Output = into,
                    Inputs = [pyramid[at], filled, source],
                    ReadsOtherExtents = true,
                    OtherExtentInputs = [filled, source],
                    DependsOnEveryTexel = true,
                    EmittedForExtent = Longer(emitter, into),
                    Parameters = [new("gain", last ? 1f : 4f), new("last", last ? 1f : 0f)]
                }
            );
            filled = into;
        }
    }

    static int Longer(TextureEmitter emitter, int image) {
        var size = emitter.SizeOf(image);

        return Math.Max(size.X, size.Y);
    }
}

/// <summary>The format of a scratch that holds a measurement rather than a picture.</summary>
static class DeepScratch {
    /// <summary>The graph's colour format, but never eight bits.</summary>
    /// <param name="emitter">The node's emitter.</param>
    /// <returns>Half float in a graph of eight bits, and the graph's own depth otherwise.</returns>
    /// <remarks>
    ///     A structure tensor, a weighed pyramid and an irradiance are numbers a later op divides or squares, and at
    ///     eight bits a soft region's gradient squares to nought. A graph of 32 bits keeps 32 throughout, which
    ///     <c>TextureFormatDeviceTests</c> holds every node to.
    /// </remarks>
    public static TextureFormat Format(TextureEmitter emitter) {
        var colour = emitter.FormatOf(TextureChannels.Colour);

        return colour == TextureFormat.Rgba8 ? TextureFormat.Rgba16Float : colour;
    }
}
