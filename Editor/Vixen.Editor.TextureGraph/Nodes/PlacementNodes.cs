// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;

namespace Vixen.Editor.TextureGraph.Nodes;

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
///         ⚠ <b><c>Rotation</c> is in radians and <c>Rotation Jitter</c> is in turns.</b> Both are
///         the kernel's own units; a node that reconciled them would be the only place in the
///         assembly where a number changes meaning between the graph and the plan —
///         <a href="https://github.com/Rikarin/Vixen/issues/735">#735</a>.
///     </para>
/// </remarks>
[Node("Placement/Tile Sampler", Preview = true, Summary = "A grid of cells with one jittered instance of a pattern in each.")]
sealed partial class TileSamplerNode : TextureNode {
    /// <summary>How overlapping instances combine: <c>Max</c>, <c>Add</c> or <c>Blend</c>.</summary>
    [Setting]
    public string Accumulation = "Max";

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
    [Input(Name = "Grid X")]
    public Int GridX = 8;

    /// <summary>How many cells down.</summary>
    [Input(Name = "Grid Y")]
    public Int GridY = 8;

    /// <summary>An instance's size as a fraction of its cell.</summary>
    [Input]
    public Scalar Scale = 1f;

    /// <summary>How much an instance may randomly shrink, 0–1.</summary>
    [Input(Name = "Scale Jitter")]
    public Scalar ScaleJitter = 0f;

    /// <summary>How far it may randomly move inside its cell, 0–1.</summary>
    [Input(Name = "Position Jitter")]
    public Scalar PositionJitter = 0f;

    /// <summary>The rotation every instance starts at, in radians.</summary>
    [Input]
    public Scalar Rotation = 0f;

    /// <summary>How much it may randomly differ, in turns.</summary>
    [Input(Name = "Rotation Jitter")]
    public Scalar RotationJitter = 0f;

    /// <summary>How much an instance may randomly darken, 0–1.</summary>
    [Input(Name = "Colour Jitter")]
    public Scalar ColourJitter = 0f;

    /// <summary>How many equal-width columns the pattern holds.</summary>
    [Input(Name = "Pattern Count")]
    public Int PatternCount = 1;

    /// <summary>Whether the pattern's alpha carries its coverage rather than its luminance.</summary>
    [Input(Name = "Alpha Coverage")]
    public Bool AlphaCoverage = false;

    /// <summary>The mask value below which an instance is dropped. ⚠ Zero never culls.</summary>
    [Input(Name = "Mask Threshold")]
    public Scalar MaskThreshold = 0f;

    /// <summary>How much of the size map reaches an instance's scale, 0–1. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Size Map Amount")]
    public Scalar SizeMapAmount = 0f;

    /// <summary>How much of the rotation map is added, in turns. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Rotation Map Amount")]
    public Scalar RotationMapAmount = 0f;

    /// <summary>How much of each instance reaches the result.</summary>
    [Input]
    public Scalar Opacity = 1f;

    /// <summary>The grid.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var accumulation = TextureSettings.Enum(emitter, nameof(Accumulation), TexturePlacementAccumulation.Max);
        var pattern = emitter.Read("Pattern");
        var mask = TexturePorts.Optional(this, emitter, "Mask", pattern);
        var sizeMap = TexturePorts.Optional(this, emitter, "Size Map", pattern);
        var rotationMap = TexturePorts.Optional(this, emitter, "Rotation Map", pattern);
        var target = emitter.Write("Out");

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
                    emitter.Number(nameof(Opacity))
                )
            );
        } catch (ArgumentException refusal) {
            // ⚠ Caught rather than pre-checked, on `DistanceNode`'s argument: the builder's message
            // names the numbers and the cell count they work out to, and repeating that arithmetic
            // here would be two ceilings that have to agree. The port named is the one an artist
            // turns to cause it.
            emitter.Report("TG0011", refusal.Message, nameof(Scale));
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
///         ⚠ <b>At most 256 instances, refused rather than truncated.</b> A splatter asked for more
///         would draw the first 256 with no warning: a picture that is right in every respect except
///         how many things are in it, which is precisely the parameter the artist was turning. A
///         graph that wants more places several of these and blends them.
///     </para>
///     <para>
///         <b>The placement map moves instances rather than placing them</b> — red and green read as
///         a signed offset, under <c>Placement Amount</c> — so at zero the scatter is the seed's
///         alone, which is what makes the node reproducible.
///     </para>
/// </remarks>
[Node("Placement/Splatter", Preview = true, Summary = "A bounded free scatter of one pattern, seeded from the plan.")]
sealed partial class SplatterNode : TextureNode {
    /// <summary>How overlapping instances combine: <c>Max</c>, <c>Add</c> or <c>Blend</c>.</summary>
    [Setting]
    public string Accumulation = "Max";

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

    /// <summary>How many instances. ⚠ At most 256, and past that the node refuses.</summary>
    [Input]
    public Int Count = 16;

    /// <summary>An instance's size as a fraction of the image.</summary>
    [Input]
    public Scalar Scale = 0.25f;

    /// <summary>How much an instance may randomly shrink, 0–1.</summary>
    [Input(Name = "Scale Jitter")]
    public Scalar ScaleJitter = 0f;

    /// <summary>The rotation every instance starts at, in radians.</summary>
    [Input]
    public Scalar Rotation = 0f;

    /// <summary>How much it may randomly differ, in turns.</summary>
    [Input(Name = "Rotation Jitter")]
    public Scalar RotationJitter = 0f;

    /// <summary>How much an instance may randomly darken, 0–1.</summary>
    [Input(Name = "Colour Jitter")]
    public Scalar ColourJitter = 0f;

    /// <summary>How many equal-width columns the pattern holds.</summary>
    [Input(Name = "Pattern Count")]
    public Int PatternCount = 1;

    /// <summary>Whether the pattern's alpha carries its coverage rather than its luminance.</summary>
    [Input(Name = "Alpha Coverage")]
    public Bool AlphaCoverage = false;

    /// <summary>The mask value below which an instance is dropped. ⚠ Zero never culls.</summary>
    [Input(Name = "Mask Threshold")]
    public Scalar MaskThreshold = 0f;

    /// <summary>How much of the size map reaches an instance's scale, 0–1. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Size Map Amount")]
    public Scalar SizeMapAmount = 0f;

    /// <summary>How much of the rotation map is added, in turns. ⚠ Zero ignores the map.</summary>
    [Input(Name = "Rotation Map Amount")]
    public Scalar RotationMapAmount = 0f;

    /// <summary>How far the placement map may move an instance, in fractions of the image.</summary>
    [Input(Name = "Placement Amount")]
    public Scalar PlacementAmount = 0f;

    /// <summary>How much of each instance reaches the result.</summary>
    [Input]
    public Scalar Opacity = 1f;

    /// <summary>The scatter.</summary>
    [Output(Name = "Out")]
    public Image Out;

    /// <inheritdoc />
    protected internal override void Compile(TextureEmitter emitter) {
        ArgumentNullException.ThrowIfNull(emitter);

        var accumulation = TextureSettings.Enum(emitter, nameof(Accumulation), TexturePlacementAccumulation.Max);
        var pattern = emitter.Read("Pattern");
        var mask = TexturePorts.Optional(this, emitter, "Mask", pattern);
        var sizeMap = TexturePorts.Optional(this, emitter, "Size Map", pattern);
        var rotationMap = TexturePorts.Optional(this, emitter, "Rotation Map", pattern);
        var placement = TexturePorts.Optional(this, emitter, "Placement", pattern);
        var target = emitter.Write("Out");

        if (pattern < 0 || mask < 0 || sizeMap < 0 || rotationMap < 0 || placement < 0) {
            return;
        }

        try {
            emitter.Dispatch(
                TexturePlacement.Splatter(
                    target,
                    pattern,
                    mask,
                    sizeMap,
                    rotationMap,
                    placement,
                    emitter.Integer(nameof(Count)),
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
                    emitter.Number(nameof(Opacity))
                )
            );
        } catch (ArgumentException refusal) {
            emitter.Report("TG0011", refusal.Message, nameof(Count));
        }
    }
}
