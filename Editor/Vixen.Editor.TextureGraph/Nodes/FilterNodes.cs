// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

/// <summary>Two images composited into one.</summary>
/// <remarks>
///     ⚠ <b>This is the node doc 48 § Part 4's promotion rule exists for.</b> Both image ports take
///     whatever they are given and the node resolves to the widest of the two, so a grey mask blended
///     over a colour is one <c>Blend</c> with a splat inserted upstream of it — and a grey blended
///     over a grey stays grey, one channel wide, all the way down. Without that rule the library needs
///     a <c>BlendGrayscale</c> beside this, which is the cost § Part 4 names.
/// </remarks>
[Node("Colour/Blend", Preview = true, Summary = "Two images composited under one of sixteen operators.")]
sealed partial class BlendNode : TextureNode {
    /// <summary>Which operator. One of <c>TextureBlendMode</c>'s sixteen names.</summary>
    [Setting]
    public string Mode = "Copy";

    /// <summary>What is underneath.</summary>
    [Input(Name = "Background")]
    public Image Background;

    /// <summary>What is on top.</summary>
    [Input(Name = "Foreground")]
    public Image Foreground;

    /// <summary>How much of the result is the foreground's, before its own alpha.</summary>
    [Input]
    public Scalar Opacity = 1f;

    /// <summary>The composite.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var mode = TextureSettings.Enum(emitter, nameof(Mode), TextureBlendMode.Copy);
        var background = emitter.Read("Background");
        var foreground = emitter.Read("Foreground");
        var target = emitter.Write("Out");

        if (background < 0 || foreground < 0) {
            return;
        }

        emitter.Dispatch(TextureBlend.Mix(target, background, foreground, mode, emitter.Number(nameof(Opacity))));
    }
}

/// <summary>A box blur.</summary>
/// <remarks>
///     <para>
///         <b>Two dispatches and a scratch image, because the kernel does one axis.</b> A radius-r box
///         is <c>2r+1</c> taps per axis rather than <c>(2r+1)²</c> in one pass, and the plan is what
///         separates it — which is also why the intermediate is an <em>image</em> a node asks for
///         rather than something the kernel hides: an image in a plan is written exactly once, and
///         that invariant is what lets <c>TexturePoolSchedule</c> free the scratch the moment the
///         second dispatch has read it.
///     </para>
///     <para>
///         ⚠ <b>The radius is in texels at the base resolution</b> — doc 48 § D8 — so the same graph
///         baked at 4K is the same material rather than a filter a quarter as wide. The node writes
///         the number the author typed and <c>TexturePlan.Resolve</c> scales it; a node that scaled it
///         itself would be a second place the bake's resolution got decided.
///     </para>
/// </remarks>
[Node("Filters/Blur", Preview = true, Summary = "A separable box blur, in texels at the base resolution.")]
sealed partial class BlurNode : TextureNode {
    /// <summary>What to blur.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The half-width, in texels at the graph's base resolution.</summary>
    [Input]
    public Scalar Radius = 8f;

    /// <summary>The blurred image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var source = emitter.Read("Input");
        var format = TextureEmitter.FormatOf(emitter.Resolved);
        var scratch = emitter.Scratch(format);
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        var radius = emitter.Number(nameof(Radius));

        emitter.Dispatch(Pass(scratch, source, radius, vertical: false));
        emitter.Dispatch(Pass(target, scratch, radius, vertical: true));
    }

    /// <summary>One axis of the box.</summary>
    /// <remarks>
    ///     Written here rather than taken from a <c>TextureFilters</c> builder because there is not
    ///     one for <c>Blur</c>: § M2 gave the box kernel no builder and every caller writes the three
    ///     parameters out. This emits all three, which is the property the builders exist for —
    ///     <c>TexturePlanEvaluator.Uniforms</c> refuses an op that leaves a declared member out.
    /// </remarks>
    static TextureOp Pass(int output, int source, float radius, bool vertical) =>
        new() {
            Kernel = "Blur",
            Output = output,
            Inputs = [source],
            Parameters = [
                new("radius", radius, TextureParameterUnit.TexelsAtBase),
                new("stepX", vertical ? 0f : 1f),
                new("stepY", vertical ? 1f : 0f)
            ]
        };
}

/// <summary>An input range remapped through a gamma into an output range.</summary>
/// <remarks>
///     <b>The node that turns any grey field into a mask</b>, and the one whose channels are exactly
///     its input's: a levels curve on a colour is three curves and on a mask is one, and neither is a
///     promotion. ⚠ Its <c>seed</c> is not a port — the evaluator supplies it from
///     <c>TexturePlan.SeedFor</c>, so two Levels nodes in one graph do not dither identically.
/// </remarks>
[Node("Colour/Levels", Preview = true, Summary = "An input range remapped through a gamma into an output range.")]
sealed partial class LevelsNode : TextureNode {
    /// <summary>What to remap.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The input value that becomes the output black.</summary>
    [Input(Name = "Input Black")]
    public Scalar InputBlack = 0f;

    /// <summary>The input value that becomes the output white.</summary>
    [Input(Name = "Input White")]
    public Scalar InputWhite = 1f;

    /// <summary>The midtone exponent. 1 is linear; below 1 lifts, above 1 crushes.</summary>
    [Input]
    public Scalar Gamma = 1f;

    /// <summary>What the input black becomes.</summary>
    [Input(Name = "Output Black")]
    public Scalar OutputBlack = 0f;

    /// <summary>What the input white becomes.</summary>
    [Input(Name = "Output White")]
    public Scalar OutputWhite = 1f;

    /// <summary>How much ordered noise to add, in units of one 8-bit step.</summary>
    /// <remarks>
    ///     ⚠ <b>One by default, where the kernel's own default is zero.</b> A levels curve that lifts
    ///     a narrow range fills an 8-bit output with visible bands and a bake is a file, so the
    ///     banding is permanent; one step costs nothing and is invisible. The kernel defaults to zero
    ///     because a kernel is also what a hand-built plan calls, and this is the authoring default.
    /// </remarks>
    [Input]
    public Scalar Dither = 1f;

    /// <summary>The remapped image.</summary>
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
                Kernel = "Levels",
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("inputBlack", emitter.Number("Input Black")),
                    new("inputWhite", emitter.Number("Input White")),
                    new("gamma", emitter.Number(nameof(Gamma))),
                    new("outputBlack", emitter.Number("Output Black")),
                    new("outputWhite", emitter.Number("Output White")),
                    new("dither", emitter.Number(nameof(Dither)))
                ]
            }
        );
    }
}

/// <summary>Rotate, scale, offset and shear.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Its rotation is in <em>turns</em> rather than radians, and that is the kernel's
///         decision.</b> An exposed 0…1 parameter covers the circle exactly once, which is what a
///         slider wants; a node converting to radians here would put the conversion in the one place
///         nobody reads it back out of.
///     </para>
///     <para>
///         ⚠ <b>Its two settings used to name enums declared in this namespace, which shadowed the
///         assembly's own</b> — <a href="https://github.com/Rikarin/Vixen/issues/727">#727</a>. The
///         shadowing <c>TextureFilter</c> had two members where
///         <see cref="TextureGraph.TextureFilter" /> has three, so <c>Box</c> was not a name this
///         setting would take and the message listing what it <em>would</em> take said so. Deleting
///         the copies fixes the shadowing and re-opens that hole from the other side, because
///         <c>Transform2D.rvn</c> treats every non-zero filter as bilinear: <see cref="Compile" />
///         refuses <c>Box</c> by name rather than silently drawing the bilinear picture under it.
///     </para>
/// </remarks>
[Node("Space/Transform 2D", Preview = true, Summary = "Rotate, scale, offset and shear, with a mip-correct minification.")]
sealed partial class Transform2DNode : TextureNode {
    /// <summary>What is read outside the source: <c>Clamp</c>, <c>Wrap</c> or <c>Mirror</c>.</summary>
    [Setting]
    public string Tiling = "Wrap";

    /// <summary>How a sub-sample reads: <c>Point</c> or <c>Bilinear</c>. ⚠ Not <c>Box</c>.</summary>
    [Setting]
    public string Filter = "Bilinear";

    /// <summary>What to transform.</summary>
    [Input(Name = "Input")]
    public Image Input;

    /// <summary>The rotation, in turns.</summary>
    [Input]
    public Scalar Rotation = 0f;

    /// <summary>How much bigger the image gets along x. Below 1 minifies.</summary>
    [Input(Name = "Scale X")]
    public Scalar ScaleX = 1f;

    /// <summary>The same along y.</summary>
    [Input(Name = "Scale Y")]
    public Scalar ScaleY = 1f;

    /// <summary>How far it moves along x, in fractions of its own size.</summary>
    [Input(Name = "Offset X")]
    public Scalar OffsetX = 0f;

    /// <summary>The same along y.</summary>
    [Input(Name = "Offset Y")]
    public Scalar OffsetY = 0f;

    /// <summary>x sheared by y.</summary>
    [Input(Name = "Shear X")]
    public Scalar ShearX = 0f;

    /// <summary>y sheared by x.</summary>
    [Input(Name = "Shear Y")]
    public Scalar ShearY = 0f;

    /// <summary>The transformed image.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var tiling = TextureSettings.Enum(emitter, nameof(Tiling), TextureTiling.Wrap);
        var filter = TextureSettings.Enum(emitter, nameof(Filter), TextureFilter.Bilinear);
        var source = emitter.Read("Input");
        var target = emitter.Write("Out");

        if (source < 0) {
            return;
        }

        if (filter == TextureFilter.Box) {
            // ⚠ Refused rather than passed through. `Transform2D.rvn` compares `filter` against 0 and
            // takes bilinear for everything else, so a `Box` here is a bilinear transform drawn under
            // the name of a box filter — a plausible picture, and the exact shape of #727's original
            // defect. A box belongs to `Resample`, whose ratio is known because the target's size is
            // the whole of its answer — and which has no node yet, for that same reason (#733).
            emitter.Report(
                "TG0010",
                $"'{nameof(Filter)}' is 'Box', and a transform has no ratio to box over — its minification "
                + "is already mip-correct per texel, which is what it computes instead of asking for a mip. "
                + "Use Point or Bilinear.",
                nameof(Filter)
            );

            return;
        }

        emitter.Dispatch(
            new TextureOp {
                Kernel = TextureColourKernels.Transform2D,
                Output = target,
                Inputs = [source],
                Parameters = [
                    new("rotation", emitter.Number(nameof(Rotation))),
                    new("scaleX", emitter.Number("Scale X")),
                    new("scaleY", emitter.Number("Scale Y")),
                    new("offsetX", emitter.Number("Offset X")),
                    new("offsetY", emitter.Number("Offset Y")),
                    new("shearX", emitter.Number("Shear X")),
                    new("shearY", emitter.Number("Shear Y")),
                    new("tiling", (float)tiling),
                    new("filter", (float)filter)
                ]
            }
        );
    }
}

/// <summary>The distance from every texel to the nearest edge of a mask.</summary>
/// <remarks>
///     <para>
///         <b>The multi-dispatch node, and the reason a node appends ops rather than returning
///         one.</b> A jump flood is <c>log2</c> of the image's longer side, each pass writing its own
///         scratch — because an image in a plan is written exactly once — and then one read. So this
///         node's op count depends on the resolution the graph is being <em>baked</em> at, which is
///         the only node here that does.
///     </para>
///     <para>
///         ⚠ <b>Its mask port is measured rather than composited, so a colour arriving at it is a type
///         error naming the port</b> — doc 48 § Part 4's second half. There is no luminance a colour
///         and a mask agree on, and picking one would be a distance field measured from a boundary
///         the author never chose.
///     </para>
/// </remarks>
[Node("Analysis/Distance", Preview = true, Summary = "A distance field from a mask, by jump flood.")]
sealed partial class DistanceNode : TextureNode {
    /// <summary>Which side is measured: <c>Outside</c>, <c>Inside</c> or <c>Both</c>.</summary>
    [Setting]
    public string Mode = "Outside";

    /// <summary>The mask to measure from. A single channel.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>How far the field reaches, as a fraction of the image's longer side.</summary>
    [Input(Name = "Max Distance")]
    public Scalar MaxDistance = 0.25f;

    /// <summary>What counts as inside the mask.</summary>
    [Input]
    public Scalar Threshold = 0.5f;

    /// <summary>The field.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var mode = TextureSettings.Enum(emitter, nameof(Mode), TextureDistanceMode.Outside);
        var mask = emitter.ReadGrey("Mask");
        var target = emitter.Write("Out", TextureChannels.Grey);

        if (mask < 0) {
            return;
        }

        var width = emitter.Width;
        var height = emitter.Height;
        var passes = TextureAnalysis.FloodDispatches(width, height);
        var scratch = ImmutableArray.CreateBuilder<int>(passes);

        for (var pass = 0; pass < passes; pass++) {
            // ⚠ Rgba16Float and not the node's own channels: a jump flood's record is a *signed
            // offset in texels*, not a picture, and a grey scratch would hold one lane of a
            // coordinate pair.
            scratch.Add(emitter.Scratch(TextureFormat.Rgba16Float));
        }

        try {
            emitter.Dispatch(
                TextureAnalysis.Distance(
                    target,
                    mask,
                    scratch.ToImmutable(),
                    width,
                    height,
                    mode,
                    emitter.Number("Max Distance"),
                    emitter.Number(nameof(Threshold))
                )
            );
        } catch (ArgumentException refusal) {
            // ⚠ Caught rather than pre-checked, so that the refusal and the reason for it stay in one
            // place. The builder's own message names both numbers — a half-float's record is exact on
            // the integers only to 2048 — and repeating that arithmetic here to raise the diagnostic
            // earlier would be two ceilings that have to agree. What this adds is the node: an
            // exception in a background bake names nothing an author can select.
            emitter.Report("TG0011", refusal.Message, "Max Distance");
        }
    }
}
