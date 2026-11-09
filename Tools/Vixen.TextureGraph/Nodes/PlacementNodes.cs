// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.NodeGraph;

namespace Vixen.TextureGraph.Nodes;

/// <summary>What the two placement nodes share.</summary>
static class Placements {
    /// <summary>The output image, in colour when an instance draws a colour or a UV whatever the pattern is.</summary>
    /// <param name="emitter">Where to write.</param>
    /// <param name="draws">What each instance draws.</param>
    /// <returns>The image.</returns>
    /// <remarks>
    ///     ⚠ A grey pattern resolves the node to grey, and a grey target keeps one lane of three: a random
    ///     colour per instance written there is a random grey, and a UV is its red alone.
    /// </remarks>
    public static int Target(TextureEmitter emitter, TexturePlacementOutput draws) =>
        draws is TexturePlacementOutput.Colour or TexturePlacementOutput.Uv
            ? emitter.Write("Out", TextureChannels.Colour)
            : emitter.Write("Out");
}

/// <summary>A grid of cells with one instance of a pattern in each.</summary>
/// <remarks>
///     <para>
///         <b>Only the pattern has to be wired.</b> The three map ports are
///         <see cref="TexturePorts.Optional" />: the kernel declares four textures and the evaluator
///         binds them positionally, so an unfilled slot takes the pattern and its own
///         <c>…Amount</c> — zero by default — is what says nothing reads it. A mask threshold of
///         zero never culls, because a coverage is never below zero.
///     </para>
///     <para>
///         ⚠ <b>So turning a map's amount up without wiring the map reads the <em>pattern</em> as
///         that map</b>, which is a plausible picture rather than an error. The amounts default to
///         zero for exactly that reason, and this is the one arrangement in the node library where a
///         port's meaning depends on whether another port is connected.
///     </para>
///     <para>
///         ⚠ <b>The kernel searches three cells and this node refuses a plan that would reach
///         further.</b> An instance the search does not reach is drawn cut off along a cell boundary,
///         which reads as a pattern rather than as a defect — so <c>Scale</c> and
///         <c>Position Jitter</c> together are checked before the op is built, and the refusal names
///         both numbers. That is doc 48 § D7's bargain: what FX-Map's recursion buys is an instance
///         count nobody can state, and what refusing it buys is a cost that is knowable before the
///         node runs.
///     </para>
///     <para>
///         ⚠ <b><c>Rotation</c>, <c>Rotation Jitter</c> and <c>Rotation Map Amount</c> are all in
///         radians, and two of the three were in turns until
///         <a href="https://github.com/Rikarin/Vixen/issues/735">#735</a>.</b> This node was the worst
///         case in the assembly: a rotation in radians immediately above a jitter in turns, with
///         nothing on either to say so and a difference of 2π between them. No node converts, so what
///         makes them agree is the kernels agreeing.
///     </para>
///     <para>
///         <b><c>Output</c> draws each instance's own numbers instead of the pattern</b> (Patina #170): a
///         random grey or colour per instance, its number over the count, or its own UV, each shaped by the
///         pattern's coverage. They are what a tint per tile, a mask from a range of instances
///         (<c>Index</c> through a <c>Utility/Histogram Select</c>) or a ramp across every instance is made of.
///     </para>
/// </remarks>
[Node("Placement/Tile Sampler", Preview = true, Adjustment = false, Summary = "A grid of cells with one jittered instance of a pattern in each.")]
sealed partial class TileSamplerNode : TextureNode {
    /// <summary>How overlapping instances combine: <c>Max</c>, <c>Add</c> or <c>Blend</c>.</summary>
    [Setting(AcceptedFrom = typeof(TexturePlacementAccumulation))]
    public string Accumulation = "Max";

    /// <summary>
    ///     What each instance draws: <c>Pattern</c>, or one of its own numbers — <c>Random</c> grey,
    ///     <c>Colour</c>, <c>Index</c> over the count or its <c>Uv</c> — shaped by the pattern's coverage.
    /// </summary>
    [Setting(AcceptedFrom = typeof(TexturePlacementOutput))]
    public string Output = "Pattern";

    /// <summary>The stamp, read as an atlas of <c>Pattern Count</c> equal-width columns.</summary>
    [Input(Name = "Pattern")]
    public Image Pattern;

    /// <summary>Read at each instance's centre; under the threshold it is dropped.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>Read at each instance's centre; shrinks it, under <c>Size Map Amount</c>.</summary>
    [Input(Name = "Size Map")]
    public Image SizeMap;

    /// <summary>Read at each instance's centre; turns it, under <c>Rotation Map Amount</c>.</summary>
    [Input(Name = "Rotation Map")]
    public Image RotationMap;

    /// <summary>How many cells across.</summary>
    [Input(Name = "Grid X", Minimum = 1f)]
    public Int GridX = 8;

    /// <summary>How many cells down.</summary>
    [Input(Name = "Grid Y", Minimum = 1f)]
    public Int GridY = 8;

    /// <summary>An instance's size as a fraction of its cell.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Scale = 1f;

    /// <summary>How much an instance may randomly shrink, 0–1.</summary>
    [Input(Name = "Scale Jitter", Minimum = 0f, Maximum = 1f)]
    public Scalar ScaleJitter = 0f;

    /// <summary>How far it may randomly move inside its cell, 0–1.</summary>
    [Input(Name = "Position Jitter", Minimum = 0f, Maximum = 1f)]
    public Scalar PositionJitter = 0f;

    /// <summary>How far odd rows are shifted along x, as a fraction of a cell: a half is a running bond.</summary>
    [Input(Name = "Row Offset", Minimum = 0f, Maximum = 1f)]
    public Scalar RowOffset = 0f;

    /// <summary>The rotation every instance starts at, in radians.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Rotation = 0f;

    /// <summary>How much it may randomly differ, in radians. At 2π it is free.</summary>
    /// <remarks>
    ///     ⚠ <b>This said "turns until #735" and #735 is closed</b>, which is the stale claim that
    ///     put a default of 1 on <c>Patterns/Tile Random</c>'s <c>Turn</c> port under a summary
    ///     promising a whole turn — about a sixth of one, silently, with nothing in either test file
    ///     able to see it: the compound roll call only asks that the picture is not flat.
    /// </remarks>
    [Input(Name = "Rotation Jitter", SoftMinimum = 0f, SoftMaximum = MathF.Tau)]
    public Scalar RotationJitter = 0f;

    /// <summary>How much an instance may randomly darken, 0–1.</summary>
    [Input(Name = "Colour Jitter", Minimum = 0f, Maximum = 1f)]
    public Scalar ColourJitter = 0f;

    /// <summary>How many equal-width columns the pattern holds.</summary>
    [Input(Name = "Pattern Count", Minimum = 1f)]
    public Int PatternCount = 1;

    /// <summary>Whether the pattern's alpha carries its coverage rather than its luminance.</summary>
    /// <remarks>
    ///     ⚠ <b>It also decides how a minified instance is filtered</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/950">#950</a>. With an alpha the colour
    ///     and the coverage are two numbers, so a partly covered texel keeps the pattern's colour and
    ///     says so in its alpha; without one the luminance <em>is</em> the coverage, so a partly
    ///     covered texel is a darker one. Ticking this on a pattern whose alpha is opaque everywhere
    ///     hardens every edge the bake minifies.
    /// </remarks>
    [Input(Name = "Alpha Coverage")]
    public Bool AlphaCoverage = false;

    /// <summary>The mask value below which an instance is dropped. ⚠ Zero never culls.</summary>
    [Input(Name = "Mask Threshold", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar MaskThreshold = 0f;

    /// <summary>How much of the size map reaches an instance's scale, 0–1. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Size Map Amount", Minimum = 0f, Maximum = 1f)]
    public Scalar SizeMapAmount = 0f;

    /// <summary>What a full rotation map is worth, in radians. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Rotation Map Amount", SoftMinimum = 0f, SoftMaximum = MathF.Tau)]
    public Scalar RotationMapAmount = 0f;

    /// <summary>How much of each instance reaches the result.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Opacity = 1f;

    /// <summary>The grid.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var accumulation = TextureSettings.Enum(emitter, nameof(Accumulation), TexturePlacementAccumulation.Max);
        var draws = TextureSettings.Enum(emitter, nameof(Output), TexturePlacementOutput.Pattern);
        var pattern = emitter.Read("Pattern");
        var mask = TexturePorts.Optional(this, emitter, "Mask", pattern);
        var sizeMap = TexturePorts.Optional(this, emitter, "Size Map", pattern);
        var rotationMap = TexturePorts.Optional(this, emitter, "Rotation Map", pattern);
        var target = Placements.Target(emitter, draws);

        if (pattern < 0 || mask < 0 || sizeMap < 0 || rotationMap < 0) {
            return;
        }

        try {
            emitter.Dispatch(
                TexturePlacement.TileSampler(
                    target,
                    pattern,
                    mask,
                    sizeMap,
                    rotationMap,
                    emitter.Integer("Grid X"),
                    emitter.Integer("Grid Y"),
                    emitter.Number(nameof(Scale)),
                    emitter.Number("Scale Jitter"),
                    emitter.Number("Position Jitter"),
                    emitter.Number(nameof(Rotation)),
                    emitter.Number("Rotation Jitter"),
                    emitter.Number("Colour Jitter"),
                    emitter.Integer("Pattern Count"),
                    emitter.Flag("Alpha Coverage"),
                    emitter.Number("Mask Threshold"),
                    emitter.Number("Size Map Amount"),
                    emitter.Number("Rotation Map Amount"),
                    accumulation,
                    emitter.Number(nameof(Opacity)),
                    draws,
                    emitter.Number("Row Offset")
                )
            );
        } catch (ArgumentException refusal) {
            // ⚠ Caught rather than pre-checked, on `DistanceNode`'s argument: the builder's message
            // names the numbers and the cell count they work out to, and repeating that arithmetic
            // here would be two ceilings that have to agree. The port named is the one an artist
            // turns to cause it.
            emitter.Report(TextureDiagnostics.BuilderRefusedTheNumbers, refusal.Message, nameof(Scale));
        }
    }
}

/// <summary>A bounded free scatter of one pattern.</summary>
/// <remarks>
///     <para>
///         <b>Only the pattern has to be wired</b>, for <c>Tile Sampler</c>'s reason: the four map
///         ports are <see cref="TexturePorts.Optional" /> and an unfilled slot takes the pattern,
///         whose own <c>…Amount</c> at zero is what says nothing reads it.
///     </para>
///     <para>
///         ⚠ <b>At most 4 096 instances, in batches of 256, refused rather than truncated</b> (Patina
///         #170). Every texel examines every instance of a batch, so a batch is one dispatch of the
///         kernel's own bounded loop and a splatter of a thousand is four of them, each starting from
///         what the one before it left. Past 4 096 the node refuses rather than drawing fewer: a picture
///         that is right in every respect except how many things are in it, which is precisely the
///         parameter the artist was turning.
///     </para>
///     <para>
///         <b>The placement map moves instances rather than placing them</b> — red and green read as
///         a signed offset, under <c>Placement Amount</c> — so at zero the scatter is the seed's
///         alone, which is what makes the node reproducible.
///     </para>
///     <para>
///         <b><c>Output</c> draws each instance's own numbers instead of the pattern</b> (Patina #170): a
///         random grey or colour per instance, its number over the count, or its own UV, each shaped by the
///         pattern's coverage. They are what a tint per tile, a mask from a range of instances
///         (<c>Index</c> through a <c>Utility/Histogram Select</c>) or a ramp across every instance is made of.
///     </para>
/// </remarks>
[Node("Placement/Splatter", Preview = true, Adjustment = false, Summary = "A bounded free scatter of one pattern, seeded from the plan.")]
sealed partial class SplatterNode : TextureNode {
    /// <summary>How overlapping instances combine: <c>Max</c>, <c>Add</c> or <c>Blend</c>.</summary>
    [Setting(AcceptedFrom = typeof(TexturePlacementAccumulation))]
    public string Accumulation = "Max";

    /// <summary>
    ///     What each instance draws: <c>Pattern</c>, or one of its own numbers — <c>Random</c> grey,
    ///     <c>Colour</c>, <c>Index</c> over the count or its <c>Uv</c> — shaped by the pattern's coverage.
    /// </summary>
    [Setting(AcceptedFrom = typeof(TexturePlacementOutput))]
    public string Output = "Pattern";

    /// <summary>Where the instances go: <c>Scatter</c>, anywhere, or <c>Ring</c>, evenly round a circle.</summary>
    [Setting(AcceptedFrom = typeof(TextureSplatterDistribution))]
    public string Distribution = "Scatter";

    /// <summary>The stamp, read as an atlas of <c>Pattern Count</c> equal-width columns.</summary>
    [Input(Name = "Pattern")]
    public Image Pattern;

    /// <summary>Read at each instance's centre; under the threshold it is dropped.</summary>
    [Input(Name = "Mask")]
    public Image Mask;

    /// <summary>Read at each instance's centre; shrinks it, under <c>Size Map Amount</c>.</summary>
    [Input(Name = "Size Map")]
    public Image SizeMap;

    /// <summary>Read at each instance's centre; turns it, under <c>Rotation Map Amount</c>.</summary>
    [Input(Name = "Rotation Map")]
    public Image RotationMap;

    /// <summary>Where the instances go: red and green read as a signed offset.</summary>
    [Input(Name = "Placement")]
    public Image Placement;

    /// <summary>How many instances. ⚠ At most 4 096, and past that the node refuses.</summary>
    [Input(Minimum = 1f, Maximum = TexturePlacement.MaxBatchedInstances)]
    public Int Count = 16;

    /// <summary>An instance's size as a fraction of the image.</summary>
    [Input(Minimum = 0f, SoftMaximum = 1f)]
    public Scalar Scale = 0.25f;

    /// <summary>How much an instance may randomly shrink, 0–1.</summary>
    [Input(Name = "Scale Jitter", Minimum = 0f, Maximum = 1f)]
    public Scalar ScaleJitter = 0f;

    /// <summary>The rotation every instance starts at, in radians.</summary>
    [Input(SoftMinimum = -MathF.PI, SoftMaximum = MathF.PI)]
    public Scalar Rotation = 0f;

    /// <summary>How much it may randomly differ, in radians. At 2π it is free. ⚠ Turns until #735.</summary>
    [Input(Name = "Rotation Jitter", SoftMinimum = 0f, SoftMaximum = MathF.Tau)]
    public Scalar RotationJitter = 0f;

    /// <summary>How much an instance may randomly darken, 0–1.</summary>
    [Input(Name = "Colour Jitter", Minimum = 0f, Maximum = 1f)]
    public Scalar ColourJitter = 0f;

    /// <summary>How many equal-width columns the pattern holds.</summary>
    [Input(Name = "Pattern Count", Minimum = 1f)]
    public Int PatternCount = 1;

    /// <summary>Whether the pattern's alpha carries its coverage rather than its luminance.</summary>
    /// <remarks>
    ///     ⚠ <b>It also decides how a minified instance is filtered</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/950">#950</a>. With an alpha the colour
    ///     and the coverage are two numbers, so a partly covered texel keeps the pattern's colour and
    ///     says so in its alpha; without one the luminance <em>is</em> the coverage, so a partly
    ///     covered texel is a darker one. Ticking this on a pattern whose alpha is opaque everywhere
    ///     hardens every edge the bake minifies.
    /// </remarks>
    [Input(Name = "Alpha Coverage")]
    public Bool AlphaCoverage = false;

    /// <summary>The mask value below which an instance is dropped. ⚠ Zero never culls.</summary>
    [Input(Name = "Mask Threshold", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar MaskThreshold = 0f;

    /// <summary>How much of the size map reaches an instance's scale, 0–1. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Size Map Amount", Minimum = 0f, Maximum = 1f)]
    public Scalar SizeMapAmount = 0f;

    /// <summary>What a full rotation map is worth, in radians. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Rotation Map Amount", SoftMinimum = 0f, SoftMaximum = MathF.Tau)]
    public Scalar RotationMapAmount = 0f;

    /// <summary>How far the placement map may move an instance, in fractions of the image.</summary>
    [Input(Name = "Placement Amount", SoftMinimum = 0f, SoftMaximum = 1f)]
    public Scalar PlacementAmount = 0f;

    /// <summary>Under <c>Ring</c>, the ring's radius as a fraction of the image, about its centre.</summary>
    [Input(Name = "Ring Radius", SoftMinimum = 0f, SoftMaximum = 0.5f)]
    public Scalar RingRadius = 0.3f;

    /// <summary>Under <c>Ring</c>, whether each instance turns with its angle round the ring, so it faces along it.</summary>
    [Input(Name = "Follow Ring")]
    public Bool FollowRing = false;

    /// <summary>How much of each instance reaches the result.</summary>
    [Input(Minimum = 0f, Maximum = 1f)]
    public Scalar Opacity = 1f;

    /// <summary>The scatter.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var accumulation = TextureSettings.Enum(emitter, nameof(Accumulation), TexturePlacementAccumulation.Max);
        var draws = TextureSettings.Enum(emitter, nameof(Output), TexturePlacementOutput.Pattern);
        var distribution = TextureSettings.Enum(emitter, nameof(Distribution), TextureSplatterDistribution.Scatter);
        var pattern = emitter.Read("Pattern");
        var mask = TexturePorts.Optional(this, emitter, "Mask", pattern);
        var sizeMap = TexturePorts.Optional(this, emitter, "Size Map", pattern);
        var rotationMap = TexturePorts.Optional(this, emitter, "Rotation Map", pattern);
        var placement = TexturePorts.Optional(this, emitter, "Placement", pattern);
        var target = Placements.Target(emitter, draws);

        if (pattern < 0 || mask < 0 || sizeMap < 0 || rotationMap < 0 || placement < 0) {
            return;
        }

        var count = emitter.Integer(nameof(Count));
        var scratch = ImmutableArray.CreateBuilder<int>();

        // ⚠ Four lanes whatever the node's own channels: a batch hands the next its coverage as well as its
        // colour, and a grey image would drop it.
        for (var batch = 1; batch < TexturePlacement.SplatterBatches(count); batch++) {
            scratch.Add(emitter.Scratch(emitter.FormatOf(TextureChannels.Colour)));
        }

        try {
            var single = TexturePlacement.Splatter(
                    target,
                    pattern,
                    mask,
                    sizeMap,
                    rotationMap,
                    placement,
                    Math.Clamp(count, 1, TexturePlacement.MaxInstances),
                    emitter.Number(nameof(Scale)),
                    emitter.Number("Scale Jitter"),
                    emitter.Number(nameof(Rotation)),
                    emitter.Number("Rotation Jitter"),
                    emitter.Number("Colour Jitter"),
                    emitter.Integer("Pattern Count"),
                    emitter.Flag("Alpha Coverage"),
                    emitter.Number("Mask Threshold"),
                    emitter.Number("Size Map Amount"),
                    emitter.Number("Rotation Map Amount"),
                    emitter.Number("Placement Amount"),
                    accumulation,
                    emitter.Number(nameof(Opacity)),
                    draws,
                    distribution,
                    emitter.Number("Ring Radius"),
                    emitter.Flag("Follow Ring")
                );

            emitter.Dispatch(TexturePlacement.SplatterChain(target, scratch.ToImmutable(), single, count));
        } catch (ArgumentException refusal) {
            emitter.Report(TextureDiagnostics.BuilderRefusedTheNumbers, refusal.Message, nameof(Count));
        }
    }
}
