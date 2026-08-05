// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering.Compositor;

namespace Vixen.Rendering.Terrain;

/// <summary>Builds the terrain node kinds a compositor document names.</summary>
/// <remarks>
///     <para>
///         <b><c>PostEffectFactory</c>'s arrangement, for the ground.</b> <c>CompositorBuilder</c>
///         cannot switch on a type downstream of it, so a document naming <c>!Terrain</c> needs
///         this registered on <c>GraphicsOptions.Factories</c> — in <c>Game.OnConfigure</c>,
///         because the compositor is built before <c>OnInitialise</c> runs. Registering it is the
///         whole installation: the host recognises the factory and wires <see cref="Scene" /> to
///         the world's extraction bridge itself.
///     </para>
///     <para>
///         <b>And the transformer that splices the caster.</b> A document holding a
///         <c>!Terrain</c> node and a <c>!ShadowMap</c> writing the atlas it samples gets a
///         <see cref="TerrainCasterRenderer" /> inserted directly after the shadow node — the one
///         place in the frame where the cascades are drawn and nothing has sampled them yet.
///         Registered after <c>PostEffectFactory</c>, necessarily: transformers run in
///         registration order, and this one has to see the expanded frame the
///         <c>!StandardFrame</c> transform produced, not the preset that stands for it.
///     </para>
///     <para>
///         ⚠ <b><see cref="Vegetation" /> is where a quality tier reaches the vegetation.</b> The
///         waterfall's asset lives in <c>Vixen.Rendering.PostFx</c>, which this project must not
///         reference, so what crosses is plain numbers: a host that resolved a tier fills them, a
///         document scalar out-votes them per field, and a host that fills nothing runs the
///         defaults. Folding the resolved tier in automatically is owed to the increment that
///         teaches <c>!StandardFrame</c> to emit this node.
///     </para>
/// </remarks>
public sealed class TerrainFactory : ISceneRendererFactory, ICompositorAssetTransformer {
    // The casters built this document walk, waiting for the terrain node that owns the draw sets.
    // Per build, because a build makes fresh nodes: Transform clears it, Create pairs from it, and
    // an entry left behind is a caster node no terrain node answered — which draws nothing quietly,
    // exactly as its own remarks promise.
    readonly List<TerrainCasterRenderer> unpaired = [];

    // And the surface nodes, waiting for the velocity node that follows them: the caster precedes
    // its terrain node in every document the transform produces and the velocity node follows it,
    // so the two pairings queue in opposite directions.
    readonly List<TerrainSceneRenderer> surfaces = [];
    /// <summary>Where the frame's terrains come from — the extraction bridge's list.</summary>
    /// <remarks>
    ///     Null builds nodes that draw nothing quietly, which is a document opened by a tool with
    ///     no world. <c>AppGraphics</c> assigns the world renderer's own source here when it finds
    ///     this factory in <c>GraphicsOptions.Factories</c>.
    /// </remarks>
    public TerrainSceneSource? Scene { get; set; }

    /// <summary>The vegetation numbers the host's tier resolved to, for nodes that do not say.</summary>
    public TerrainVegetationQuality Vegetation { get; set; } = new();

    /// <inheritdoc />
    /// <remarks>
    ///     Pure, as the contract demands: the same document in, the same document out — the
    ///     insertion is decided by what the document already says, and a document that holds a
    ///     caster node, no terrain node, or no matching shadow node passes through untouched.
    /// </remarks>
    public GraphicsCompositorAsset Transform(GraphicsCompositorAsset document, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(document);

        // A build makes fresh nodes, so a rebuild must not pair against the last build's.
        unpaired.Clear();
        surfaces.Clear();

        if (document.Game is not { } game) {
            return document;
        }

        var spliced = SpliceVelocity(SpliceCasters(game));

        return ReferenceEquals(spliced, game) ? document : document with { Game = spliced };
    }

    /// <summary>Inserts a caster node after the shadow node whose atlas a terrain node samples.</summary>
    /// <remarks>
    ///     Within one children list, because that is where the standard frame's expansion puts both
    ///     — and where any hand-authored frame that wants the splice puts them too, since a caster
    ///     in a different sequence from its atlas's writer would have no defined order against it.
    ///     Sequences are walked recursively so a nested frame still qualifies.
    /// </remarks>
    static ISceneRendererAsset SpliceCasters(ISceneRendererAsset node) {
        if (node is not SequenceAsset sequence) {
            return node;
        }

        var children = sequence.Children;
        ISceneRendererAsset[]? replaced = null;

        for (var index = 0; index < children.Length; index++) {
            var child = SpliceCasters(children[index]);

            if (!ReferenceEquals(child, children[index])) {
                replaced ??= [.. children];
                replaced[index] = child;
            }
        }

        var current = replaced ?? children;
        var terrain = default(TerrainNodeAsset);
        var already = false;

        foreach (var child in current) {
            terrain ??= child as TerrainNodeAsset;
            already |= child is TerrainCasterNodeAsset;
        }

        if (terrain is null || already) {
            return replaced is null ? node : sequence with { Children = replaced };
        }

        for (var index = 0; index < current.Length; index++) {
            if (current[index] is not ShadowMapAsset shadows || shadows.Atlas != terrain.ShadowAtlas) {
                continue;
            }

            // Directly after the cascades and before anything that samples them — position is the
            // node's whole reason to exist, so it is decided here, once, by the transform.
            var caster = new TerrainCasterNodeAsset {
                Name = $"{terrain.Name}.Casters",
                Enabled = terrain.Enabled,
                ShadowAtlas = terrain.ShadowAtlas,
                ScenePass = terrain.ScenePass
            };

            return sequence with {
                Children = [.. current[..(index + 1)], caster, .. current[(index + 1)..]]
            };
        }

        return replaced is null ? node : sequence with { Children = replaced };
    }

    /// <summary>Inserts a velocity node after the render pass whose Motion stage the frame draws.</summary>
    /// <remarks>
    ///     <see cref="SpliceCasters" />' walk, aimed at the other end of the frame: the frame's
    ///     velocity pass — recognised by the <c>Motion</c> stage it draws, which is
    ///     <c>!StandardFrame</c>'s contract for "the pass TAA's motion vectors land in" — clears the
    ///     motion plane, so the ground's reprojection must be a node <em>after</em> it, however
    ///     early the terrain node itself sits. A frame with no such pass — no TAA — gets no node
    ///     and pays nothing.
    /// </remarks>
    static ISceneRendererAsset SpliceVelocity(ISceneRendererAsset node) {
        if (node is not SequenceAsset sequence) {
            return node;
        }

        var children = sequence.Children;
        ISceneRendererAsset[]? replaced = null;

        for (var index = 0; index < children.Length; index++) {
            var child = SpliceVelocity(children[index]);

            if (!ReferenceEquals(child, children[index])) {
                replaced ??= [.. children];
                replaced[index] = child;
            }
        }

        var current = replaced ?? children;
        var terrain = default(TerrainNodeAsset);
        var already = false;

        foreach (var child in current) {
            terrain ??= child as TerrainNodeAsset;
            already |= child is TerrainVelocityNodeAsset;
        }

        if (terrain is null || already) {
            return replaced is null ? node : sequence with { Children = replaced };
        }

        for (var index = 0; index < current.Length; index++) {
            if (current[index] is not RenderPassAsset pass
                || pass.ColourTargets.Length == 0
                || !DrawsMotionStage(pass)) {
                continue;
            }

            // Directly after the frame's own motion vectors and before anything that resolves
            // them — position is the node's whole reason to exist, the caster splice's argument.
            // The names are the pass's rather than the terrain node's, because the plane the
            // ground must join is whichever one the frame's velocity pass cleared.
            var velocity = new TerrainVelocityNodeAsset {
                Name = $"{terrain.Name}.Velocity",
                Enabled = terrain.Enabled,
                Motion = pass.ColourTargets[0],
                Depth = pass.DepthTarget ?? terrain.Depth
            };

            return sequence with {
                Children = [.. current[..(index + 1)], velocity, .. current[(index + 1)..]]
            };
        }

        return replaced is null ? node : sequence with { Children = replaced };
    }

    /// <summary>Whether a pass draws the frame's <c>Motion</c> stage — the velocity pass's signature.</summary>
    static bool DrawsMotionStage(RenderPassAsset pass) {
        foreach (var child in pass.Children) {
            if (child is SingleStageAsset { Stage: "Motion" }) {
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc />
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        if (declared is TerrainCasterNodeAsset casters) {
            var node = new TerrainCasterRenderer {
                Name = casters.Name,
                Enabled = casters.Enabled,
                ShadowAtlas = casters.ShadowAtlas,
                ScenePass = casters.ScenePass,
                Scene = Scene,
                Device = builder.Device,
                Modules = builder.Modules
            };

            // The caster precedes its terrain node in every document the transform produces, so it
            // waits here for the node that owns the draw sets it will borrow.
            unpaired.Add(node);

            return node;
        }

        if (declared is TerrainVelocityNodeAsset velocity) {
            var node = new TerrainVelocityRenderer {
                Name = velocity.Name,
                Enabled = velocity.Enabled,
                Motion = velocity.Motion,
                Depth = velocity.Depth,
                Device = builder.Device
            };

            // The velocity node follows its terrain node in every document the transform produces,
            // so the surface it borrows from is already built — matched by the depth name, because
            // "tests the depth the ground wrote" is the contract the two halves share.
            for (var index = 0; index < surfaces.Count; index++) {
                if (surfaces[index].Depth != velocity.Depth) {
                    continue;
                }

                node.Surfaces = surfaces[index];
                surfaces[index].VelocitySibling = node;
                surfaces.RemoveAt(index);

                break;
            }

            return node;
        }

        if (declared is not TerrainNodeAsset terrain) {
            return null;
        }

        // Bound loudly rather than left null: a terrain drawn from a view nothing filled would
        // stream, cull and place the ground around a camera at the origin — a plausible picture of
        // the wrong place, which is worse than a refusal naming the view.
        if (!builder.Views.TryGetValue(terrain.View, out var view)) {
            throw new CompositorBindingException(terrain.Name, "view", terrain.View);
        }

        var ground = new TerrainSceneRenderer {
            Name = terrain.Name,
            Enabled = terrain.Enabled,
            Output = terrain.Output,
            Depth = terrain.Depth,
            Albedo = terrain.Albedo,
            Normals = terrain.Normals,
            ShadowAtlas = terrain.ShadowAtlas,
            ScenePass = terrain.ScenePass,
            View = view,
            Grass = terrain.Grass,
            Scene = Scene,

            // The frame's set-0 state — the same instance every shading pass binds through, whose
            // parameters carry the published cascades and cluster buffers and whose SceneLighting
            // carries the sun and the camera. Null in a builder with no frame wired, which is the
            // preview.
            Frame = builder.SceneConstants,
            Vegetation = Vegetation with {
                GrassDensityScale = terrain.GrassDensityScale ?? Vegetation.GrassDensityScale,
                GrassCullDistanceScale = terrain.GrassCullDistanceScale ?? Vegetation.GrassCullDistanceScale,
                GrassResidentCells = terrain.GrassResidentCells ?? Vegetation.GrassResidentCells,
                GrassBladesPerCell = terrain.GrassBladesPerCell ?? Vegetation.GrassBladesPerCell,
                TerrainNearRange = terrain.TerrainNearRange ?? Vegetation.TerrainNearRange
            },
            Device = builder.Device,
            Modules = builder.Modules
        };

        // The caster node built earlier in this walk gets the surface node whose draw sets it
        // borrows — matched by the atlas name, because that is the contract the two halves share.
        for (var index = 0; index < unpaired.Count; index++) {
            if (unpaired[index].ShadowAtlas != terrain.ShadowAtlas) {
                continue;
            }

            unpaired[index].Surfaces = ground;
            unpaired.RemoveAt(index);

            break;
        }

        // And the surface waits here for the velocity node that follows it, the mirror image of
        // the caster's queue above.
        surfaces.Add(ground);

        return ground;
    }
}
