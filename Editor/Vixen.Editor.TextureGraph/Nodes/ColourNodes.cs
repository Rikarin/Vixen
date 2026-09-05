// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>Hue rotation, saturation and lightness.</summary>
/// <remarks>
///     ⚠ <b>Its hue is in turns and its lightness is signed, and neither is what a colour picker
///     shows.</b> <c>Hsl.rvn</c> takes <c>hue</c> as a fraction of the circle, <c>saturation</c> as a
///     multiplier about 1 and <c>lightness</c> as an offset about 0 — so the identity is
///     <c>(0, 1, 0)</c> and not <c>(0, 0.5, 0.5)</c>. A node that centred them on a half would be the
///     one place in this library where an artist's number and the plan's differ, and the picture
///     under a wrong guess is a perfectly plausible one.
/// </remarks>
[Node("Colour/HSL", Preview = true, Summary = "Hue rotation, saturation and lightness, about the identity.")]
sealed partial class HslNode : TextureNode {
    /// <summary>What to adjust.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How far the hue turns, in turns. 0 leaves it alone.</summary>
    [Input]
    public Scalar Hue = 0f;

    /// <summary>What the saturation is multiplied by. 1 leaves it alone; 0 is grey.</summary>
    [Input]
    public Scalar Saturation = 1f;

    /// <summary>What is added to the lightness. 0 leaves it alone.</summary>
    [Input]
    public Scalar Lightness = 0f;

    /// <summary>The adjusted image.</summary>
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
                Kernel = TextureColourKernels.Hsl,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("hue", emitter.Number(nameof(Hue))),
                    new("saturation", emitter.Number(nameof(Saturation))),
                    new("lightness", emitter.Number(nameof(Lightness)))
                ]
            }
        );
    }
}

/// <summary>Colour to grey, under three weights.</summary>
/// <remarks>
///     <para>
///         <b>The node doc 48 § Part 4's second half sends an author to.</b> A colour arriving at a
///         port that <em>measures</em> — a distance field's mask, a height, a slope — is refused
///         precisely because there is no luminance the two agree on, and this is where the graph says
///         which one it means. That is the whole reason the refusal is worth having: the alternative
///         is a luminance chosen by whoever wrote the kernel.
///     </para>
///     <para>
///         ⚠ <b>Its output is one channel and that is not the same as writing grey into three.</b>
///         The kernel splats, because grey and colour are one port kind; the <em>image</em> is
///         <see cref="TextureChannels.Grey" /> so that a mask costs a quarter of the pool a colour
///         does. A node that wrote colour here would draw the identical picture and quadruple every
///         mask in the graph, which is the class of defect nobody reports.
///     </para>
///     <para>
///         <b>The weights are Rec. 709's and the kernel normalises them</b>, so a triple of ones is a
///         plain mean rather than a treble-bright image.
///     </para>
/// </remarks>
[Node("Colour/Grayscale", Preview = true, Summary = "Colour to a single channel, under three weights.")]
sealed partial class GrayscaleNode : TextureNode {
    /// <summary>What to convert.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Red's share.</summary>
    [Input(Name = "Weight R")]
    public Scalar WeightR = 0.2126f;

    /// <summary>Green's share.</summary>
    [Input(Name = "Weight G")]
    public Scalar WeightG = 0.7152f;

    /// <summary>Blue's share.</summary>
    [Input(Name = "Weight B")]
    public Scalar WeightB = 0.0722f;

    /// <summary>The grey.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (source < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Grayscale,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("weightR", emitter.Number("Weight R")),
                    new("weightG", emitter.Number("Weight G")),
                    new("weightB", emitter.Number("Weight B"))
                ]
            }
        );
    }
}

/// <summary>Per-channel inversion.</summary>
/// <remarks>
///     ⚠ <b>Alpha is off by default and the three colour channels are on, which is the kernel's own
///     default and worth keeping.</b> An inversion that flipped alpha would turn every opaque image
///     transparent, and "invert" almost never means that — but it is exactly what a node defaulting
///     every flag to the same value would do.
/// </remarks>
[Node("Colour/Invert", Preview = true, Summary = "Each channel flipped about a half, independently.")]
sealed partial class InvertNode : TextureNode {
    /// <summary>What to invert.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Whether red is flipped.</summary>
    [Input(Name = "Red")]
    public Bool InvertR = true;

    /// <summary>Whether green is.</summary>
    [Input(Name = "Green")]
    public Bool InvertG = true;

    /// <summary>Whether blue is.</summary>
    [Input(Name = "Blue")]
    public Bool InvertB = true;

    /// <summary>Whether alpha is. ⚠ Off, and see the node's own remarks.</summary>
    [Input(Name = "Alpha")]
    public Bool InvertA = false;

    /// <summary>The inverted image.</summary>
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
                Kernel = TextureColourKernels.Invert,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("invertR", emitter.Flag("Red") ? 1f : 0f),
                    new("invertG", emitter.Flag("Green") ? 1f : 0f),
                    new("invertB", emitter.Flag("Blue") ? 1f : 0f),
                    new("invertA", emitter.Flag("Alpha") ? 1f : 0f)
                ]
            }
        );
    }
}

/// <summary>Each output channel from a channel of one of two inputs.</summary>
/// <remarks>
///     <para>
///         <b>Four settings and two images, and the selectors are names rather than numbers.</b>
///         <see cref="TextureChannelSource" />'s ten members are the kernel's contract, and
///         <c>ChannelShuffle.rvn</c> falls through to the first input's red for anything it does not
///         recognise — so a number typed here would be a plausible picture whenever it drifted, and a
///         name is refused by <see cref="TextureSettings.Enum" /> when it does not match.
///     </para>
///     <para>
///         ⚠ <b>Its output is always four channels, whatever arrives.</b> Packing a roughness, a
///         metalness and an occlusion into one file is the reason the node exists, and the widest
///         thing it produces is not a function of what it read.
///     </para>
///     <para>
///         ⚠ <b>A grey image's green is zero here, and the same grey blended beside a colour has a
///         green equal to its grey.</b> That is not this node's decision and it cannot be made here:
///         when both inputs are single-channel the node resolves to grey and the kernel reads an
///         <c>R16Float</c> view, whose G and B read 0 and whose A reads 1; when anything colour
///         arrives at the same node, <c>TextureGraphCompiler.Read</c> splats the grey into all three
///         lanes first. Both are defensible and they disagree, so a graph packing channels out of
///         greys should say what it means with <c>Zero</c> and <c>One</c> rather than relying on
///         which case it is in.
///     </para>
/// </remarks>
[Node("Colour/Channel Shuffle", Preview = true, Summary = "Each output channel taken from a channel of one of two inputs.")]
sealed partial class ChannelShuffleNode : TextureNode {
    /// <summary>Where red comes from. One of <see cref="TextureChannelSource" />'s ten names.</summary>
    [Setting(Name = "Red From")]
    public string SourceR = "FirstRed";

    /// <summary>Where green comes from.</summary>
    [Setting(Name = "Green From")]
    public string SourceG = "FirstGreen";

    /// <summary>Where blue comes from.</summary>
    [Setting(Name = "Blue From")]
    public string SourceB = "FirstBlue";

    /// <summary>Where alpha comes from.</summary>
    [Setting(Name = "Alpha From")]
    public string SourceA = "FirstAlpha";

    /// <summary>The image the <c>First…</c> selectors read.</summary>
    [Input(Name = "First")]
    public Image First;

    /// <summary>The image the <c>Second…</c> selectors read. Optional: unwired it is the first.</summary>
    /// <remarks>
    ///     The kernel declares two textures and the evaluator binds an op's inputs positionally over
    ///     them, so the slot is always filled — see <see cref="TexturePorts.Optional" />. ⚠ Leaving
    ///     it unwired therefore makes <c>SecondRed</c> mean <c>FirstRed</c> rather than refusing, and
    ///     that is a plausible picture: a shuffle packing two maps that quietly reads one of them
    ///     twice looks like a shuffle.
    /// </remarks>
    [Input(Name = "Second")]
    public Image Second;

    /// <summary>The packed image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var red = TextureSettings.Enum(emitter, "Red From", TextureChannelSource.FirstRed);
        var green = TextureSettings.Enum(emitter, "Green From", TextureChannelSource.FirstGreen);
        var blue = TextureSettings.Enum(emitter, "Blue From", TextureChannelSource.FirstBlue);
        var alpha = TextureSettings.Enum(emitter, "Alpha From", TextureChannelSource.FirstAlpha);
        var first = emitter.Read("First");
        var second = TexturePorts.Optional(this, emitter, "Second", first);
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (first < 0 || second < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.ChannelShuffle,
                Output = target,
                Inputs = [first, second],
                Parameters = [
                    new("sourceR", (float)red),
                    new("sourceG", (float)green),
                    new("sourceB", (float)blue),
                    new("sourceA", (float)alpha)
                ]
            }
        );
    }
}

/// <summary>A spline per channel, through a baked table.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The kernel evaluates no spline, and that is the node's whole design.</b>
///         <c>Core/Vixen.Core/Curves</c>'s <c>CurveEvaluation</c> is the one Hermite implementation in
///         this repository and <c>CurveEditor</c> is the control that edits it; transcribing it into
///         Raven would be the bug its own remark names — "a curve that reads one way in the editor and
///         another in the build". So the curve is baked into a 256-entry table by
///         <see cref="TextureRamp.FromCurves" /> and this kernel interpolates between the entries.
///     </para>
///     <para>
///         <b>With no curve named, the table is the identity</b> — baked through the same evaluator,
///         so the node is a no-op by construction rather than by a branch nobody exercises.
///     </para>
/// </remarks>
[Node("Colour/Curve", Preview = true, Summary = "A spline per channel, through a table baked from the curve editor.")]
sealed partial class CurveNode : TextureNode {
    /// <summary>The curve asset the table is baked from, or empty for the identity.</summary>
    [Setting(Name = "Curve", Summary = "The curve asset a host resolves. Empty is the identity.")]
    public string CurveAsset = "";

    /// <summary>What to shape.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>How much of the curved result to keep. 0 is the input, 1 is the curve.</summary>
    [Input]
    public Scalar Amount = 1f;

    /// <summary>The shaped image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var table = TextureTables.Curve(emitter, "Curve");
        var target = emitter.Write("Out");

        if (source < 0 || table < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Curve,
                Output = target,
                Inputs = [source, table],
                Parameters = [new("amount", emitter.Number(nameof(Amount)))]
            }
        );
    }
}

/// <summary>Grey through a colour ramp.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Its input is grey and grey here is the <em>red channel</em>, not a luminance.</b>
///         <c>GradientMap.rvn</c> says why at length: doc 48 § 4.2 gives <c>Grayscale</c> its own node
///         precisely so that "which weights" is answered once, by an artist — so a colour arriving
///         here is refused rather than averaged, which is <see cref="TextureEmitter.ReadGrey" />'s
///         rule and § Part 4's second half.
///     </para>
///     <para>
///         <b>The ramp is a baked strip, as <c>Source/Gradient</c>'s is</b>, and for the same reason:
///         a kernel that re-mixed stops would be a fourth opinion about a gradient's interpolation
///         space.
///     </para>
/// </remarks>
[Node("Colour/Gradient Map", Preview = true, Summary = "A grey image through a colour ramp.")]
sealed partial class GradientMapNode : TextureNode {
    /// <summary>The gradient asset the ramp is baked from, or empty for black to white.</summary>
    [Setting(Name = "Ramp", Summary = "The gradient asset a host resolves. Empty is black to white.")]
    public string RampAsset = "";

    /// <summary>What to map. A single channel — put a Grayscale in front of a colour.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>Whether the input's alpha survives, rather than the ramp's.</summary>
    [Input(Name = "Keep Alpha")]
    public Bool KeepAlpha = true;

    /// <summary>The mapped image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.ReadGrey("Input");
        var ramp = TextureTables.Ramp(emitter, "Ramp");

        // Colour whatever arrived, because that is the point of the node: a mask goes in and a
        // ramp's colours come out, so writing this at the input's channels would throw the map away.
        var target = emitter.Write("Out", TextureChannels.Colour);

        if (source < 0 || ramp < 0) {
            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.GradientMap,
                Output = target,
                Inputs = [source, ramp],
                Parameters = [new("keepAlpha", emitter.Flag("Keep Alpha") ? 1f : 0f)]
            }
        );
    }
}

/// <summary>The image stretched onto the full range by its own extremes.</summary>
/// <remarks>
///     <para>
///         <b>The one node in the catalogue that is a <em>chain</em> whose length depends on the bake
///         resolution and the size of the image it reads.</b> Finding the extremes of every texel is
///         a reduction — one <c>MinMaxReduce</c> dispatch per rung down to a 1×1 image, because a
///         4K image does not reduce to one texel in one dispatch without a loop no invocation leaves
///         — and <see cref="TextureAdjust.AutoLevels" /> is what emits the ladder so that a call site
///         cannot be two dispatches short at 4K.
///     </para>
///     <para>
///         ⚠ <b>It could not be a node until <a href="https://github.com/Rikarin/Vixen/issues/733">
///         #733</a>, and the reason is one number.</b> Every rung is an image at a level offset —
///         3, 6, 9 down from its source — and a node had no way to ask for one: every image any node
///         allocated was at the plan's base level, and a reduction whose target is the same size as
///         its source has a block of one texel and never converges.
///     </para>
///     <para>
///         ⚠ <b>The ladder is measured from the image this node <em>reads</em>, not from the graph's
///         base.</b> Auto Levels after a half-resolution Resample reduces a half-resolution image, and
///         a ladder counted from the base would start three levels below where its source is — the
///         reduction would then read an 8×8 block of an image that is not there and settle on the
///         extremes of a corner. <see cref="TextureEmitter.LevelOf" /> is what makes it relative.
///     </para>
/// </remarks>
[Node("Colour/Auto Levels", Preview = true, Summary = "Stretches the image onto the full range by its own extremes.")]
sealed partial class AutoLevelsNode : TextureNode {
    /// <summary>What to stretch.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The stretched image.</summary>
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
            // Rgba16Float, because a rung carries a `(min, max)` pair rather than a colour and both
            // lanes are reduced independently. R16Float would keep the minimum and lose the maximum.
            scratch.Add(emitter.Scratch(TextureFormat.Rgba16Float, level + rung));
        }

        emitter.Dispatch(TextureAdjust.AutoLevels(target, source, scratch.ToImmutable(), size.X, size.Y));
    }
}
