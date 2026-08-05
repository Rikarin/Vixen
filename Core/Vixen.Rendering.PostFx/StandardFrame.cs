// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.PostFx;

/// <summary>How the standard frame shadows its sun and its lamps.</summary>
public enum ShadowMode {
    /// <summary>No shadow passes, no atlases, no caster stage.</summary>
    Off,

    /// <summary>Cascades for the sun and an atlas for the lamps.</summary>
    Cascades,

    /// <summary>
    ///     <see cref="Cascades" /> plus the virtual shadow map, as the A/B sample 13 runs: the map
    ///     answers where it has a drawn page and the cascades cover everywhere it does not yet.
    /// </summary>
    Virtual
}

/// <summary>How much of doc 19's global-illumination stack the frame runs.</summary>
public enum GiMode {
    /// <summary>Direct light only. The frame draws a single colour target and never splits.</summary>
    Off,

    /// <summary>
    ///     The occlusion pair — distance-field and screen-space — over the ambient split, with no
    ///     probe machinery: shaded ambient, honestly darkened.
    /// </summary>
    Ambient,

    /// <summary>The full stack: clipmap, irradiance field, surface cache and screen-probe gather.</summary>
    Probes
}

/// <summary>Where reflections come from.</summary>
public enum ReflectionsMode {
    /// <summary>None.</summary>
    Off,

    /// <summary>
    ///     Reserved for baked probe reflections; emits nothing yet. A member now rather than later
    ///     because a <c>[DataContract]</c> enum's values are a document's vocabulary, and inserting
    ///     one between existing members renumbers what saved documents already say.
    /// </summary>
    Probe,

    /// <summary>The traced screen reflections, composited through the ambient combine.</summary>
    Screen
}

/// <summary>Which antialiasing runs, and therefore whether the frame has motion vectors.</summary>
public enum AntialiasingMode {
    /// <summary>None.</summary>
    Off,

    /// <summary>FXAA alone, after the tonemap. Needs nothing from the scene.</summary>
    Fxaa,

    /// <summary>The temporal resolve, which emits the velocity pass and the Motion stage.</summary>
    Taa,

    /// <summary>Both: TAA converges the still frame, FXAA catches what its history clipped.</summary>
    TaaFxaa
}

/// <summary>Whether the frame meters itself.</summary>
public enum ExposureMode {
    /// <summary>The camera's own exposure — the lens decides, or a later look profile pins it.</summary>
    Fixed,

    /// <summary>The histogram meter, with the tonemap reading its buffer.</summary>
    Automatic
}

/// <summary>The standard frame's named insertion points — nodes of the project's own, spliced in.</summary>
/// <remarks>
///     The URP "Renderer Features" lesson, narrowed to three seams: a project that needs one custom
///     pass adds it here and never forks the frame. Each list is ordinary node assets, spliced in
///     the order written; an empty list costs nothing. Where each lands is
///     <see cref="StandardFrameAsset" />'s contract, stated on the members below rather than
///     discovered by experiment.
/// </remarks>
[DataContract("StandardFrameExtensions")]
public sealed record StandardFrameExtensions {
    /// <summary>Runs after the Main pass, before the velocity and particle passes.</summary>
    /// <remarks>Where a pass that draws more scene belongs — decals, a custom stage — so what it
    ///     writes is in the depth the velocity pass shares and in the colour the sparks add to.</remarks>
    public ISceneRendererAsset[] AfterOpaque { get; init; } = [];

    /// <summary>Runs after lighting is whole — after the ambient combine — and before the temporal
    ///     resolve and everything downstream of it.</summary>
    /// <remarks>The earliest point a full-screen effect may read the frame: HDR, lit, and not yet
    ///     accumulated, so whatever it writes is antialiased and graded with the scene.</remarks>
    public ISceneRendererAsset[] BeforePost { get; init; } = [];

    /// <summary>Runs at the very end, after the last node has written the output resource.</summary>
    /// <remarks>Display-referred, eight bits: a debug overlay or a transition wipe belongs here, a
    ///     lighting effect does not.</remarks>
    public ISceneRendererAsset[] BeforeUi { get; init; } = [];
}

/// <summary>The engine-owned frame, as one node: docs/plan/39's <c>!StandardFrame</c>.</summary>
/// <remarks>
///     <para>
///         Expands at build time into the same node graph a hand-authored document would contain —
///         sample 13's <c>Frame.vxcompositor</c> at full knobs, the bare sky/opaque/tonemap chain at
///         none. The knobs are few and semantic on purpose: they say what the game wants, never how
///         the frame is wired, and everything the expansion emits (resource extents, load actions,
///         seat/publisher pairs, pass order) is the invariant the 2026-08-04 audit paid for, encoded
///         once here instead of transcribed per project.
///     </para>
///     <para>
///         The type lives in the effect set rather than beside the compositor schema, and not by
///         accident: the expansion emits <see cref="SkyAsset" />, <see cref="TonemapAsset" /> and
///         their siblings, which <c>Vixen.Rendering</c> must not reference — the builder reaches
///         node kinds it does not know through <see cref="ISceneRendererFactory" />, and this node
///         reaches the builder the same way, through <see cref="PostEffectFactory" /> implementing
///         <see cref="ICompositorAssetTransformer" />. Registering the factory a host already
///         registers is therefore the whole installation.
///     </para>
///     <para>
///         ⚠ <b>Two facts stay the host's, exactly as they are for a hand-authored frame.</b> The
///         caster stages are extraction's: a host whose objects should cast adds <c>"Shadow"</c>
///         (and <c>"Motion"</c> for TAA) to <c>GraphicsOptions.CasterStages</c>, because a frame
///         document cannot decide what an object is extracted as. And the ambient split is the
///         material's: <c>gi</c> above <see cref="GiMode.Off" /> emits the split targets and the
///         combine, which pay off when the shading pass runs with <c>ForwardPlus.SplitOutputs</c>
///         on. Driving both from the expansion is owed to a later increment; until then they are
///         stated here rather than discovered.
///     </para>
///     <para>
///         Artistic values are deliberately neutral — the shipped node defaults, untouched — because
///         look belongs to the <c>.vxlook</c> profile (<see cref="Look" />), not to pipeline
///         structure: it lands on the emitted nodes through the volume fold at run time, so editing
///         it relights the same expanded document with nothing rebuilt.
///         What is not neutral is structure: depth read-only on the velocity and particle passes,
///         back-face culling and zero raster bias on the caster stage, the per-target load lists,
///         the TAA-before-fog ordering. Those are correctness, and they are this type's whole point.
///     </para>
/// </remarks>
[DataContract("StandardFrame")]
public sealed record StandardFrameAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = string.Empty;

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     The scalability tier the numeric sub-knobs are taken from, or null for the host's pick.
    /// </summary>
    /// <remarks>
    ///     Nullable on the opinion model's terms rather than defaulting to a tier: a document that
    ///     writes <c>quality: High</c> has decided, and one that writes nothing has left the
    ///     decision to whoever launched it — <c>GraphicsOptions.Quality</c>, handed through
    ///     <see cref="CompositorBuilder.Quality" />, which is what a settings screen switches
    ///     without editing the document. Flattening the two readings onto High would make the
    ///     platform pick a dead letter for every document the template ever stamped out.
    /// </remarks>
    public QualityTier? Quality { get; init; }

    /// <summary>Per-document quality overrides — the top layer of doc 39's waterfall.</summary>
    /// <remarks>
    ///     A whole <see cref="RenderQualityAsset" /> inline, not a reference: the document format
    ///     carries the node's own <c>[DataContract]</c> members, so the override travels with the
    ///     frame and the expansion stays pure. It folds over the project preset
    ///     (<see cref="PostEffectFactory.Preset" />) and the engine table per parameter — see
    ///     <see cref="RenderQuality.Resolve" />. Null says nothing, which is the ordinary case.
    /// </remarks>
    public RenderQualityAsset? Preset { get; init; }

    /// <summary>The project's look profile — the artistic base under the volume stack.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one knob on this node the expansion does not read.</b> The emitted post nodes
    ///         stay neutral whatever this says, because the look reaches them at run time through the
    ///         volume fold — the same document relights when the look is edited, with nothing
    ///         rebuilt. What the expansion's transformer does instead is deposit it on
    ///         <see cref="CompositorBuilder.Look" /> for the host to hand to
    ///         <c>PostProcessVolumeSystem.Look</c>; a snapshot of the expansion with and without one
    ///         is therefore identical, and tested to be.
    ///     </para>
    ///     <para>
    ///         Inline for phase 1, exactly as <see cref="Preset" /> is and for the same purity
    ///         reason: the document carries the asset's members, so no content IO enters the
    ///         transform. Doc 39's <c>look: Assets/Dusk.vxlook</c> reference spelling rides the
    ///         asset-reference machinery of a later increment; until then a host may equally load a
    ///         standalone <c>.vxlook</c> itself — <c>GraphicsOptions.Look</c> — and a document that
    ///         writes one inline out-votes that, the way an authored <see cref="Quality" /> out-votes
    ///         the platform's pick.
    ///     </para>
    /// </remarks>
    public LookAsset? Look { get; init; }

    /// <summary>Sun and lamp shadows.</summary>
    public ShadowMode Shadows { get; init; } = ShadowMode.Cascades;

    /// <summary>The global-illumination stack.</summary>
    public GiMode Gi { get; init; } = GiMode.Off;

    /// <summary>Reflections.</summary>
    public ReflectionsMode Reflections { get; init; } = ReflectionsMode.Off;

    /// <summary>Antialiasing — and, through it, whether the frame has motion vectors.</summary>
    public AntialiasingMode Antialiasing { get; init; } = AntialiasingMode.Fxaa;

    /// <summary>Whether the frame meters itself or trusts the camera.</summary>
    public ExposureMode Exposure { get; init; } = ExposureMode.Fixed;

    /// <summary>
    ///     Whether the additive particle pass and its stage are emitted.
    /// </summary>
    /// <remarks>
    ///     On by default because the pass is cheap when the stage is empty, and because the stage
    ///     has to exist before a <c>ParticleStage</c> can name it — a project that adds an emitter
    ///     should not also have to know which pass its sparks land in.
    /// </remarks>
    public bool Particles { get; init; } = true;

    /// <summary>The resource the finished frame is written to.</summary>
    /// <remarks>
    ///     Declared by the expansion <em>and</em> importable by the host, on sample 13's terms: an
    ///     import wins over a declaration of the same name, which is what lets one document write
    ///     the window at run time and a scratch texture in a test.
    /// </remarks>
    public string Output { get; init; } = "SceneColour";

    /// <summary>The project's own nodes, spliced at the three named seams.</summary>
    public StandardFrameExtensions Extensions { get; init; } = new();
}

/// <summary>Expands a <see cref="StandardFrameAsset" /> into the graph it stands for.</summary>
/// <remarks>
///     <para>
///         Internal, and the door is <see cref="PostEffectFactory" />: the expansion is an
///         implementation of the node, not a second API — a host that wants the expanded document
///         registers the factory and builds, and the explode tooling of doc 39's later increments
///         will go through the same seam. Deterministic by construction: the same asset in, a
///         structurally identical document out, with no clock, no randomness and no environment in
///         it — which is what lets the tests snapshot structure instead of pictures.
///     </para>
///     <para>
///         Resource names are sample 13's canonical ones (<c>SceneHdr</c>, <c>SceneDepth</c>,
///         <c>ShadowAtlas</c>…), unprefixed on purpose: the expansion is meant to produce the
///         document a person would have written, and the explode path depends on that. A document
///         may declare resources or stages of its own beside the node; one that redeclares a
///         canonical name <em>differently</em> is refused by name rather than silently overridden,
///         because both readings of that document are somebody's bug.
///     </para>
/// </remarks>
static class StandardFrame {
    // The canonical names, sample 13's. Consts so the expansion and its tests cannot drift apart
    // one string at a time.
    const string SceneHdr = "SceneHdr";
    const string SceneDepth = "SceneDepth";
    const string SceneAlbedo = "SceneAlbedo";
    const string SceneNormals = "SceneNormals";
    const string SceneMotion = "SceneMotion";
    const string ShadowAtlas = "ShadowAtlas";
    const string PunctualAtlas = "PunctualShadowAtlas";
    const string Camera = "Camera";
    const string FogVolume = "FogVolume";

    /// <summary>Expands every <c>!StandardFrame</c> in the document, or returns it untouched.</summary>
    /// <param name="document">The authored document.</param>
    /// <param name="fallback">
    ///     The tier for a frame whose <see cref="StandardFrameAsset.Quality" /> is null — the
    ///     platform's pick, <see cref="CompositorBuilder.Quality" /> on the build path.
    /// </param>
    /// <param name="project">
    ///     The project's quality preset, folded under the document's own
    ///     <see cref="StandardFrameAsset.Preset" /> — see <see cref="RenderQuality.Resolve" />.
    /// </param>
    /// <exception cref="InvalidOperationException">
    ///     Two frame nodes, a frame node inside a render pass, or a canonical resource or stage the
    ///     document already declares differently.
    /// </exception>
    /// <param name="notes">
    ///     Where a sentence about each emitted declaration goes — emitted instance to comment — or
    ///     <see langword="null" /> to skip writing any. The explode path's half of the expansion:
    ///     the graph is the same either way, and the notes are what lets the exploded <em>text</em>
    ///     carry the explanations this file states in prose instead of shipping eleven hundred
    ///     uncommented lines. ⚠ Keyed by <em>reference</em>, and the caller's dictionary has to say
    ///     so (<see cref="ReferenceEqualityComparer" />): the values are records, so two emitted
    ///     nodes that happen to agree member-for-member would otherwise be one key.
    /// </param>
    public static GraphicsCompositorAsset Expand(
        GraphicsCompositorAsset document,
        QualityTier fallback = QualityTier.High,
        RenderQualityAsset? project = null,
        IDictionary<object, string>? notes = null
    ) {
        if (document.Game is not { } game) {
            return document;
        }

        var found = new List<StandardFrameAsset>();
        Find(game, found, inPass: false);

        if (found.Count == 0) {
            return document;
        }

        // One frame per document, because two would race for the same canonical resources — and a
        // document with two whole frames in it does not mean anything a refusal is hiding.
        if (found.Count > 1) {
            throw new InvalidOperationException(
                "This document contains more than one !StandardFrame. The node stands for the whole "
                + "frame, so a second one would redeclare every resource the first emitted."
            );
        }

        var frame = found[0];
        var expanded = Emit(frame, RenderQuality.Resolve(frame.Quality ?? fallback, project, frame.Preset));

        if (notes is not null) {
            Annotate(notes, frame, expanded.Stages, expanded.Resources, expanded.Root);
        }

        return document with {
            Stages = Merge(document.Stages, expanded.Stages, stage => stage.Name, "stage"),
            Resources = Merge(document.Resources, expanded.Resources, resource => resource.Name, "resource"),
            Game = Replace(game, frame, expanded.Root)
        };
    }

    /// <summary>The inline look the document's frame node carries, or null.</summary>
    /// <remarks>
    ///     The half of the node <see cref="Expand" /> deliberately does not read — the look is
    ///     runtime layering, not structure — surfaced separately so the transformer can deposit it on
    ///     <see cref="CompositorBuilder.Look" />. Called after a successful expansion, so the "two
    ///     frames" and "frame inside a pass" refusals have already fired and cannot fire differently
    ///     here.
    /// </remarks>
    public static LookAsset? LookOf(GraphicsCompositorAsset document) {
        if (document.Game is not { } game) {
            return null;
        }

        var found = new List<StandardFrameAsset>();
        Find(game, found, inPass: false);

        return found.Count == 1 ? found[0].Look : null;
    }

    static void Find(ISceneRendererAsset node, List<StandardFrameAsset> found, bool inPass) {
        switch (node) {
            case StandardFrameAsset frame:
                // Inside a render pass the expansion's own passes would nest, which the RHI cannot
                // record — refused here, where the message can say so, rather than downstream where
                // it would be an unbuildable node kind.
                if (inPass) {
                    throw new InvalidOperationException(
                        "!StandardFrame expands into render passes and cannot sit inside one. Place "
                        + "it at the document root or in a plain !Sequence."
                    );
                }

                found.Add(frame);
                break;

            case SequenceAsset sequence:
                foreach (var child in sequence.Children) {
                    Find(child, found, inPass);
                }

                break;

            case RenderPassAsset pass:
                foreach (var child in pass.Children) {
                    Find(child, found, inPass: true);
                }

                break;
        }
    }

    static ISceneRendererAsset Replace(
        ISceneRendererAsset node,
        StandardFrameAsset frame,
        ISceneRendererAsset expanded
    ) {
        if (ReferenceEquals(node, frame)) {
            return expanded;
        }

        if (node is SequenceAsset sequence) {
            var children = new ISceneRendererAsset[sequence.Children.Length];

            for (var index = 0; index < children.Length; index++) {
                children[index] = Replace(sequence.Children[index], frame, expanded);
            }

            return sequence with { Children = children };
        }

        return node;
    }

    /// <summary>
    ///     The document's declarations and the expansion's, one list. An identical redeclaration is
    ///     collapsed — the document agreeing with the expansion is not a conflict — and a differing
    ///     one is refused by name, because silently preferring either side is a frame that disagrees
    ///     with its own file.
    /// </summary>
    static TDeclared[] Merge<TDeclared>(
        TDeclared[] documents,
        TDeclared[] added,
        Func<TDeclared, string> name,
        string kind
    ) where TDeclared : class {
        var result = new List<TDeclared>(documents);

        foreach (var declaration in added) {
            var existing = documents.FirstOrDefault(candidate => name(candidate) == name(declaration));

            if (existing is null) {
                result.Add(declaration);
            } else if (!existing.Equals(declaration)) {
                throw new InvalidOperationException(
                    $"This document declares {kind} '{name(declaration)}' differently from the "
                    + "!StandardFrame expansion. The name is the expansion's contract — rename the "
                    + "document's, or drop it and take the expanded one."
                );
            }
        }

        return [.. result];
    }

    // The numeric sub-knobs one tier name folds used to be a hardcoded table here; it is now
    // RenderQuality.EngineDefaults, expressed as the same asset type a project's .vxpreset
    // overrides, and what arrives below is the fold's flat product. The emission only ever reads
    // the struct — which is exactly what let the table move without touching a pass.
    static (RenderStageAsset[] Stages, RenderResourceAsset[] Resources, SequenceAsset Root) Emit(
        StandardFrameAsset frame,
        ResolvedQuality tier
    ) {
        var culling = tier.Culling != CullingMode.Off;

        var shadows = frame.Shadows != ShadowMode.Off;
        var probes = frame.Gi == GiMode.Probes;
        var mirrors = frame.Reflections == ReflectionsMode.Screen;

        // The ambient split: three targets on Main and the combine at the far end. Reflections need
        // it too, because the combine's validity blend is the only compositor the traced plane has.
        var split = frame.Gi != GiMode.Off || mirrors;

        // TAA is what pays for the velocity pass; motion blur rides it rather than justifying one.
        var motion = frame.Antialiasing is AntialiasingMode.Taa or AntialiasingMode.TaaFxaa;
        var fxaa = frame.Antialiasing is AntialiasingMode.Fxaa or AntialiasingMode.TaaFxaa;
        var metered = frame.Exposure == ExposureMode.Automatic;

        var stages = new List<RenderStageAsset> { new() { Name = "Opaque" } };

        if (shadows) {
            // Back faces and zero raster bias, and both are structural: front-culling records the
            // far side of a wide caster (the sun inside closed buildings), and a slope-scaled
            // raster bias explodes on geometry edge-on to a low sun. The biases that replace them
            // are the shadow node's own, in metres, per cascade. Depth clamp so a caster in front
            // of the light's near plane still casts.
            stages.Add(
                new() {
                    Name = "Shadow",
                    Shader = "ShadowCaster",
                    Cull = CullMode.Back,
                    DepthBias = 0f,
                    DepthBiasSlope = 0f,
                    DepthClamp = true
                }
            );
        }

        if (motion) {
            // Test-only because the pass runs after Main and shares its depth: a fragment that
            // lost the depth test is behind something and has no business writing its velocity.
            stages.Add(
                new() {
                    Name = "Motion",
                    Shader = "MotionVectors",
                    Depth = DepthPreset.TestOnly,
                    SortMode = RenderSortMode.FrontToBack
                }
            );
        }

        if (frame.Particles) {
            // Additive because a spark is light added to what is behind it; test-only because it
            // occludes nothing; cull off because a billboard's winding depends on the camera; and
            // back-to-front not for the blend — additive commutes — but for the depth test, where
            // a near spark drawn first would reject a far one behind it.
            stages.Add(
                new() {
                    Name = "Embers",
                    Blend = BlendPreset.Additive,
                    Depth = DepthPreset.TestOnly,
                    Cull = CullMode.None,
                    SortMode = RenderSortMode.BackToFront
                }
            );
        }

        var resources = new List<RenderResourceAsset> {
            // Declared and importable: the host's swapchain import wins over this declaration,
            // which is what lets the same expansion write a window and a test's scratch texture.
            new() {
                Name = frame.Output,
                Format = PixelFormat.Bgra8UNormSrgb,
                Usage = TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.Storage
            },

            // What the scene is drawn into, and it is not the output: shading writes radiance well
            // past one, and eight bits would clip every highlight before the tonemap shapes them.
            // Scaled by the tier's render scale — as is every scene-sized plane below, and never
            // the output above, which is the window's: the upscale happens wherever a pass reads a
            // scaled plane and writes an unscaled one.
            new() {
                Name = SceneHdr,
                Format = PixelFormat.Rgba16Float,
                Usage = TextureUsage.ColourTarget | TextureUsage.Sampled | TextureUsage.Storage,
                Scale = tier.RenderScale
            },

            // CopySource only when the probe gather exists, because its host-side placement reads
            // this depth back every frame — without the flag that copy is a validation error per
            // frame while every counter says the gather ran.
            new() {
                Name = SceneDepth,
                Format = PixelFormat.Depth32Float,
                Usage = TextureUsage.DepthStencilTarget
                    | TextureUsage.Sampled
                    | (probes ? TextureUsage.CopySource : TextureUsage.None),
                Scale = tier.RenderScale
            }
        };

        if (shadows) {
            // The extents are the nodes' own arithmetic, never literals: the cascade fold has
            // always been two columns, and an atlas declared to any other shape is an empty
            // scissor for the outer cascades — the audit's hardest silent failure.
            var cascadeAtlas = ShadowCascades.AtlasSize(tier.CascadeCount, tier.CascadeResolution);

            // PunctualShadowRenderer.AtlasSize's arithmetic — tilesPerSide × resolution, square —
            // from the same two numbers the node below is given, so the two cannot disagree.
            var punctualSide = Math.Max(tier.PunctualTilesPerSide, 1) * tier.PunctualResolution;

            resources.Add(
                new() {
                    Name = ShadowAtlas,
                    Format = PixelFormat.Depth32Float,
                    Usage = TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
                    Width = cascadeAtlas.X,
                    Height = cascadeAtlas.Y
                }
            );

            resources.Add(
                new() {
                    Name = PunctualAtlas,
                    Format = PixelFormat.Depth32Float,
                    Usage = TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
                    Width = punctualSide,
                    Height = punctualSide
                }
            );
        }

        if (split) {
            // The split planes: what every surface is, kept apart from what lit it. Rgba16Float on
            // the visibility resolve's terms — its storage-view path decodes exactly this format.
            resources.Add(
                new() {
                    Name = SceneAlbedo,
                    Format = PixelFormat.Rgba16Float,
                    Usage = TextureUsage.ColourTarget | TextureUsage.Sampled,
                    Scale = tier.RenderScale
                }
            );

            resources.Add(
                new() {
                    Name = SceneNormals,
                    Format = PixelFormat.Rgba16Float,
                    Usage = TextureUsage.ColourTarget
                        | TextureUsage.Sampled
                        | (probes ? TextureUsage.CopySource : TextureUsage.None),
                    Scale = tier.RenderScale
                }
            );
        }

        if (motion) {
            // Signed float, not unorm: a motion vector points either way and a fast pan moves a
            // pixel a long way, so an unsigned format folds half the screen's motion onto the
            // other half and eight bits quantise a slow pan into steps TAA then accumulates.
            resources.Add(
                new() {
                    Name = SceneMotion,
                    Format = PixelFormat.Rg16Float,
                    Usage = TextureUsage.ColourTarget | TextureUsage.Sampled,
                    Scale = tier.RenderScale
                }
            );
        }

        var nodes = new List<ISceneRendererAsset>();

        if (culling) {
            // Sample 13's trade, taken as the default of the tiers that cull at all: no readback —
            // the in-frame wait was the frame's single hardest stall — with the indirect arguments
            // doing the removing the host's superset list no longer does. ReadBack is the preset's
            // opt-back-in to the safe reading.
            nodes.Add(
                new GpuCullingAsset {
                    Name = "Cull",
                    ReadBack = tier.Culling == CullingMode.ReadBack,
                    IndirectDraws = tier.Culling is CullingMode.Indirect or CullingMode.LatePhase
                }
            );
        }

        if (frame.Gi != GiMode.Off) {
            // The qualified slot name and the ForwardPlus passes line are one contract: the
            // clipmap writes bindings under `<pass>.<composed type>`, and a pass that composes the
            // field without this line is a set written short — every draw refused.
            nodes.Add(
                new GlobalDistanceFieldAsset {
                    Name = "Clipmap",
                    Shader = "DistanceFieldAo.GlobalDistanceField",
                    Passes = ["ForwardPlus.GlobalDistanceField"]
                }
            );
        }

        if (probes) {
            nodes.Add(
                new IrradianceFieldAsset {
                    Name = "Probes",
                    Budget = tier.IrradianceBudget,
                    DilationPasses = tier.DilationPasses,
                    Passes = ["IndirectDiffuse", "ForwardPlus"]
                }
            );

            nodes.Add(new SurfaceCacheAsset { Name = "Cache" });
        }

        if (shadows) {
            // Before anything that shades, because Main reads the atlas this fills. The camera
            // view is not decoration: without it every cascade is fitted to the node's fallback
            // camera and the shadows cover somewhere nobody is looking.
            nodes.Add(
                new ShadowMapAsset {
                    Name = "Sun",
                    Stage = "Shadow",
                    Atlas = ShadowAtlas,
                    View = Camera,
                    CascadeCount = tier.CascadeCount,
                    Resolution = tier.CascadeResolution,
                    ShadowDistance = tier.ShadowDistance,
                    SplitLambda = tier.SplitLambda
                }
            );

            nodes.Add(
                new PunctualShadowAsset {
                    Name = "Lamps",
                    Stage = "Shadow",
                    Atlas = PunctualAtlas,
                    Resolution = tier.PunctualResolution,
                    TilesPerSide = tier.PunctualTilesPerSide,
                    ConstantBias = tier.ConstantBias,
                    SlopeBias = tier.SlopeBias,

                    // Without this line the atlas is rendered and shown to nobody — the entry is
                    // qualified because a composed slot's bindings are named for what fills it.
                    Passes = ["ForwardPlus.PunctualShadowAtlas"]
                }
            );
        }

        // ⚠ Here, and not beside the composite. The three dispatches need shadows and lights and
        // not scene colour, so they belong after the shadow passes and before anything draws — and
        // saying that as an edge is what puts the barriers in. The composite that reads what they
        // wrote is the "Air" node far below, after the temporal accumulation, which is safe
        // precisely because the volume does its temporal work in its own space.
        if (tier.Fog && tier.VolumetricFog) {
            nodes.Add(
                new VolumetricFogAsset {
                    Name = "Volumetrics",
                    View = Camera,
                    Output = FogVolume,
                    Slices = tier.VolumetricSlices,
                    Far = tier.VolumetricFar,

                    // ⚠ The atlas is named unconditionally and this is the only gate. Detection is
                    // the floor — a frame with no Sun node goes unshadowed whatever this says — and
                    // the tier is a ceiling under it, because the taps are the feature's whole cost.
                    Shadows = tier.VolumetricShadows
                }
            );
        }

        // The background fills a target the Main pass then *loads* — that is what puts the sky
        // behind the level, and why the sky has no target of its own.
        nodes.Add(new SkyAsset { Name = "Sky", Output = SceneHdr, View = Camera });

        var sceneTextures = shadows
            ? new ScenePublishAsset[] {
                // The consuming pass publishes, not the producing one: a graph resource's barrier
                // belongs to whoever declared it read, so the shadow nodes hand over matrices and
                // samplers and this line hands over the textures.
                new() { Binding = "shadowMap", Resource = ShadowAtlas },

                // Under the compose slot's name, so the key that reaches set 0 is
                // `ForwardPlus.PunctualShadowAtlas.atlas` — any other spelling is a set written
                // short rather than a shadow that does not appear.
                new() { Binding = "PunctualShadowAtlas.atlas", Resource = PunctualAtlas }
            }
            : [];

        // Member order is target order and the shader dictates it: location 0 is direct light,
        // 1 albedo with occlusion in alpha, 2 world normal with roughness in alpha. Only SceneHdr
        // is loaded — that is the sky — while the split planes clear, because loading a target no
        // pass produced is refused by the graph.
        nodes.Add(
            new RenderPassAsset {
                Name = "Main",
                ColourTargets = split ? [SceneHdr, SceneAlbedo, SceneNormals] : [SceneHdr],
                DepthTarget = SceneDepth,
                Loaded = [SceneHdr],
                SceneTextures = sceneTextures,
                Children = [new SingleStageAsset { Name = "Opaque", View = Camera, Stage = "Opaque" }]
            }
        );

        Splice(nodes, frame.Extensions.AfterOpaque);

        if (motion) {
            // After Main and sharing its depth read-only, so the velocity written for a pixel is
            // the velocity of whatever ended up visible there.
            nodes.Add(
                new RenderPassAsset {
                    Name = "Velocity",
                    ColourTargets = [SceneMotion],
                    DepthTarget = SceneDepth,
                    DepthLoad = LoadAction.Load,
                    ReadOnlyDepth = true,
                    Children = [
                        new SingleStageAsset { Name = "MotionVectors", View = Camera, Stage = "Motion" }
                    ]
                }
            );
        }

        if (frame.Particles) {
            // A pass of its own rather than a second child of Main, because Main may declare three
            // colour targets and a particle shader returns one value — additively blending
            // undefined attachments into the split planes. One target here and the question does
            // not arise. Before the post chain, so an ember goes through the whole photometric
            // chain and reads as light rather than as dots pasted on.
            nodes.Add(
                new RenderPassAsset {
                    Name = "Sparks",
                    ColourTargets = [SceneHdr],
                    DepthTarget = SceneDepth,
                    DepthLoad = LoadAction.Load,
                    ReadOnlyDepth = true,
                    Loaded = [SceneHdr],
                    Children = [new SingleStageAsset { Name = "Embers", View = Camera, Stage = "Embers" }]
                }
            );
        }

        if (culling) {
            // After every pass that touches SceneDepth, so the reduce sees the frame's finished
            // depth; what consumes it is next frame's cull, which is the one frame of staleness
            // the one-phase shape costs.
            nodes.Add(new HiZAsset { Name = "Occluders", Depth = SceneDepth });

            if (tier.Culling == CullingMode.LatePhase) {
                // The second question, after the pyramid holds this frame's own depth: what main
                // rejected against last frame's is asked about again, so nothing pops at the
                // trailing edge. Ordering is the feature — after Occluders, by construction here.
                nodes.Add(
                    new GpuCullingAsset { Name = "LateCull", Phase = CullPhase.Late, IndirectDraws = true }
                );
            }
        }

        if (frame.Shadows == ShadowMode.Virtual) {
            // The A/B, not a swap: the map answers where it has a drawn page and the cascades
            // above cover everywhere else, so the sun is shadowed exactly once at every pixel and
            // never not at all. After the depth is finished, on HiZ's terms: the marking pass
            // reads this frame's depth and is serviced next frame.
            nodes.Add(
                new VirtualShadowAsset {
                    Name = "SunPages",
                    Stage = "Shadow",
                    Depth = SceneDepth,
                    View = Camera,
                    Levels = tier.VirtualLevels,
                    FirstExtent = tier.VirtualFirstExtent,
                    PagesPerFrame = tier.VirtualPagesPerFrame,
                    Passes = ["ForwardPlus.VirtualShadowLookup"]
                }
            );
        }

        if (probes) {
            // A target of its own rather than SceneHdr: the upsample draws irradiance over π,
            // which wants multiplying by albedo, and the combine below is the pass that multiplies.
            nodes.Add(
                new ScreenProbeGatherAsset {
                    Name = "Gather",
                    Depth = SceneDepth,
                    Normals = SceneNormals,
                    Output = "ProbeIrradiance",
                    View = Camera,
                    TileSize = tier.ProbeTileSize,
                    ScreenTraces = tier.ScreenTraces
                }
            );
        }

        if (mirrors) {
            // Colour is SceneHdr by deliberate scheduling: at this point it holds the sky, the
            // level and the sparks, so a screen hit reflects this frame's own lit opaques.
            nodes.Add(
                new ReflectionsAsset {
                    Name = "Mirrors",
                    Depth = SceneDepth,
                    Normals = SceneNormals,
                    Colour = SceneHdr,
                    Target = "Reflections",
                    View = Camera,
                    ScreenSteps = tier.ReflectionSteps,
                    RoughnessThreshold = tier.RoughnessThreshold
                }
            );
        }

        if (frame.Gi != GiMode.Off) {
            // One AO march for the room, one for the crease; the combine multiplies the two. The
            // sun-shadow channel follows the cascades: where a shadow map already shadows the sun,
            // a cone toward it here would shadow the sun twice, softer and wronger — so the march
            // fills it only when nothing else does.
            nodes.Add(
                new DistanceFieldAoAsset {
                    Name = "Occlusion",
                    Depth = SceneDepth,
                    Normals = SceneNormals,
                    Source = "GlobalDistanceField",
                    Output = "AmbientOcclusion",
                    SunShadow = !shadows,
                    View = Camera
                }
            );

            nodes.Add(
                new SsaoAsset {
                    Name = "ContactOcclusion",
                    Depth = SceneDepth,
                    Normals = SceneNormals,
                    View = Camera,
                    Output = "ScreenOcclusion",
                    Directions = tier.SsaoDirections,
                    Steps = tier.SsaoSteps,
                    Scale = tier.SsaoScale
                }
            );
        }

        // What the post chain reads next — advanced by every node that transforms the colour.
        var colour = SceneHdr;

        if (split) {
            // Where the frame becomes whole: direct × sun + albedo × irradiance × occlusion, with
            // reflections blended over by validity. Every seat names a plane produced above it,
            // and an absent optional stays empty rather than naming a resource of no producer.
            nodes.Add(
                new AmbientCombineAsset {
                    Name = "Combine",
                    Direct = SceneHdr,
                    Albedo = SceneAlbedo,
                    Normals = SceneNormals,
                    Irradiance = probes ? "ProbeIrradiance" : "",
                    Occlusion = frame.Gi != GiMode.Off ? "AmbientOcclusion" : "",
                    ContactOcclusion = frame.Gi != GiMode.Off ? "ScreenOcclusion" : "",
                    Reflections = mirrors ? "Reflections" : "",

                    // The AO planes above run at a fraction of the frame; the depth and the camera
                    // are what let the combine upsample them bilaterally instead of smearing
                    // half-res occlusion across every corner's depth edge.
                    Depth = frame.Gi != GiMode.Off ? SceneDepth : "",
                    View = frame.Gi != GiMode.Off ? Camera : "",
                    Output = "SceneCombined"
                }
            );

            colour = "SceneCombined";
        }

        Splice(nodes, frame.Extensions.BeforePost);

        if (motion) {
            // Before the fog, necessarily: the accumulator averages radiance, and it must only
            // ever see the finished frame — after the combine, before anything atmospheric is
            // added on top.
            nodes.Add(
                new TemporalAntialiasingAsset {
                    Name = "Accumulate",
                    Source = colour,
                    MotionVectors = SceneMotion,
                    Depth = SceneDepth,
                    Output = "SceneResolved",
                    VarianceClipping = tier.TaaVarianceClipping
                }
            );

            colour = "SceneResolved";
        }

        if (tier.Fog) {
            // Before the shutter and before every curve, because fog is light in the scene: added
            // to what a surface sent, so a blurred lantern and the haze in front of it smear
            // together.
            nodes.Add(
                new FogAsset {
                    Name = "Air",
                    Source = colour,
                    Depth = SceneDepth,
                    View = Camera,
                    Output = "SceneFogged",

                    // ⚠ The same three numbers the dispatch was given, from the same tier. The
                    // composite inverts the grid's Z distribution to find a slice, so a near, a far
                    // or a slice count that disagrees reads the wrong slice for every pixel.
                    Volume = tier.VolumetricFog ? FogVolume : "",
                    VolumeFar = tier.VolumetricFar,
                    VolumeSlices = tier.VolumetricSlices
                }
            );

            colour = "SceneFogged";
        }

        if (tier.DepthOfField) {
            nodes.Add(
                new DepthOfFieldAsset {
                    Name = "Defocus",
                    Source = colour,
                    Depth = SceneDepth,
                    View = Camera,
                    Output = "SceneDefocused",
                    Samples = tier.DofSamples,
                    MaximumRadius = tier.DofMaximumRadius
                }
            );

            colour = "SceneDefocused";
        }

        if (tier.MotionBlur && motion) {
            // Gated on the velocity pass rather than emitting one for itself: TAA is what pays
            // for motion vectors, and a blur reading a resource nothing writes is a copy.
            nodes.Add(
                new MotionBlurAsset {
                    Name = "Shutter",
                    Source = colour,
                    MotionVectors = SceneMotion,
                    View = Camera,
                    Output = "SceneBlurred",
                    Samples = tier.MotionBlurSamples
                }
            );

            colour = "SceneBlurred";
        }

        if (metered) {
            // The histogram rather than the mean, because percentiles are what a meter is for —
            // and the node publishes one number in a buffer, which the tonemap names below. Its
            // clamps are a look decision and stay at the node's own defaults until the look
            // profile owns them.
            nodes.Add(new AutoExposureAsset { Name = "Meter", Source = colour, UseHistogram = true });

            if (tier.LocalExposure) {
                nodes.Add(
                    new LocalExposureAsset {
                        Name = "Adapt",
                        Source = colour,
                        Output = "SceneAdapted",
                        Taps = tier.LocalExposureTaps
                    }
                );

                colour = "SceneAdapted";
            }
        }

        if (tier.LensFlare) {
            nodes.Add(
                new LensFlareAsset {
                    Name = "Flare",
                    Source = colour,
                    View = Camera,
                    Output = "SceneFlared"
                }
            );

            colour = "SceneFlared";
        }

        if (tier.Bloom) {
            // Publishes the pyramid and nothing else; the tonemap composites it. Wiring the
            // pyramid into `bloom:` rather than `source:` is the difference between a glow and a
            // black window with every counter reporting a frame that drew.
            nodes.Add(
                new BloomAsset {
                    Name = "Glow",
                    Source = colour,
                    Output = "BloomPyramid",
                    Levels = tier.BloomLevels,
                    FilterRadius = tier.BloomFilterRadius
                }
            );
        }

        // Everything after the tonemap is display-referred, so the last of these writes the
        // output resource and the ones before it hand eight-bit intermediates along.
        var afterTonemap = (fxaa ? 1 : 0) + (motion ? 1 : 0) + (tier.Vignette ? 1 : 0);

        nodes.Add(
            new TonemapAsset {
                Name = "Tonemap",
                Source = colour,
                Bloom = tier.Bloom ? "BloomPyramid" : "",
                ExposureBuffer = metered ? "Meter.Exposure" : "",

                // Metered, the buffer decides and no lens may fight it; fixed, the camera's own
                // aperture, shutter and ISO are the authored exposure — which is what `Fixed`
                // means until a look profile pins an EV.
                View = metered ? "" : Camera,
                Output = afterTonemap > 0 ? "SceneGraded" : frame.Output
            }
        );

        colour = afterTonemap > 0 ? "SceneGraded" : frame.Output;

        if (fxaa) {
            // After the curve, unlike the resolve above: FXAA finds edges by luminance contrast,
            // and contrast in scene-referred light is unbounded.
            afterTonemap--;

            var edges = new FxaaAsset {
                Name = "Edges",
                Source = colour,
                Output = afterTonemap > 0 ? "SceneAntialiased" : frame.Output
            };

            // The preset picks a threshold set, never one number: the thresholds only mean
            // anything together. Balanced is the shader's own defaults, left untouched.
            nodes.Add(
                tier.Fxaa switch {
                    FxaaPreset.Performance => edges with {
                        EdgeThreshold = 0.25f, EdgeThresholdMinimum = 0.0833f, SubpixelQuality = 0.5f
                    },
                    FxaaPreset.Quality => edges with {
                        EdgeThreshold = 0.063f, EdgeThresholdMinimum = 0.0312f, SubpixelQuality = 1f
                    },
                    _ => edges
                }
            );

            colour = afterTonemap > 0 ? "SceneAntialiased" : frame.Output;
        }

        if (motion) {
            // What the temporal resolve softened, put back — which is why it rides the TAA knob
            // rather than a tier: a frame with no history has nothing to recover.
            afterTonemap--;

            nodes.Add(
                new SharpenAsset {
                    Name = "Recover",
                    Source = colour,
                    Output = afterTonemap > 0 ? "SceneSharpened" : frame.Output
                }
            );

            colour = afterTonemap > 0 ? "SceneSharpened" : frame.Output;
        }

        if (tier.Vignette) {
            nodes.Add(new VignetteAsset { Name = "Glass", Source = colour, Output = frame.Output });
        }

        Splice(nodes, frame.Extensions.BeforeUi);

        var root = new SequenceAsset {
            Name = frame.Name is { Length: > 0 } name ? name : "Frame",
            Enabled = frame.Enabled,
            Children = [.. nodes]
        };

        return ([.. stages], [.. resources], root);
    }

    static void Splice(List<ISceneRendererAsset> nodes, ISceneRendererAsset[] extras) {
        foreach (var extra in extras) {
            nodes.Add(extra);
        }
    }

    /// <summary>One sentence per emitted declaration — the exploded file's comments.</summary>
    /// <remarks>
    ///     By name rather than woven through <see cref="Emit" />, so the emission stays readable and
    ///     the prose lives in one place. The names are the consts above, which is what keeps this
    ///     switch and the emission from drifting apart one string at a time; a name this switch does
    ///     not know — a spliced extension node, a project's own resource — simply gets no note,
    ///     because the expansion has no business explaining what it did not write.
    /// </remarks>
    static void Annotate(
        IDictionary<object, string> notes,
        StandardFrameAsset frame,
        RenderStageAsset[] stages,
        RenderResourceAsset[] resources,
        SequenceAsset root
    ) {
        var shadows = frame.Shadows != ShadowMode.Off;
        var probes = frame.Gi == GiMode.Probes;

        notes.TryAdd(
            root,
            "The exploded !StandardFrame. Every line below is what the node stood for; nothing regenerates it."
        );

        foreach (var resource in resources) {
            var note = resource.Name == frame.Output
                ? "Declared and importable: the host's swapchain import wins over this declaration, which is "
                + "what lets one document write the window at run time and a scratch texture in a test."
                : resource.Name switch {
                    SceneHdr =>
                        "What the scene is drawn into, and it is not the output: shading writes radiance well "
                        + "past one, and eight bits would clip every highlight before the tonemap shapes them.",
                    SceneDepth => probes
                        ? "The frame's one depth buffer. CopySource because the probe gather's host-side "
                        + "placement copies it back every frame — without the flag that copy is a validation "
                        + "error per frame while every counter says the gather ran."
                        : "The frame's one depth buffer, cleared to zero — the far plane, under reversed depth.",
                    ShadowAtlas =>
                        "The extent is ShadowCascades.AtlasSize's own arithmetic, never a literal: the fold "
                        + "is two columns, and an atlas of any other shape is an empty scissor for the outer "
                        + "cascades.",
                    PunctualAtlas =>
                        "tilesPerSide × resolution, square — the same two numbers the Lamps node is given, so "
                        + "the atlas and its renderer cannot disagree.",
                    SceneAlbedo =>
                        "The ambient split: what every surface is, kept apart from what lit it, until the "
                        + "combine multiplies the two back together.",
                    SceneNormals =>
                        "The split's other plane — world normal with roughness in alpha — read by every "
                        + "screen-space march below.",
                    SceneMotion =>
                        "Signed float, not unorm: a motion vector points either way, and eight bits would "
                        + "quantise a slow pan into steps TAA then accumulates.",
                    _ => null
                };

            if (note is not null) {
                notes.TryAdd(resource, note);
            }
        }

        foreach (var stage in stages) {
            var note = stage.Name switch {
                "Opaque" => "The scene's own draws.",
                "Shadow" =>
                    "Back faces, zero raster bias, clamped depth — all structural. The biases that replace "
                    + "them are the shadow nodes' own, in metres, per cascade.",
                "Motion" =>
                    "Test-only against Main's depth: a fragment that lost the depth test is behind something "
                    + "and has no business writing its velocity.",
                "Embers" =>
                    "Additive, test-only, unculled — and back-to-front for the depth test rather than the "
                    + "blend, which commutes.",
                _ => null
            };

            if (note is not null) {
                notes.TryAdd(stage, note);
            }
        }

        foreach (var node in root.Children) {
            var note = node.Name switch {
                "Cull" =>
                    "No readback: the in-frame wait was the frame's hardest stall, so the indirect arguments "
                    + "do the removing the host's superset list no longer does.",
                "Clipmap" =>
                    "The distance field every march below reads. The passes line is the compose-slot "
                    + "contract — without it the shading pass's set is written short and every draw refused.",
                "Probes" => "The irradiance field, filled a budgeted few probes per frame until it settles.",
                "Cache" => "The surface cache the screen-probe traces hit instead of falling through to the sky.",
                "Sun" =>
                    "Before anything that shades, because Main reads the atlas this fills. The view line is "
                    + "what fits the cascades to the camera somebody is actually looking through.",
                "Lamps" =>
                    "The lamp atlas. Without the passes line it is rendered and shown to nobody — the entry "
                    + "is qualified because a composed slot's bindings are named for what fills it.",
                "Volumetrics" =>
                    "Three dispatches that fill a froxel volume. Here because they need shadows and lights "
                    + "and not scene colour; the Air node far below is what reads what they wrote.",
                "Sky" =>
                    "Fills a target the Main pass then loads — that is what puts the sky behind the level, "
                    + "and why the sky has no target of its own.",
                "Main" =>
                    "The scene. Target order is the shader's; only the colour the sky filled is loaded, "
                    + "while the split planes clear — loading a target no pass produced is refused.",
                "Velocity" =>
                    "After Main and sharing its depth read-only, so a pixel's velocity belongs to whatever "
                    + "ended up visible there.",
                "Sparks" =>
                    "A pass of its own so a one-output additive shader never blends into the split planes; "
                    + "before the post chain, so an ember reads as light rather than as dots pasted on.",
                "Occluders" =>
                    "The depth pyramid next frame's cull tests against — after every pass that touches the "
                    + "depth, one frame of staleness by design.",
                "SunPages" =>
                    "The A/B, not a swap: the map shades where it has a drawn page and the cascades cover "
                    + "everywhere it does not yet.",
                "Gather" =>
                    "Irradiance over π into its own target, which wants multiplying by albedo — the combine "
                    + "below is the pass that multiplies.",
                "Mirrors" =>
                    "Reads the colour as it stands — sky, level, sparks — so a screen hit reflects this "
                    + "frame's own lit opaques.",
                "Occlusion" =>
                    "The room-scale march. The sun cone fills its channel only where no shadow map already "
                    + "owns the sun, or the sun would be shadowed twice, softer and wronger.",
                "ContactOcclusion" => "The crease-scale march; the combine multiplies the two occlusions.",
                "Combine" =>
                    "Where the frame becomes whole: direct + albedo × irradiance × occlusion, with "
                    + "reflections blended over by validity. Every seat names a plane produced above it.",
                "Accumulate" =>
                    "Before the fog, necessarily: the accumulator averages radiance and must only ever see "
                    + "the finished frame.",
                "Air" =>
                    "Fog is light in the scene — added before the shutter and every curve, so a blurred "
                    + "lantern and the haze in front of it smear together.",
                "Defocus" => "The lens's focus answer, while the frame is still scene-referred.",
                "Shutter" =>
                    "Rides the velocity pass TAA paid for rather than emitting one of its own — a blur "
                    + "reading a resource nothing writes would be a copy.",
                "Meter" =>
                    "The histogram rather than the mean, because percentiles are what a meter is for; it "
                    + "publishes one number in a buffer the tonemap names.",
                "Adapt" => "Local exposure over the metered frame, before the curve.",
                "Flare" => "The lens answering the brightest sources, still in scene-referred light.",
                "Glow" =>
                    "Publishes the pyramid and nothing else; the tonemap composites it. Wiring the pyramid "
                    + "into bloom: rather than source: is the difference between a glow and a black window.",
                "Tonemap" =>
                    "The curve. Metered, the buffer decides and no lens may fight it; fixed, the camera's "
                    + "own aperture, shutter and ISO are the authored exposure.",
                "Edges" =>
                    "After the curve, unlike the resolve above: FXAA finds edges by luminance contrast, and "
                    + "contrast in scene-referred light is unbounded.",
                "Recover" => "What the temporal resolve softened, put back — a frame with no history has nothing to recover.",
                "Glass" => "Display-referred, dead last before the output: the vignette is the glass, not the scene.",
                _ => null
            };

            if (note is not null) {
                notes.TryAdd(node, note);
            }
        }
    }
}
