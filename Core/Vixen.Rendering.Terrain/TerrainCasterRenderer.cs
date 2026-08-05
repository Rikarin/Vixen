// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;

namespace Vixen.Rendering.Terrain;

/// <summary>The terrain caster node the transformer splices between the sun and the frame's shading.</summary>
/// <remarks>
///     <para>
///         <b>Internal, because the transformer is its only author.</b> <see cref="TerrainFactory" />
///         inserts one wherever a document holds both a <c>!Terrain</c> node and a <c>!ShadowMap</c>
///         writing the atlas that node samples — so a standard frame gets terrain shadows from the
///         same three <c>extensions:</c> lines that got it terrain, and a hand-authored frame gets
///         them the moment its atlas names line up. A document tag would be a second way to say the
///         same thing, one that can be put in the wrong place.
///     </para>
///     <para>
///         The names are copied from the terrain node it serves, because the two are halves of one
///         contract: the caster writes the atlas the surface samples, under the pass name the
///         cascades are published against.
///     </para>
/// </remarks>
sealed record TerrainCasterNodeAsset : ISceneRendererAsset {
    /// <inheritdoc />
    public string Name { get; init; } = "TerrainCasters";

    /// <inheritdoc />
    public bool Enabled { get; init; } = true;

    /// <summary>The cascade atlas to draw into — the terrain node's own name for it.</summary>
    public string ShadowAtlas { get; init; } = "ShadowAtlas";

    /// <summary>Which pass's names the cascades are published under.</summary>
    public string ScenePass { get; init; } = "ForwardPlus";
}

/// <summary>
///     Draws every casting terrain into the sun's cascade atlas, between the cascade pass and the
///     passes that sample it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Its own node, because position in the frame is the whole point.</b> The graph runs
///         passes in declaration order, and <see cref="TerrainSceneRenderer" /> builds after the
///         Main pass — a caster pass declared there would write the atlas after the frame's
///         surfaces had already sampled it, so the hills would shadow the ground and nothing else.
///         This node sits directly after the <c>!ShadowMap</c> node, so its pass lands between the
///         cascades' clear-and-draw and the first consumer, and every receiver in the frame sees
///         the terrain's depths.
///     </para>
///     <para>
///         <b>The atlas is loaded, never cleared.</b> The cascade pass's mesh casters are already
///         in it, and the caster pipeline's reverse-Z <c>Greater</c> test merges the terrain in
///         depth-correctly — nearer of the two wins, which is what a shadow map is.
///     </para>
///     <para>
///         ⚠ <b>The draw sets are the surface node's, resolved at execute.</b> The two nodes share
///         one <see cref="TerrainSceneRenderer" />'s per-heightfield state — a caster with its own
///         would re-upload every atlas — and this node builds first, before the surface node has
///         synced its sets for the frame. So the pass declares from what extraction says
///         (<see cref="TerrainSceneSource" />, which is current) and resolves renderers inside
///         Execute, which runs after every node has built: a terrain whose set was born this frame
///         is skipped until its heightmap has been copied once, and casts from its second frame.
///     </para>
/// </remarks>
sealed class TerrainCasterRenderer : SceneRenderer {
    readonly Matrix4x4[] matrices = new Matrix4x4[ShadowCascades.MaxCascades];
    readonly List<TerrainCasterPass> active = [];
    readonly HashSet<Vixen.Terrain.Terrain> placed = [];

    TerrainShaders casterShaders;

    /// <summary>The cascade atlas this draws into.</summary>
    public string ShadowAtlas { get; init; } = "ShadowAtlas";

    /// <summary>Which pass's names the cascades are published under.</summary>
    /// <remarks>Carried for symmetry with the terrain node it serves; the cascades themselves are
    ///     read straight off the shadow node, whose <c>ShaderName</c> is one seam earlier.</remarks>
    public string ScenePass { get; init; } = "ForwardPlus";

    /// <summary>Where the frame's terrains come from — the same list the surface node draws.</summary>
    public TerrainSceneSource? Scene { get; set; }

    /// <summary>The device, or null for a node that declines to draw.</summary>
    public IGraphicsDevice? Device { get; set; }

    /// <summary>Where shader modules come from.</summary>
    public EffectPipelineDescriber? Modules { get; set; }

    /// <summary>The surface node whose draw sets the casters borrow — the factory pairs the two.</summary>
    /// <remarks>Null draws nothing quietly: a caster node no terrain node answered is a document
    ///     holding half the contract, and half a contract casts no shadow.</remarks>
    internal TerrainSceneRenderer? Surfaces { get; set; }

    /// <summary>How many cascades the last build found published.</summary>
    public int CascadeCount { get; private set; }

    /// <summary>How many terrains the last frame drew into the atlas.</summary>
    public int CastersDrawn { get; private set; }

    /// <summary>Whether the last build was still waiting for the caster shader to resolve.</summary>
    public bool WaitingForShaders { get; private set; }

    /// <inheritdoc />
    protected override void Build(GraphicsCompositor compositor, CompositorFrame frame) {
        ArgumentNullException.ThrowIfNull(compositor);
        ArgumentNullException.ThrowIfNull(frame);

        CascadeCount = 0;
        CastersDrawn = 0;
        WaitingForShaders = false;

        if (Device is null || Modules is null || Scene is not { } scene || Surfaces is not { } surfaces) {
            return;
        }

        // Nothing to cast, or nowhere to cast it: both ordinary, both free. The flag is the
        // component's CastShadows, carried through extraction — a terrain that opts out never
        // reaches the pass at all, and a frame whose every terrain opts out declares no pass.
        if (!frame.Has(ShadowAtlas)) {
            return;
        }

        var casting = false;

        foreach (var entry in scene.Terrains) {
            casting |= entry.CastShadows;
        }

        if (!casting) {
            return;
        }

        // The cascades come off the shadow node itself — Nested exists for exactly this walk, and
        // the node's Cascades are the *unfolded* matrices a rasteriser wants under its tile
        // viewports. Reading the published parameters instead would hand back the folded form,
        // whose fold is NdcToUv's business and lands every patch a quarter of the way into
        // somebody else's tile. No sun, a disabled one, or one that has not collected yet all
        // answer the same way: no tile to draw into.
        if (FindSun(compositor.Game) is not { } sun || sun.Cascades.Length == 0) {
            return;
        }

        var count = sun.Cascades.Length;

        for (var cascade = 0; cascade < count; cascade++) {
            matrices[cascade] = sun.Cascades[cascade].ViewProjection;
        }

        if (!ResolveShaders(frame)) {
            WaitingForShaders = true;
            return;
        }

        var atlas = frame.Texture(ToString(), ShadowAtlas);
        var format = frame.FormatOf(ToString(), ShadowAtlas);

        // The tile arithmetic is the shadow node's own — same count, same resolution — so the two
        // passes' viewports cannot disagree; the extent against the declared texture is that
        // node's guard, which threw before this node ever built.
        var resolution = sun.Resolution;

        CascadeCount = count;

        frame.Graph.AddPass(
            ToString(),
            pass => {
                // Loaded, never cleared: the cascade pass's mesh casters are in this atlas and the
                // terrain merges with them through the depth test. Load-op actions hit the render
                // area, not the scissor — a clear here would wipe all four tiles at once.
                pass.DepthAttachment(atlas, LoadAction.Load);

                pass.Execute(
                    context => {
                        // Resolved now rather than at build, so the surface node's set sync for
                        // this frame — including a mode flip's rebuild — has already happened.
                        active.Clear();
                        placed.Clear();

                        foreach (var entry in scene.Terrains) {
                            if (!entry.CastShadows || !placed.Add(entry.Terrain)) {
                                continue;
                            }

                            if (surfaces.CasterFor(entry.Terrain, casterShaders, format) is not { } caster) {
                                continue;
                            }

                            // Host writes only — records, blocks, sets — so the caster pass needs
                            // no upload sibling and no barriers of its own.
                            caster.Upload(entry.Origin, matrices.AsSpan(0, count));
                            active.Add(caster);
                            CastersDrawn++;
                        }

                        for (var cascade = 0; cascade < count; cascade++) {
                            var viewport = ShadowCascades.TileViewport(cascade, count, resolution);

                            context.CommandList.SetViewport(viewport);

                            // The scissor is what makes one atlas safe — a patch crossing a tile
                            // edge would otherwise shadow a cascade it was never projected for.
                            context.CommandList.SetScissor(
                                new((int)viewport.X, (int)viewport.Y, (int)viewport.Width, (int)viewport.Height)
                            );

                            foreach (var caster in active) {
                                caster.Record(context.CommandList, cascade);
                            }
                        }
                    }
                );
            }
        );
    }

    /// <summary>The shadow node writing this atlas, wherever the frame's tree holds it.</summary>
    ShadowMapRenderer? FindSun(SceneRenderer? node) {
        if (node is ShadowMapRenderer sun && sun.Enabled && sun.Atlas == ShadowAtlas) {
            return sun;
        }

        if (node is null) {
            return null;
        }

        foreach (var child in node.Nested) {
            if (FindSun(child) is { } found) {
                return found;
            }
        }

        return null;
    }

    /// <summary>Resolves the caster's two stages, once, through the frame's effect system.</summary>
    bool ResolveShaders(CompositorFrame frame) {
        if (casterShaders.IsValid) {
            return true;
        }

        if (frame.Effects.Resolve(EffectKey.Of("TerrainCaster")) is not { } effect) {
            return false;
        }

        casterShaders = new(
            Modules!.ModuleOf(effect, ShaderStage.Vertex),
            Modules.ModuleOf(effect, ShaderStage.Fragment)
        );

        return casterShaders.IsValid;
    }

    /// <inheritdoc />
    public override string ToString() => string.IsNullOrEmpty(Name) ? "TerrainCasters" : Name;
}
