// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>Each colour channel held inside a range.</summary>
[Node("Colour/Clamp", Preview = true, Summary = "Each colour channel held between a low and a high value.")]
sealed partial class ClampNode : TextureNode {
    /// <summary>What to clamp.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The lowest a channel may be.</summary>
    [Input(SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar Low = 0f;

    /// <summary>The highest a channel may be.</summary>
    [Input(SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar High = 1f;

    /// <summary>The clamped image. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(
            emitter,
            TextureColourOperators.Clamp,
            new("low", emitter.Number(nameof(Low))),
            new("high", emitter.Number(nameof(High)))
        );
}

/// <summary>Each colour channel to nought or one about a level.</summary>
[Node("Colour/Threshold", Preview = true, Summary = "Each colour channel to black or white about a level.")]
sealed partial class ThresholdNode : TextureNode {
    /// <summary>What to cut.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Where the step is. A value exactly at it goes to one.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Level = 0.5f;

    /// <summary>How wide the step is, centred on the level. Zero is a hard edge.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Softness = 0f;

    /// <summary>Nought or one per channel, or a smooth step between them. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(
            emitter,
            TextureColourOperators.Threshold,
            new("level", emitter.Number(nameof(Level))),
            new("softness", emitter.Number(nameof(Softness)))
        );
}

/// <summary>Each colour channel onto a few evenly spaced values.</summary>
[Node("Colour/Quantise", Preview = true, Summary = "Each colour channel onto a number of even steps.")]
sealed partial class QuantiseNode : TextureNode {
    /// <summary>What to quantise.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many values a channel may take, nought and one included.</summary>
    [Input(Minimum = 2f, Maximum = 256f)]
    public Int Levels = 4;

    /// <summary>The stepped image. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(
            emitter,
            TextureColourOperators.Quantise,
            new TextureParameter("levels", emitter.Integer(nameof(Levels)))
        );
}

/// <summary>Each output colour channel as a weighted sum of the input ones, plus an offset.</summary>
/// <remarks>
///     Where <c>Colour/Channel Shuffle</c> picks one channel per output, this mixes them. The defaults are
///     the identity. Nothing is normalised, so a negative weight is how a difference of two channels is
///     spelled.
/// </remarks>
[Node("Colour/Channel Mix", Preview = true, Summary = "Each output channel a weighted sum of red, green and blue.")]
sealed partial class ChannelMixNode : TextureNode {
    /// <summary>What to mix.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How much of the input's red goes into the output's red.</summary>
    [Input(Name = "Red from Red", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar RedFromRed = 1f;

    /// <summary>How much of the input's green goes into the output's red.</summary>
    [Input(Name = "Red from Green", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar RedFromGreen = 0f;

    /// <summary>How much of the input's blue goes into the output's red.</summary>
    [Input(Name = "Red from Blue", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar RedFromBlue = 0f;

    /// <summary>What is added to the output's red.</summary>
    [Input(Name = "Red Offset", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar RedOffset = 0f;

    /// <summary>How much of the input's red goes into the output's green.</summary>
    [Input(Name = "Green from Red", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar GreenFromRed = 0f;

    /// <summary>How much of the input's green goes into the output's green.</summary>
    [Input(Name = "Green from Green", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar GreenFromGreen = 1f;

    /// <summary>How much of the input's blue goes into the output's green.</summary>
    [Input(Name = "Green from Blue", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar GreenFromBlue = 0f;

    /// <summary>What is added to the output's green.</summary>
    [Input(Name = "Green Offset", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar GreenOffset = 0f;

    /// <summary>How much of the input's red goes into the output's blue.</summary>
    [Input(Name = "Blue from Red", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar BlueFromRed = 0f;

    /// <summary>How much of the input's green goes into the output's blue.</summary>
    [Input(Name = "Blue from Green", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar BlueFromGreen = 0f;

    /// <summary>How much of the input's blue goes into the output's blue.</summary>
    [Input(Name = "Blue from Blue", SoftMinimum = -2f, SoftMaximum = 2f)]
    public Scalar BlueFromBlue = 1f;

    /// <summary>What is added to the output's blue.</summary>
    [Input(Name = "Blue Offset", SoftMinimum = -1f, SoftMaximum = 1f)]
    public Scalar BlueOffset = 0f;

    /// <summary>The mixed colour. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input", TextureChannels.Colour);

        // Colour whatever arrives: a grey in is a colour out as soon as two rows differ.
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourOperators.ChannelMix,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("redFromRed", emitter.Number("Red from Red")),
                    new("redFromGreen", emitter.Number("Red from Green")),
                    new("redFromBlue", emitter.Number("Red from Blue")),
                    new("redOffset", emitter.Number("Red Offset")),
                    new("greenFromRed", emitter.Number("Green from Red")),
                    new("greenFromGreen", emitter.Number("Green from Green")),
                    new("greenFromBlue", emitter.Number("Green from Blue")),
                    new("greenOffset", emitter.Number("Green Offset")),
                    new("blueFromRed", emitter.Number("Blue from Red")),
                    new("blueFromGreen", emitter.Number("Blue from Green")),
                    new("blueFromBlue", emitter.Number("Blue from Blue")),
                    new("blueOffset", emitter.Number("Blue Offset"))
                ]
            }
        );
    }
}

/// <summary>A grey read through a ramp that is itself an image.</summary>
/// <remarks>
///     <c>Colour/Gradient Map</c>'s ramp is a setting the CPU bakes; this one is wired, so it can be a
///     photograph's row or be driven by anything upstream.
/// </remarks>
[Node("Colour/Image Ramp", Preview = true, Summary = "A grey mapped through a row of another image.")]
sealed partial class ImageRampNode : TextureNode {
    /// <summary>The grey that picks a position along the ramp: nought is its left end and one its right.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The ramp, read along one row at its own size.</summary>
    [Input(Name = "Ramp")]
    public Image Ramp;

    /// <summary>Which row of the ramp is read, from nought at the top to one at the bottom.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Row = 0.5f;

    /// <summary>The mapped colour, alpha included.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.ReadGrey("Input");
        var ramp = emitter.Read("Ramp", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0 || ramp < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourOperators.ImageRamp,
                Output = target,
                Inputs = [source, ramp],

                // The ramp is read at its own size, whatever it is.
                ReadsOtherExtents = true,
                OtherExtentInputs = [ramp],
                Parameters = [new("row", emitter.Number(nameof(Row)))]
            }
        );
    }
}

/// <summary>The whole image shifted in hue, saturation and lightness so that one colour becomes another.</summary>
/// <remarks>
///     The shift is the HSL difference between <see cref="From" /> and <see cref="To" />, added to every
///     texel — so <see cref="From" /> lands exactly on <see cref="To" /> and everything else keeps its
///     distance. <c>Colour/Replace Colour Range</c> is the one that touches only colours near
///     <see cref="From" />.
/// </remarks>
[Node("Colour/Replace Colour", Preview = true, Summary = "Shifts the whole image so one colour becomes another.")]
sealed partial class ReplaceColourNode : TextureNode {
    /// <summary>What to shift.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The colour that becomes <see cref="To" />.</summary>
    [Input(Default = [1f, 0f, 0f, 1f])]
    public Float4 From;

    /// <summary>What <see cref="From" /> becomes.</summary>
    [Input(Default = [0f, 0f, 1f, 1f])]
    public Float4 To;

    /// <summary>The shifted colour. Alpha is unchanged.</summary>
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

        emitter.Dispatch(ColourOperators.Replace(emitter, target, source, source, measured: false));
    }
}

/// <summary>The image shifted so that its average colour becomes a chosen one.</summary>
/// <remarks>
///     <c>Colour/Replace Colour</c> with its <c>From</c> measured: the mean is a reduction ladder down to
///     one texel, so this node reads every texel of its input before it writes one, and says so on the
///     plan the way <c>Colour/Auto Levels</c> does.
/// </remarks>
[Node("Colour/Colour Match", Preview = true, Summary = "Shifts the image so its average colour becomes a target.")]
sealed partial class ColourMatchNode : TextureNode {
    /// <summary>What to shift.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The colour the image's average becomes.</summary>
    [Input(Default = [0.5f, 0.5f, 0.5f, 1f])]
    public Float4 Target;

    /// <summary>The shifted colour. Alpha is unchanged.</summary>
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

        var size = emitter.SizeOf(source);
        var level = emitter.LevelOf(source);
        var levels = TextureAdjust.ReductionLevels(size.X, size.Y);
        var scratch = ImmutableArray.CreateBuilder<int>(levels.Length);

        foreach (var rung in levels) {
            scratch.Add(emitter.Scratch(emitter.FormatOf(TextureChannels.Colour), level + rung));
        }

        var ladder = scratch.ToImmutable();

        emitter.Dispatch(
            TextureAdjust.Reduction(source, ladder, size.X, size.Y, TextureColourOperators.MeanReduce)
        );

        emitter.Dispatch(
            ColourOperators.Replace(emitter, target, source, ladder[^1], measured: true) with {
                ReadsOtherExtents = true,
                OtherExtentInputs = [ladder[^1]],
                DependsOnEveryTexel = true
            }
        );
    }
}

/// <summary>Colours near one colour moved towards another, and nothing else.</summary>
[Node(
    "Colour/Replace Colour Range",
    Preview = true,
    Summary = "Moves only the colours near one colour, by how far that colour is from another."
)]
sealed partial class ReplaceColourRangeNode : TextureNode {
    /// <summary>What to recolour.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The colour at the middle of the range.</summary>
    [Input(Default = [1f, 0f, 0f, 1f])]
    public Float4 From;

    /// <summary>What <see cref="From" /> becomes. Its neighbours move by the same amount.</summary>
    [Input(Default = [0f, 0f, 1f, 1f])]
    public Float4 To;

    /// <summary>How far from <see cref="From" />, in RGB, a colour still moves the whole way.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Tolerance = 0.1f;

    /// <summary>How much further the move fades out over.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Softness = 0.1f;

    /// <summary>The recoloured image. Alpha is unchanged.</summary>
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
                Kernel = TextureColourOperators.ReplaceColourRange,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("fromRed", emitter.Number(nameof(From), 0)),
                    new("fromGreen", emitter.Number(nameof(From), 1)),
                    new("fromBlue", emitter.Number(nameof(From), 2)),
                    new("toRed", emitter.Number(nameof(To), 0)),
                    new("toGreen", emitter.Number(nameof(To), 1)),
                    new("toBlue", emitter.Number(nameof(To), 2)),
                    new("tolerance", emitter.Number(nameof(Tolerance))),
                    new("softness", emitter.Number(nameof(Softness)))
                ]
            }
        );
    }
}

/// <summary>The foreground or the background, outright, per texel, in the proportion of an opacity.</summary>
/// <remarks>
///     A dissolve is a composite rather than a blend mode here, because <c>Colour/Blend</c>'s modes mix a
///     colour per texel and this one chooses: every texel is exactly one of the two inputs. The draw is the
///     plan's seed and the texel's position, so it repeats across bakes.
/// </remarks>
[Node("Colour/Dissolve", Preview = true, Adjustment = false, Summary = "Shows the foreground in a random scatter of texels.")]
sealed partial class DissolveNode : TextureNode {
    /// <summary>What shows where the foreground does not.</summary>
    [Input(Name = "Background")]
    public Image Background;

    /// <summary>What shows in the scatter.</summary>
    [Input(Name = "Foreground")]
    public Image Foreground;

    /// <summary>Optional. Scales the opacity per texel; unwired, the opacity holds everywhere.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>The share of texels that show the foreground, on average.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Opacity = 0.5f;

    /// <summary>The composite.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var background = emitter.Read("Background", TextureChannels.Colour);
        var foreground = emitter.Read("Foreground", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (background < 0 || foreground < 0) {
            return;
        }

        var masked = Binding.IsConnected("Mask");
        var mask = masked ? emitter.ReadGrey("Mask") : foreground;

        if (mask < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourOperators.Dissolve,
                Output = target,
                Inputs = [background, foreground, mask],
                Parameters = [new("opacity", emitter.Number(nameof(Opacity))), new("masked", masked ? 1f : 0f)]
            }
        );
    }
}

/// <summary>sRGB-encoded colour decoded to linear.</summary>
[Node("Colour/sRGB to Linear", Preview = true, Summary = "Decodes sRGB-encoded colour to linear light.")]
sealed partial class SrgbToLinearNode : TextureNode {
    /// <summary>The sRGB-encoded image.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The same colours, linear. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(emitter, TextureColourOperators.SrgbConvert, new TextureParameter("toLinear", 1f));
}

/// <summary>Linear colour encoded as sRGB.</summary>
[Node("Colour/Linear to sRGB", Preview = true, Summary = "Encodes linear colour with the sRGB curve.")]
sealed partial class LinearToSrgbNode : TextureNode {
    /// <summary>The linear image.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The same colours, sRGB-encoded. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(emitter, TextureColourOperators.SrgbConvert, new TextureParameter("toLinear", 0f));
}

/// <summary>Colour multiplied by alpha.</summary>
[Node("Colour/Premultiply", Preview = true, Summary = "Multiplies colour by alpha.")]
sealed partial class PremultiplyNode : TextureNode {
    /// <summary>The straight-alpha image.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The premultiplied image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(emitter, TextureColourOperators.Premultiply, new TextureParameter("inverse", 0f));
}

/// <summary>Colour divided by alpha.</summary>
[Node("Colour/Unpremultiply", Preview = true, Summary = "Divides colour by alpha; transparent stays black.")]
sealed partial class UnpremultiplyNode : TextureNode {
    /// <summary>The premultiplied image.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The straight-alpha image. A fully transparent texel is transparent black.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) =>
        ColourOperators.Pointwise(emitter, TextureColourOperators.Premultiply, new TextureParameter("inverse", 1f));
}

/// <summary>A colour graded through a lookup cube stored as a strip of slices.</summary>
/// <remarks>
///     The strip is the layout colour-grading tools exchange: as many slices as the image is tall, each
///     a square, side by side — so a 16-point cube is 256 by 16. Red runs across a slice, green down it,
///     and blue picks the slice.
/// </remarks>
[Node("Colour/Lookup Table", Preview = true, Summary = "Grades colour through a 2D strip of a 3D lookup cube.")]
sealed partial class ColourLookupNode : TextureNode {
    /// <summary>What to grade.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The strip: as many square slices as it is tall, side by side.</summary>
    [Input(Name = "Table")]
    public Image Table;

    /// <summary>The graded colour. Alpha is unchanged.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input", TextureChannels.Colour);
        var table = emitter.Read("Table", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0 || table < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourOperators.ColourLookup,
                Output = target,
                Inputs = [source, table],

                // The strip is read at its own size, which is what says how many points the cube has.
                ReadsOtherExtents = true,
                OtherExtentInputs = [table]
            }
        );
    }
}

/// <summary>Every texel snapped to the nearest of up to eight colours.</summary>
[Node("Colour/Palette", Preview = true, Summary = "Snaps every texel to the nearest of up to eight colours.")]
sealed partial class PaletteNode : TextureNode {
    /// <summary>What to snap.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How many of the eight colours are in the palette.</summary>
    [Input(Minimum = 1f, Maximum = 8f)]
    public Int Count = 2;

    /// <summary>The first colour. A tie goes to the earlier colour.</summary>
    [Input(Name = "Colour 1", Default = [0f, 0f, 0f, 1f])]
    public Float4 Colour1;

    /// <summary>The second colour.</summary>
    [Input(Name = "Colour 2", Default = [1f, 1f, 1f, 1f])]
    public Float4 Colour2;

    /// <summary>The third colour.</summary>
    [Input(Name = "Colour 3", Default = [1f, 0f, 0f, 1f])]
    public Float4 Colour3;

    /// <summary>The fourth colour.</summary>
    [Input(Name = "Colour 4", Default = [0f, 1f, 0f, 1f])]
    public Float4 Colour4;

    /// <summary>The fifth colour.</summary>
    [Input(Name = "Colour 5", Default = [0f, 0f, 1f, 1f])]
    public Float4 Colour5;

    /// <summary>The sixth colour.</summary>
    [Input(Name = "Colour 6", Default = [1f, 1f, 0f, 1f])]
    public Float4 Colour6;

    /// <summary>The seventh colour.</summary>
    [Input(Name = "Colour 7", Default = [0f, 1f, 1f, 1f])]
    public Float4 Colour7;

    /// <summary>The eighth colour.</summary>
    [Input(Name = "Colour 8", Default = [1f, 0f, 1f, 1f])]
    public Float4 Colour8;

    /// <summary>The snapped colour. Alpha is unchanged.</summary>
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

        var parameters = ImmutableArray.CreateBuilder<TextureParameter>(25);

        parameters.Add(new("count", emitter.Integer(nameof(Count))));

        for (var index = 0; index < 8; index++) {
            var port = $"Colour {index + 1}";

            parameters.Add(new($"red{index}", emitter.Number(port, 0)));
            parameters.Add(new($"green{index}", emitter.Number(port, 1)));
            parameters.Add(new($"blue{index}", emitter.Number(port, 2)));
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourOperators.Palette,
                Output = target,
                Inputs = [source],
                Parameters = parameters.ToImmutable()
            }
        );
    }
}

/// <summary>One of four images, chosen by a number.</summary>
/// <remarks>
///     ⚠ <b>All four are bound and the kernel copies one</b>, because the choice is a value and a value
///     change must not recompile the plan. An unwired slot that is chosen draws transparent black, which
///     is visible, rather than quietly standing in another input.
/// </remarks>
[Node("Colour/Select Input", Preview = true, Adjustment = false, Summary = "Passes on one of four inputs, chosen by number.")]
sealed partial class SelectInputNode : TextureNode {
    /// <summary>Slot one.</summary>
    [Input(Name = "Input 1")]
    public Image Input1;

    /// <summary>Slot two. Optional.</summary>
    [Input(Name = "Input 2")]
    public Image Input2;

    /// <summary>Slot three. Optional.</summary>
    [Input(Name = "Input 3")]
    public Image Input3;

    /// <summary>Slot four. Optional.</summary>
    [Input(Name = "Input 4")]
    public Image Input4;

    /// <summary>Which slot is passed on, from one to four.</summary>
    [Input(Minimum = 1f, Maximum = 4f)]
    public Int Index = 1;

    /// <summary>The chosen input.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var first = emitter.Read("Input 1", TextureChannels.Colour);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (first < 0) {
            return;
        }

        var slots = ImmutableArray.CreateBuilder<int>(4);
        var wired = 1;

        slots.Add(first);

        for (var slot = 2; slot <= 4; slot++) {
            var port = $"Input {slot}";

            if (!Binding.IsConnected(port)) {
                slots.Add(first);

                continue;
            }

            var image = emitter.Read(port, TextureChannels.Colour);

            if (image < 0) {
                return;
            }

            slots.Add(image);
            wired |= 1 << (slot - 1);
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourOperators.SelectInput,
                Output = target,
                Inputs = slots.ToImmutable(),
                Parameters = [new("index", emitter.Integer(nameof(Index))), new("wired", wired)]
            }
        );
    }
}

/// <summary>What the pointwise operators share.</summary>
static class ColourOperators {
    /// <summary>One pointwise op over the node's <c>Input</c>, written to its <c>Out</c>.</summary>
    /// <param name="emitter">The node's emitter.</param>
    /// <param name="kernel">The kernel.</param>
    /// <param name="parameters">Its parameters.</param>
    public static void Pointwise(TextureEmitter emitter, string kernel, params TextureParameter[] parameters) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        emitter.Dispatch(new TextureOp { Kernel = kernel, Output = target, Inputs = [source], Parameters = [.. parameters] });
    }

    /// <summary>The replace op, with <c>From</c> named by the node or measured from a 1×1 mean.</summary>
    /// <param name="emitter">The node's emitter.</param>
    /// <param name="target">The image written.</param>
    /// <param name="source">The image shifted.</param>
    /// <param name="reference">The 1×1 mean when measured; any image the node holds otherwise.</param>
    /// <param name="measured">Whether <c>From</c> is the mean rather than a port.</param>
    /// <returns>The op.</returns>
    public static TextureOp Replace(TextureEmitter emitter, int target, int source, int reference, bool measured) {
        var to = measured ? "Target" : "To";

        return new() {
            Kernel = TextureColourOperators.ReplaceColour,
            Output = target,
            Inputs = [source, reference],
            Parameters = [
                new("fromRed", measured ? 1f : emitter.Number("From", 0)),
                new("fromGreen", measured ? 0f : emitter.Number("From", 1)),
                new("fromBlue", measured ? 0f : emitter.Number("From", 2)),
                new("toRed", emitter.Number(to, 0)),
                new("toGreen", emitter.Number(to, 1)),
                new("toBlue", emitter.Number(to, 2)),
                new("measured", measured ? 1f : 0f)
            ]
        };
    }
}
