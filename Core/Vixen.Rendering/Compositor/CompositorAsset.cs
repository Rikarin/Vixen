// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Graphics;

namespace Vixen.Rendering.Compositor;

/// <summary>One node of an authored compositor graph.</summary>
/// <remarks>
///     <para>
///         An interface with a <c>[DataContract]</c> name per implementation, which is how the rest
///         of the engine does polymorphism in a file: the contract name is the YAML tag, so
///         <c>!SingleStage</c> selects the type and nothing keeps a registration table in sync.
///     </para>
///     <para>
///         Deliberately a <em>parallel</em> model rather than annotations on
///         <see cref="SceneRenderer" /> itself. The runtime node holds texture views, render stages
///         and a command list; the asset holds names. Merging them would mean a type that is half
///         serialisable, and the half that is not is the half a file cannot express.
///     </para>
/// </remarks>
public interface ISceneRendererAsset {
    /// <summary>The node's name, for debug groups and for a human reading the file.</summary>
    string Name { get; }

    /// <summary>Whether the node runs.</summary>
    bool Enabled { get; }
}

/// <summary>A blend state by name, because a file should not spell out seven factors.</summary>
public enum BlendPreset {
    /// <summary>Overwrite.</summary>
    Opaque,

    /// <summary>Straight alpha.</summary>
    AlphaBlend,

    /// <summary>Premultiplied alpha.</summary>
    PremultipliedAlpha,

    /// <summary>Additive.</summary>
    Additive
}

/// <summary>A depth state by name.</summary>
public enum DepthPreset {
    /// <summary>Test and write, with the engine's reversed comparison.</summary>
    TestAndWrite,

    /// <summary>Test but do not write — what a transparent stage wants.</summary>
    TestOnly,

    /// <summary>No depth at all.</summary>
    Disabled
}

/// <summary>One render stage, as a file declares it.</summary>
/// <remarks>
///     Blend and depth are named presets rather than full states, because those four and those three
///     are what a stage has ever wanted and an author writing out seven blend factors is an author
///     about to get one of them wrong. A project that genuinely needs another supplies its own
///     <see cref="IPipelineDescriber" />, which is where an unusual pipeline belongs anyway.
/// </remarks>
[DataContract("RenderStage")]
public sealed record RenderStageAsset {
    /// <summary>What the stage is called, and what a node refers to it by.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>How its work is ordered.</summary>
    public RenderSortMode SortMode { get; set; } = RenderSortMode.FrontToBack;

    /// <summary>How its fragments combine with the target.</summary>
    public BlendPreset Blend { get; set; } = BlendPreset.Opaque;

    /// <summary>What its draws do with depth.</summary>
    public DepthPreset Depth { get; set; } = DepthPreset.TestAndWrite;

    /// <summary>Which faces it discards.</summary>
    public CullMode Cull { get; set; } = CullMode.Back;

    /// <summary>A constant added to depth — a shadow-caster stage's peter-panning knob.</summary>
    public float DepthBias { get; set; }

    /// <summary>A factor on the polygon's depth slope.</summary>
    public float DepthBiasSlope { get; set; }

    /// <summary>Whether to clamp depth rather than clip it, so a caster in front of near still casts.</summary>
    public bool DepthClamp { get; set; }
}

/// <summary>Several nodes, run in order.</summary>
[DataContract("Sequence")]
public sealed record SequenceAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The children, in the order they run.</summary>
    public ISceneRendererAsset[] Children { get; set; } = [];
}

/// <summary>A render pass, and what draws into it.</summary>
[DataContract("RenderPass")]
public sealed record RenderPassAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     The names of its colour attachments, in the order the shader writes them.
    /// </summary>
    /// <remarks>
    ///     Names, not textures. A texture handle belongs to a device that did not exist when the file
    ///     was written, so the file says <em>which</em> target and the host binds the name — which is
    ///     also what lets one authored compositor run against a swapchain, an offscreen buffer or a
    ///     test's scratch texture without changing.
    /// </remarks>
    public string[] ColourTargets { get; set; } = [];

    /// <summary>The name of its depth attachment, if it has one.</summary>
    public string? DepthTarget { get; set; }

    /// <summary>How many samples its attachments have.</summary>
    public int SampleCount { get; set; } = 1;

    /// <summary>What draws into it.</summary>
    public ISceneRendererAsset[] Children { get; set; } = [];
}

/// <summary>One stage drawn from one view.</summary>
[DataContract("SingleStage")]
public sealed record SingleStageAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The name of the view to draw from.</summary>
    public string View { get; set; } = string.Empty;

    /// <summary>The name of the stage to draw.</summary>
    public string Stage { get; set; } = string.Empty;
}

/// <summary>A directional light's cascaded shadow map.</summary>
[DataContract("ShadowMap")]
public sealed record ShadowMapAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The name of the stage that draws depth-only casters.</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>The name of the depth atlas to render into.</summary>
    public string Atlas { get; set; } = string.Empty;

    /// <summary>How many cascades to fit.</summary>
    public int CascadeCount { get; set; } = 4;

    /// <summary>One cascade's side in texels.</summary>
    public int Resolution { get; set; } = 1024;

    /// <summary>How far shadows are drawn — not the camera's far plane.</summary>
    public float ShadowDistance { get; set; } = 150f;

    /// <summary>How far to blend the splits from uniform toward logarithmic.</summary>
    public float SplitLambda { get; set; } = 0.75f;

    /// <summary>How far behind a cascade the light's near plane sits.</summary>
    public float Extrusion { get; set; } = 50f;
}

/// <summary>Spot and point light shadows in one atlas.</summary>
[DataContract("PunctualShadows")]
public sealed record PunctualShadowAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; set; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; set; } = true;

    /// <summary>The name of the stage that draws depth-only casters.</summary>
    public string Stage { get; set; } = string.Empty;

    /// <summary>The name of the depth atlas to render into.</summary>
    public string Atlas { get; set; } = string.Empty;

    /// <summary>One tile's side in texels.</summary>
    public int Resolution { get; set; } = 512;

    /// <summary>How many tiles the atlas is across.</summary>
    public int TilesPerSide { get; set; } = 4;
}

/// <summary>
///     A whole authored frame: the stages it has, and the tree that draws them.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/06's third idea, as a file. "Swap forward for deferred" is a different document
///         rather than a different build, and the three shipped presets are three of these over the
///         same features.
///     </para>
///     <para>
///         <see cref="Version" /> is checked rather than ignored. A file from a later editor is
///         refused by number, naming both versions, because the alternative is binding what it
///         understands and silently dropping what it does not — which produces a frame that is
///         missing a pass and says nothing about it.
///     </para>
///     <para>
///         <strong>Every member here is settable rather than init-only</strong>, throughout the
///         model. The generated binary serializer constructs an instance and then assigns to it, so
///         an <c>init</c> member is one it cannot write and silently leaves at its default — which
///         is a baked compositor that reads back empty. An asset is an editable document anyway; the
///         editor mutates one every time somebody drags a node.
///     </para>
/// </remarks>
[DataContract("GraphicsCompositor")]
public sealed record GraphicsCompositorAsset {
    /// <summary>The schema version this document is written in.</summary>
    public int Version { get; set; } = 1;

    /// <summary>The stages, which nodes refer to by name.</summary>
    public RenderStageAsset[] Stages { get; set; } = [];

    /// <summary>The root of the graph — the whole frame.</summary>
    public ISceneRendererAsset? Game { get; set; }
}
