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
///         ⚠ <b><see cref="Vegetation" /> is where a quality tier reaches the vegetation.</b> The
///         waterfall's asset lives in <c>Vixen.Rendering.PostFx</c>, which this project must not
///         reference, so what crosses is plain numbers: a host that resolved a tier fills them, a
///         document scalar out-votes them per field, and a host that fills nothing runs the
///         defaults. Folding the resolved tier in automatically is owed to the increment that
///         teaches <c>!StandardFrame</c> to emit this node.
///     </para>
/// </remarks>
public sealed class TerrainFactory : ISceneRendererFactory {
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
    public SceneRenderer? Create(ISceneRendererAsset declared, CompositorBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        if (declared is not TerrainNodeAsset terrain) {
            return null;
        }

        // Bound loudly rather than left null: a terrain drawn from a view nothing filled would
        // stream, cull and place the ground around a camera at the origin — a plausible picture of
        // the wrong place, which is worse than a refusal naming the view.
        if (!builder.Views.TryGetValue(terrain.View, out var view)) {
            throw new CompositorBindingException(terrain.Name, "view", terrain.View);
        }

        return new TerrainSceneRenderer {
            Name = terrain.Name,
            Enabled = terrain.Enabled,
            Output = terrain.Output,
            Depth = terrain.Depth,
            View = view,
            Grass = terrain.Grass,
            Scene = Scene,
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
    }
}
