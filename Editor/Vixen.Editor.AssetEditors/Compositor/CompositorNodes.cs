// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.NodeGraph;
using Vixen.Graphics;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;

namespace Vixen.Editor.AssetEditors.Compositor;

/// <summary>The whole frame: the one node a compositor graph is compiled from.</summary>
/// <remarks>
///     ⚠ <b>Exactly one, and everything reachable from it is the frame.</b> A graph with two would
///     have two answers to "what does this document render"; a graph with none renders nothing, and
///     saying so is more useful than compiling an empty frame that draws a black screen. Nodes not
///     reachable from it are reported rather than dropped silently — an author who has unhooked a
///     pass while debugging should be told it is unhooked, not have it quietly deleted on save.
/// </remarks>
[Node("Frame/Frame", Summary = "The root. Everything on its chain is the frame, in order.")]
public sealed partial class FrameNode : CompositorNode {
    /// <summary>Where the frame's first node connects.</summary>
    [Output(Name = "Body")]
    public Flow Body;

    /// <inheritdoc />
    protected internal override ISceneRendererAsset? Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        Sequence("Frame", children);

    /// <summary>Wraps a chain in a sequence, or hands back the single node it holds.</summary>
    /// <param name="name">What the sequence would be called.</param>
    /// <param name="children">The chain.</param>
    /// <returns>The node, or <see langword="null" /> for an empty chain.</returns>
    /// <remarks>
    ///     A sequence of one is a sequence with nothing to sequence, and a document full of them is a
    ///     document that reads as though somebody generated it. The runtime treats the two
    ///     identically, so this is entirely about the file a person opens.
    /// </remarks>
    internal static ISceneRendererAsset? Sequence(string name, IReadOnlyList<ISceneRendererAsset> children) =>
        children.Count switch {
            0 => null,
            1 => children[0],
            _ => new SequenceAsset { Name = name, Children = [.. children] }
        };
}

/// <summary>Several nodes, run in order, under one name.</summary>
[Node("Frame/Sequence", Summary = "A named group of nodes, run in the order of its inner chain.")]
public sealed partial class SequenceNode : CompositorNode {
    /// <summary>Where the previous node connects.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Where the next node connects.</summary>
    [Output(Name = "Out")]
    public Flow Out;

    /// <summary>Where the first of its children connects.</summary>
    [Output(Name = "Body")]
    public Flow Body;

    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text, "What a debug group and a frame capture call it."),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, "Off removes the whole group from the frame.", Fallback: 1f)
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) => new SequenceAsset {
        Name = Named("Sequence"),
        Enabled = Flag("Enabled", true),
        Children = [.. children]
    };
}

/// <summary>A render pass, and what draws into it.</summary>
[Node("Frame/Render Pass", Summary = "Attachments, what it samples, and the nodes that draw into it.")]
public sealed partial class RenderPassNode : CompositorNode {
    /// <summary>Where the previous node connects.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Where the next node connects.</summary>
    [Output(Name = "Out")]
    public Flow Out;

    /// <summary>Where the first node that draws into it connects.</summary>
    [Output(Name = "Body")]
    public Flow Body;

    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new(
            "ColourTargets",
            "Colour targets",
            CompositorFieldKind.Names,
            "In the order the shader writes them. Names, not textures — the host binds the name."
        ),
        new("DepthTarget", "Depth target", CompositorFieldKind.Text),
        new(
            "Reads",
            "Reads",
            CompositorFieldKind.Names,
            "Not bookkeeping: this is the edge that orders the producer first and puts a barrier between them."
        ),
        new("BufferReads", "Buffer reads", CompositorFieldKind.Names),
        new("SampleCount", "Samples", CompositorFieldKind.Number, Fallback: 1f)
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new RenderPassAsset {
            Name = Named("Pass"),
            Enabled = Flag("Enabled", true),
            ColourTargets = Names("ColourTargets"),
            DepthTarget = Text("DepthTarget") is { Length: > 0 } depth ? depth : null,
            Reads = Names("Reads"),
            BufferReads = Names("BufferReads"),
            SampleCount = Math.Max(Whole("SampleCount", 1), 1),
            Children = [.. children]
        };
}

/// <summary>The base of every node that draws and contains nothing.</summary>
/// <remarks>
///     One flow in, one flow out. Declaring the pair once is what makes adding a node kind a class
///     with an <c>Emit</c> in it rather than a class with two fields nobody reads and an <c>Emit</c>.
/// </remarks>
public abstract class CompositorLeafNode : CompositorNode {
    /// <summary>Where the previous node connects.</summary>
    [Input(Name = "In")]
    public Flow In;

    /// <summary>Where the next node connects.</summary>
    [Output(Name = "Out")]
    public Flow Out;
}

/// <summary>One stage drawn from one view.</summary>
[Node("Draw/Single Stage", Summary = "Draws one render stage from one view.")]
public sealed partial class SingleStageNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("View", "View", CompositorFieldKind.Text, "The name of the view to draw from."),
        new("Stage", "Stage", CompositorFieldKind.Text, "The name of the stage to draw.")
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new SingleStageAsset {
            Name = Named("Stage"),
            Enabled = Flag("Enabled", true),
            View = Text("View"),
            Stage = Text("Stage")
        };
}

/// <summary>One effect over the whole screen — a post-process pass.</summary>
[Node("Draw/Full Screen", Summary = "Runs one shader over every pixel of its targets.")]
public sealed partial class FullScreenNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("Shader", "Shader", CompositorFieldKind.Text),
        new("ColourTargets", "Colour targets", CompositorFieldKind.Names),
        new("Reads", "Reads", CompositorFieldKind.Names),
        new("BufferReads", "Buffer reads", CompositorFieldKind.Names),
        new(
            "Blend",
            "Blend",
            CompositorFieldKind.Choice,
            Options: [
                nameof(BlendPreset.Opaque),
                nameof(BlendPreset.AlphaBlend),
                nameof(BlendPreset.PremultipliedAlpha),
                nameof(BlendPreset.Additive)
            ]
        ),
        new(
            "Load",
            "Load",
            CompositorFieldKind.Choice,
            "DontCare is right for a pass that writes every pixel; clearing first is a whole extra write.",
            [nameof(LoadAction.DontCare), nameof(LoadAction.Clear), nameof(LoadAction.Load)]
        )
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new FullScreenAsset {
            Name = Named("Full Screen"),
            Enabled = Flag("Enabled", true),
            Shader = Text("Shader"),
            ColourTargets = Names("ColourTargets"),
            Reads = Names("Reads"),
            BufferReads = Names("BufferReads"),
            Blend = Choice("Blend", BlendPreset.Opaque),
            Load = Choice("Load", LoadAction.DontCare)
        };
}

/// <summary>A compute dispatch, and the resources it declares.</summary>
[Node("Draw/Compute", Summary = "A dispatch, with the reads and writes that order it.")]
public sealed partial class ComputeNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("Shader", "Shader", CompositorFieldKind.Text),
        new("Reads", "Reads", CompositorFieldKind.Names),
        new("Writes", "Writes", CompositorFieldKind.Names),
        new("BufferReads", "Buffer reads", CompositorFieldKind.Names),
        new("BufferWrites", "Buffer writes", CompositorFieldKind.Names),
        new("GroupsX", "Groups X", CompositorFieldKind.Number, Fallback: 1f),
        new("GroupsY", "Groups Y", CompositorFieldKind.Number, Fallback: 1f),
        new("GroupsZ", "Groups Z", CompositorFieldKind.Number, Fallback: 1f)
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new ComputeAsset {
            Name = Named("Compute"),
            Enabled = Flag("Enabled", true),
            Shader = Text("Shader"),
            Reads = Names("Reads"),
            Writes = Names("Writes"),
            BufferReads = Names("BufferReads"),
            BufferWrites = Names("BufferWrites"),
            GroupsX = Math.Max(Whole("GroupsX", 1), 1),
            GroupsY = Math.Max(Whole("GroupsY", 1), 1),
            GroupsZ = Math.Max(Whole("GroupsZ", 1), 1)
        };
}

/// <summary>A directional light's cascaded shadow map.</summary>
[Node("Shadows/Shadow Map", Summary = "Cascades for the directional light, into a depth atlas.")]
public sealed partial class ShadowMapNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("Stage", "Stage", CompositorFieldKind.Text, "The stage that draws depth-only casters."),
        new("Atlas", "Atlas", CompositorFieldKind.Text),
        new("CascadeCount", "Cascades", CompositorFieldKind.Number, Fallback: 4f),
        new("Resolution", "Resolution", CompositorFieldKind.Number, Fallback: 1024f),
        new(
            "ShadowDistance",
            "Shadow distance",
            CompositorFieldKind.Number,
            "How far shadows are drawn — not the camera's far plane.",
            Fallback: 150f
        ),
        new("SplitLambda", "Split lambda", CompositorFieldKind.Number, Fallback: 0.75f),
        new("Extrusion", "Extrusion", CompositorFieldKind.Number, Fallback: 50f)
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new ShadowMapAsset {
            Name = Named("Shadows"),
            Enabled = Flag("Enabled", true),
            Stage = Text("Stage"),
            Atlas = Text("Atlas"),
            CascadeCount = Math.Max(Whole("CascadeCount", 4), 1),
            Resolution = Math.Max(Whole("Resolution", 1024), 1),
            ShadowDistance = Number("ShadowDistance", 150f),
            SplitLambda = Number("SplitLambda", 0.75f),
            Extrusion = Number("Extrusion", 50f)
        };
}

/// <summary>Spot and point light shadows in one atlas.</summary>
[Node("Shadows/Punctual Shadows", Summary = "Spot and point shadows, tiled into one atlas.")]
public sealed partial class PunctualShadowNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("Stage", "Stage", CompositorFieldKind.Text),
        new("Atlas", "Atlas", CompositorFieldKind.Text),
        new("Resolution", "Tile resolution", CompositorFieldKind.Number, Fallback: 512f),
        new("TilesPerSide", "Tiles per side", CompositorFieldKind.Number, Fallback: 4f)
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new PunctualShadowAsset {
            Name = Named("Punctual Shadows"),
            Enabled = Flag("Enabled", true),
            Stage = Text("Stage"),
            Atlas = Text("Atlas"),
            Resolution = Math.Max(Whole("Resolution", 512), 1),
            TilesPerSide = Math.Max(Whole("TilesPerSide", 4), 1)
        };
}

/// <summary>The depth pyramid next frame's culling tests against.</summary>
[Node("Culling/Hi-Z", Summary = "Reduces this frame's depth for next frame's occlusion test.")]
public sealed partial class HiZNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("Depth", "Depth", CompositorFieldKind.Text, "The depth texture to reduce.")
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) => new HiZAsset {
        Name = Named("Hi-Z"),
        Enabled = Flag("Enabled", true),
        Depth = Text("Depth")
    };
}

/// <summary>The culling dispatch, and the draw arguments it feeds.</summary>
[Node("Culling/GPU Culling", Summary = "Where the cull happens, and whether the bits come back.")]
public sealed partial class GpuCullingNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new(
            "ReadBack",
            "Read back",
            CompositorFieldKind.Toggle,
            "On is the safe reading and costs a stall. Off makes the host's list a superset.",
            Fallback: 1f
        ),
        new("IndirectDraws", "Indirect draws", CompositorFieldKind.Toggle, "Only meaningful with read back off."),
        new(
            "Phase",
            "Phase",
            CompositorFieldKind.Choice,
            "A second node with Late, placed after the draws and the Hi-Z, is what two-phase means.",
            [nameof(CullPhase.Main), nameof(CullPhase.Late)]
        )
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new GpuCullingAsset {
            Name = Named("Cull"),
            Enabled = Flag("Enabled", true),
            ReadBack = Flag("ReadBack", true),
            IndirectDraws = Flag("IndirectDraws", false),
            Phase = Choice("Phase", CullPhase.Main)
        };
}

/// <summary>Host bytes into a buffer the frame declared.</summary>
[Node("Buffers/Upload", Summary = "Copies host bytes into a declared buffer.")]
public sealed partial class BufferUploadNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("Buffer", "Buffer", CompositorFieldKind.Text),
        new("Offset", "Offset", CompositorFieldKind.Number)
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new BufferUploadAsset {
            Name = Named("Upload"),
            Enabled = Flag("Enabled", true),
            Buffer = Text("Buffer"),
            Offset = Whole("Offset", 0)
        };
}

/// <summary>A buffer the frame produced, back on the host.</summary>
[Node("Buffers/Readback", Summary = "Copies a declared buffer back, with a chosen latency.")]
public sealed partial class BufferReadbackNode : CompositorLeafNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Enabled", "Enabled", CompositorFieldKind.Toggle, Fallback: 1f),
        new("Buffer", "Buffer", CompositorFieldKind.Text),
        new("Offset", "Offset", CompositorFieldKind.Number),
        new("Size", "Size", CompositorFieldKind.Number, "Zero for the rest of the buffer."),
        new("Latency", "Latency", CompositorFieldKind.Number, "Zero is the stall. Frames in flight costs nothing.")
    ];

    /// <inheritdoc />
    protected internal override ISceneRendererAsset Emit(IReadOnlyList<ISceneRendererAsset> children) =>
        new BufferReadbackAsset {
            Name = Named("Readback"),
            Enabled = Flag("Enabled", true),
            Buffer = Text("Buffer"),
            Offset = Whole("Offset", 0),
            Size = Whole("Size", 0),
            Latency = Math.Max(Whole("Latency", 0), 0)
        };
}

/// <summary>A texture the frame declares and the render graph owns.</summary>
/// <remarks>
///     No flow ports: a declaration is something the frame <i>has</i> rather than something it does,
///     so it sits on the canvas wherever it reads best and takes no part in the chain.
/// </remarks>
[Node("Declare/Resource", Summary = "A transient target the graph may alias, skip or resize.")]
public sealed partial class ResourceNode : CompositorNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text, "What passes refer to it by."),
        new("Format", "Format", CompositorFieldKind.Text, "A PixelFormat name — Rgba8UNorm, R11G11B10UFloat…"),
        new(
            "Scale",
            "Scale",
            CompositorFieldKind.Number,
            "A fraction of the frame, so a half-res chain stays half-res on any window.",
            Fallback: 1f
        ),
        new("Width", "Width", CompositorFieldKind.Number, "Zero to take Scale of the frame's."),
        new("Height", "Height", CompositorFieldKind.Number, "Zero to take Scale of the frame's."),
        new("SampleCount", "Samples", CompositorFieldKind.Number, Fallback: 1f)
    ];

    /// <inheritdoc />
    protected internal override void Contribute(CompositorDeclarations declarations) {
        ArgumentNullException.ThrowIfNull(declarations);

        declarations.Resources.Add(new() {
            Name = Named("Target"),
            Format = Choice("Format", PixelFormat.Rgba8UNorm),
            Scale = Math.Max(Number("Scale", 1f), 0.0001f),
            Width = Math.Max(Whole("Width", 0), 0),
            Height = Math.Max(Whole("Height", 0), 0),
            SampleCount = Math.Max(Whole("SampleCount", 1), 1)
        });
    }
}

/// <summary>A buffer the frame declares and the render graph owns.</summary>
[Node("Declare/Buffer", Summary = "A transient buffer — a cluster list, a histogram.")]
public sealed partial class BufferNode : CompositorNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new("Size", "Size", CompositorFieldKind.Number, "In bytes.", Fallback: 1f)
    ];

    /// <inheritdoc />
    protected internal override void Contribute(CompositorDeclarations declarations) {
        ArgumentNullException.ThrowIfNull(declarations);

        declarations.Buffers.Add(new() {
            Name = Named("Buffer"),
            Size = Math.Max(Whole("Size", 1), 1)
        });
    }
}

/// <summary>One render stage, which nodes refer to by name.</summary>
[Node("Declare/Stage", Summary = "How a stage's work is ordered, blended and depth-tested.")]
public sealed partial class StageNode : CompositorNode {
    /// <inheritdoc />
    public override IReadOnlyList<CompositorField> Fields { get; } = [
        new("Name", "Name", CompositorFieldKind.Text),
        new(
            "SortMode",
            "Sort",
            CompositorFieldKind.Choice,
            Options: [
                nameof(RenderSortMode.FrontToBack),
                nameof(RenderSortMode.BackToFront),
                nameof(RenderSortMode.ByGroup)
            ]
        ),
        new(
            "Blend",
            "Blend",
            CompositorFieldKind.Choice,
            Options: [
                nameof(BlendPreset.Opaque),
                nameof(BlendPreset.AlphaBlend),
                nameof(BlendPreset.PremultipliedAlpha),
                nameof(BlendPreset.Additive)
            ]
        ),
        new(
            "Depth",
            "Depth",
            CompositorFieldKind.Choice,
            Options: [
                nameof(DepthPreset.TestAndWrite),
                nameof(DepthPreset.TestOnly),
                nameof(DepthPreset.Disabled)
            ]
        ),
        new(
            "Cull",
            "Cull",
            CompositorFieldKind.Choice,
            Options: [nameof(CullMode.Back), nameof(CullMode.Front), nameof(CullMode.None)]
        ),
        new("DepthBias", "Depth bias", CompositorFieldKind.Number),
        new("DepthBiasSlope", "Depth bias slope", CompositorFieldKind.Number),
        new(
            "DepthClamp",
            "Depth clamp",
            CompositorFieldKind.Toggle,
            "So a caster in front of the near plane still casts."
        )
    ];

    /// <inheritdoc />
    protected internal override void Contribute(CompositorDeclarations declarations) {
        ArgumentNullException.ThrowIfNull(declarations);

        declarations.Stages.Add(new() {
            Name = Named("Stage"),
            SortMode = Choice("SortMode", RenderSortMode.FrontToBack),
            Blend = Choice("Blend", BlendPreset.Opaque),
            Depth = Choice("Depth", DepthPreset.TestAndWrite),
            Cull = Choice("Cull", CullMode.Back),
            DepthBias = Number("DepthBias", 0f),
            DepthBiasSlope = Number("DepthBiasSlope", 0f),
            DepthClamp = Flag("DepthClamp", false)
        });
    }
}
