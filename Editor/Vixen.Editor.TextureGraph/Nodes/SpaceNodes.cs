// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>An axis, a line, and a reflect or a flip about it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The two modes obey different laws, and the node's summary must not blur them.</b>
///         <c>Flip</c> reverses the whole image about the line and is its own inverse; <c>Reflect</c>
///         copies one half over the other and is idempotent — twice is once. On a symmetric image
///         they look the same, which is how that distinction gets lost.
///     </para>
///     <para>
///         <b><c>Reflect</c> is the one that makes a tileable half out of an untileable whole</b>,
///         which is why it is the default rather than the more obvious flip.
///     </para>
/// </remarks>
[Node("Space/Mirror", Preview = true, Summary = "One half copied over the other, or the whole image reversed.")]
sealed partial class MirrorNode : TextureNode {
    /// <summary>Which way it folds: <c>X</c>, <c>Y</c> or <c>Corner</c>.</summary>
    [Setting]
    public string Axis = "X";

    /// <summary>What it does with the fold: <c>Reflect</c> or <c>Flip</c>.</summary>
    [Setting]
    public string Mode = "Reflect";

    /// <summary>What to fold.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Where the line is, from 0 to 1 across the axis.</summary>
    /// <remarks>
    ///     ⚠ Only at 0.5 is a flip an exact involution: anywhere else part of the image mirrors
    ///     outside itself, the load clamps, and a clamp cannot be undone. See <c>Mirror.rvn</c>.
    /// </remarks>
    [Input]
    public Scalar Offset = 0.5f;

    /// <summary>The folded image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var axis = TextureSettings.Enum(emitter, nameof(Axis), TextureMirrorAxis.X);
        var mode = TextureSettings.Enum(emitter, nameof(Mode), TextureMirrorMode.Reflect);
        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Mirror,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("axis", (float)axis),
                    new("mode", (float)mode),
                    new("offset", emitter.Number(nameof(Offset)))
                ]
            }
        );
    }
}

/// <summary>An integer repeat, with a shift per tile row and column.</summary>
/// <remarks>
///     <b>The per-tile offset is what makes it a brick node rather than a <c>frac</c>.</b> A four by
///     four repeat with an x offset of a half shifts every other row by half a tile, which is a
///     running bond; both offsets at zero is a plain grid. ⚠ The repeats are counts and the offsets
///     are fractions of a tile, so nothing here is a length and the same node is the same picture at
///     every bake resolution.
/// </remarks>
[Node("Space/Tile", Preview = true, Summary = "An integer repeat, with a shift per tile row and column.")]
sealed partial class TileNode : TextureNode {
    /// <summary>What to repeat.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many times across. Below one is clamped by the kernel.</summary>
    [Input(Name = "Repeat X")]
    public Int RepeatX = 1;

    /// <summary>How many times down.</summary>
    [Input(Name = "Repeat Y")]
    public Int RepeatY = 1;

    /// <summary>How far each successive tile row is shifted along x, in tiles.</summary>
    [Input(Name = "Offset X")]
    public Scalar OffsetX = 0f;

    /// <summary>How far each successive tile column is shifted along y, in tiles.</summary>
    [Input(Name = "Offset Y")]
    public Scalar OffsetY = 0f;

    /// <summary>The repeated image.</summary>
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

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Tile,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("repeatX", emitter.Integer("Repeat X")),
                    new("repeatY", emitter.Integer("Repeat Y")),
                    new("offsetX", emitter.Number("Offset X")),
                    new("offsetY", emitter.Number("Offset Y"))
                ]
            }
        );
    }
}

/// <summary>A rectangle of the source, onto the whole of the target.</summary>
/// <remarks>
///     <para>
///         <b>The rect is in the source's own normalised space and the target's size is the
///         plan's</b>, so cropping to a quarter and writing a same-sized image is a crop <em>and</em>
///         a 2× magnification. That is a thing artists ask for and it is the only 1:1 crop this
///         resolution model can express — <c>TextureImage</c> sizes an image by a power-of-two level
///         offset, and a crop to 37% of the width has no image to write into.
///     </para>
///     <para>
///         ⚠ <b><c>Point</c> is the default and it is not a quality setting.</b> Where the rect lands
///         on texel boundaries a point crop is exact, texel for texel — which is the property that
///         makes a crop a crop rather than a crop plus a half-texel drift, the error this kernel's
///         arithmetic is actually prone to.
///     </para>
/// </remarks>
[Node("Space/Crop", Preview = true, Summary = "A rectangle of the source, stretched onto the whole target.")]
sealed partial class CropNode : TextureNode {
    /// <summary>How a sub-sample reads: <c>Point</c> or <c>Bilinear</c>. ⚠ Not <c>Box</c>.</summary>
    [Setting]
    public string Filter = "Point";

    /// <summary>What to crop.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The rect's left edge, from 0 to 1 across the source.</summary>
    [Input(Name = "Rect X")]
    public Scalar RectX = 0f;

    /// <summary>Its top edge.</summary>
    [Input(Name = "Rect Y")]
    public Scalar RectY = 0f;

    /// <summary>Its width, from 0 to 1.</summary>
    [Input(Name = "Rect W")]
    public Scalar RectW = 1f;

    /// <summary>Its height.</summary>
    [Input(Name = "Rect H")]
    public Scalar RectH = 1f;

    /// <summary>The cropped image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var filter = TextureSettings.Enum(emitter, nameof(Filter), TextureFilter.Point);
        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        if (filter == TextureFilter.Box) {
            // ⚠ Refused rather than passed through, for `Transform2DNode`'s reason: `Crop.rvn`
            // compares `filter` against 0 and interpolates for everything else, so a `Box` here is a
            // bilinear crop drawn under the name of a box filter. Only `Resample` has a ratio to box
            // over, because only there is the target's size the whole of the answer.
            emitter.Report(
                "TG0010",
                $"'{nameof(Filter)}' is 'Box', and '{TextureColourKernels.Crop}' takes Point or Bilinear. A box "
                + "needs a minification ratio the kernel can work out, which a crop onto an arbitrary rect does "
                + "not give it.",
                nameof(Filter)
            );

            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Crop,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("rectX", emitter.Number("Rect X")),
                    new("rectY", emitter.Number("Rect Y")),
                    new("rectW", emitter.Number("Rect W")),
                    new("rectH", emitter.Number("Rect H")),
                    new("filter", (float)filter)
                ]
            }
        );
    }
}

/// <summary>How far a <c>Resample</c> moves its output from the image it reads, in mip levels.</summary>
/// <remarks>
///     ⚠ <b>The member's value <em>is</em> the level offset, sign and all</b> — a positive level is a
///     halving, because that is <see cref="TextureImage.LevelOffset" />'s currency and there is
///     nothing to convert. A second spelling of "smaller" would be one more place to get the sign
///     wrong, and the picture under a wrong guess is a plausible one.
/// </remarks>
enum TextureResampleSize {
    /// <summary>Four times as wide and four times as tall.</summary>
    Quadruple = -2,

    /// <summary>Twice as wide and twice as tall.</summary>
    Double = -1,

    /// <summary>⚠ The same size, which is a copy and not a resample.</summary>
    Same = 0,

    /// <summary>Half as wide and half as tall.</summary>
    Half = 1,

    /// <summary>A quarter.</summary>
    Quarter = 2
}

/// <summary>The same picture at another resolution.</summary>
/// <remarks>
///     <para>
///         <b>The target's size is the scale, so there is no scale parameter</b> — <c>Resample.rvn</c>
///         says so, and it is the same argument <see cref="TextureOp" /> makes for carrying no
///         resolution: a ratio on the op as well as on the image is a second place for a resolution
///         to be wrong.
///     </para>
///     <para>
///         ⚠ <b>Which is exactly why this node could not exist until
///         <a href="https://github.com/Rikarin/Vixen/issues/733">#733</a>.</b> Every image a node
///         allocated was at the plan's base level, so a Resample writing its output at its input's
///         size was an <em>identity copy</em> — a node that draws a perfectly plausible picture and
///         does nothing. <see cref="TextureEmitter.Write(string,TextureChannels,int)" /> is the level
///         it needed, and <see cref="Size" /> at <see cref="TextureResampleSize.Same" /> is still
///         that copy, so it says so.
///     </para>
///     <para>
///         <b><c>Box</c> going down and <c>Bilinear</c> going up.</b> Halving with <c>Point</c> keeps
///         one texel in four and drops the rest; the closed form is worth stating because it is as
///         far apart as two pictures get — a column checkerboard boxed down by any integer factor is
///         0.5 everywhere and point-sampled down is 0 or 1 everywhere.
///     </para>
/// </remarks>
[Node("Space/Resample", Preview = true, Summary = "The same picture at half, a quarter, twice or four times the size.")]
sealed partial class ResampleNode : TextureNode {
    /// <summary>
    ///     How far to move: <c>Quarter</c>, <c>Half</c>, <c>Same</c>, <c>Double</c> or
    ///     <c>Quadruple</c>, relative to the image arriving.
    /// </summary>
    [Setting]
    public string Size = "Half";

    /// <summary>How a sample reads: <c>Point</c>, <c>Bilinear</c> or <c>Box</c>.</summary>
    [Setting]
    public string Filter = "Box";

    /// <summary>What to resample.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The resampled image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var size = TextureSettings.Enum(emitter, nameof(Size), TextureResampleSize.Half);
        var filter = TextureSettings.Enum(emitter, nameof(Filter), TextureFilter.Box);
        var source = emitter.Read("Input");

        if (source < 0) {
            return;
        }

        if (size == TextureResampleSize.Same) {
            // ⚠ A warning rather than a refusal, because the plan it produces is sound — it is a copy
            // — and because the *reason* it is a copy is the thing an author cannot see. This is the
            // failure #733 describes, said at the one moment somebody can act on it.
            emitter.Report(
                "TG0018",
                $"'{nameof(Size)}' is 'Same', so this resamples an image onto one of its own size — which is a "
                + "copy, at the cost of a dispatch and a texture. The target's size is the scale; pick another.",
                nameof(Size),
                NodeSeverity.Warning
            );
        }

        // Relative to the image arriving rather than to the graph's base, so that two Resamples in a
        // row each halve. A bare offset would make the second one a no-op.
        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Resample,
                Output = emitter.Write("Out", emitter.Resolved, emitter.LevelOf(source) + (int)size),
                Inputs = [source],
                Parameters = [new("filter", (float)filter)]
            }
        );
    }
}
