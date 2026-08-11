// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Rendering.Terrain;

/// <summary>Turns the scene's <see cref="TerrainComponent" /> entities into the frame's terrain list.</summary>
/// <remarks>
///     <para>
///         <b><c>LightExtractionSystem</c>'s shape, for the ground.</b> The component holds names
///         and numbers; the frame needs heightfields and placements; this is the walk between them,
///         cleared and refilled every frame so a destroyed entity takes its landscape with it. The
///         consumer is whatever compositor node holds the same <see cref="TerrainSceneSource" /> —
///         wired through <c>TerrainFactory</c>, because the node is rebuilt per document load and
///         this system is not.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, ordered by its declared access.</b>
///         <c>TransformSystem</c> writes <see cref="WorldTransform" /> in the same phase and this
///         reads it, so a terrain that moved this frame is drawn where it moved to.
///     </para>
///     <para>
///         ⚠ <b>A reference that has not resolved yet is left out, quietly; one that resolved to a
///         broken rule is left out loudly.</b> "Loading" is a state every level starts in and lasts
///         a few frames; a <see cref="Vixen.Foliage.GrassType" /> whose own <c>Validate</c> refuses
///         it would otherwise reach the scatter dispatch, which throws mid-frame — so it is dropped
///         here and counted in <see cref="RefusedGrass" />, where a person can read it beside the
///         asset that caused it.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class TerrainExtractionSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription terrains = new QueryDescription().WithAll<TerrainComponent, WorldTransform>();
    readonly QueryDescription foliage = new QueryDescription().WithAll<FoliageVolumeComponent, WorldTransform>();
    readonly TerrainSceneSource scene;
    readonly List<Vixen.Foliage.FoliageType> palette = [];

    /// <summary>Builds the bridge over the frame list it fills.</summary>
    /// <param name="scene">Where each frame's terrains land.</param>
    /// <exception cref="ArgumentNullException"><paramref name="scene" /> is null.</exception>
    public TerrainExtractionSystem(TerrainSceneSource scene) {
        ArgumentNullException.ThrowIfNull(scene);
        this.scene = scene;
    }

    /// <summary>What turns a component's reference into a heightfield, or null while nothing can.</summary>
    /// <remarks>
    ///     Null is a world whose terrains never resolve — every entity waits in
    ///     <see cref="Waiting" /> — which is what a host that has not mounted content yet is.
    /// </remarks>
    public ITerrainAssetSource? Assets { get; set; }

    /// <summary>How many terrains the last pass put in the list.</summary>
    public int TerrainCount { get; private set; }

    /// <summary>How many entities named a terrain or foliage asset that has not resolved.</summary>
    /// <remarks>
    ///     A number that stays up is a reference nothing can load — "the ground is missing" and
    ///     "the ground has not arrived yet" are different problems, and only the second fixes
    ///     itself.
    /// </remarks>
    public int Waiting { get; private set; }

    /// <summary>How many grass rules were dropped because their own validation refused them.</summary>
    public int RefusedGrass { get; private set; }

    /// <summary>How many foliage volumes the last pass put in the list.</summary>
    public int FoliageCount { get; private set; }

    /// <summary>How many foliage volumes were dropped because a palette type refused itself.</summary>
    /// <remarks>
    ///     The whole volume rather than the one type, deliberately: the palette's <em>order</em> is
    ///     what the instances index, so dropping entry two would dress every later chunk in the
    ///     wrong species — worse than drawing nothing with a counter saying why.
    /// </remarks>
    public int RefusedFoliage { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared here rather than with attributes, for the reason <c>LightExtractionSystem</c>
    ///     gives: naming a component type in a generic call is what assigns it an id, and an
    ///     attribute can only look one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<TerrainComponent>()
        .Read<TerrainGrassComponent>()
        .Read<FoliageVolumeComponent>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // ⚠ Here and not in the node, because this is the only end that has a clock the host can
        // steer. See `TerrainSceneSource.Time`: the node's own stopwatch made grass sway a function
        // of process age, which no `--vixen-fixed-step` could reach.
        scene.Time = (float)context.Time.TotalSeconds;

        Extract(context.World);
        return dependency;
    }

    /// <summary>Refills the frame list from the world.</summary>
    /// <param name="world">The world.</param>
    /// <remarks>Public so a test or a tool can place ground without standing up a runner.</remarks>
    public void Extract(World world) {
        ArgumentNullException.ThrowIfNull(world);

        scene.Terrains.Clear();
        scene.Foliage.Clear();

        TerrainCount = 0;
        FoliageCount = 0;
        Waiting = 0;
        RefusedGrass = 0;
        RefusedFoliage = 0;

        if (Assets is not { } assets) {
            return;
        }

        foreach (var chunk in world.Chunks(terrains)) {
            var transforms = chunk.ReadValues<WorldTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                // One entity at a time rather than a column: a component holding a string is a
                // managed component, so the chunk holds handles and there is no span to read. A
                // scene has a handful of terrains, so the indirection costs nothing that matters.
                var authored = world.Get<TerrainComponent>(entities[index]);

                if (authored.Terrain is not { Length: > 0 } reference) {
                    continue;
                }

                if (assets.Terrain(reference) is not { } map) {
                    Waiting++;
                    continue;
                }

                var grass = default(Vixen.Foliage.GrassType?);
                var grassRange = 0f;

                // The grass rides its own component, so it is read per entity rather than as a
                // column — a chunk is keyed by the archetype and a terrain with grass and one
                // without sit in different chunks anyway.
                if (world.Has<TerrainGrassComponent>(entities[index])) {
                    var field = world.Get<TerrainGrassComponent>(entities[index]);

                    if (field.Grass is { Length: > 0 } rule && assets.Grass(rule) is { } type) {
                        // Refused here rather than thrown from the scatter's Prepare mid-frame:
                        // the dispatch is right to throw — nothing below it can explain a field
                        // that grows no blade — and a frame is the wrong place to find out.
                        if (type.Validate() is null) {
                            grass = type;
                            grassRange = field.Range;
                        } else {
                            RefusedGrass++;
                        }
                    }
                }

                scene.Terrains.Add(
                    new(
                        map,
                        transforms[index].Value.Translation,
                        authored.NearRange,
                        authored.LodBias,
                        authored.CastShadows,
                        grass,
                        grassRange
                    )
                );

                TerrainCount++;
            }
        }

        ExtractFoliage(world, assets);
    }

    /// <summary>Refills the foliage half of the frame list: the palette first, then the volume.</summary>
    /// <remarks>
    ///     ⚠ <b>Every palette type has to resolve before the volume can be asked for at all</b> —
    ///     <see cref="ITerrainAssetSource.Volume" />'s remarks say why the bytes cannot be read
    ///     without them. A palette part-way through loading is <see cref="Waiting" />; one whose
    ///     type refuses itself is <see cref="RefusedFoliage" />, dropped whole for the order's sake.
    /// </remarks>
    void ExtractFoliage(World world, ITerrainAssetSource assets) {
        foreach (var chunk in world.Chunks(foliage)) {
            var transforms = chunk.ReadValues<WorldTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var authored = world.Get<FoliageVolumeComponent>(entities[index]);

                if (authored.Volume is not { Length: > 0 } reference
                    || authored.Palette is not { Length: > 0 } names) {
                    continue;
                }

                palette.Clear();

                var waiting = false;
                var refused = false;

                foreach (var name in names) {
                    if (assets.Foliage(name) is not { } type) {
                        waiting = true;
                        break;
                    }

                    // Refused here rather than thrown from the cull's Upload mid-frame — the grass
                    // path's own trade, made at the same seam for the same reason.
                    if (type.Validate() is not null) {
                        refused = true;
                        break;
                    }

                    palette.Add(type);
                }

                if (refused) {
                    RefusedFoliage++;
                    continue;
                }

                if (waiting) {
                    Waiting++;
                    continue;
                }

                if (assets.Volume(reference, palette) is not { } volume) {
                    Waiting++;
                    continue;
                }

                scene.Foliage.Add(new(volume, transforms[index].Value.Translation, authored.Range));
                FoliageCount++;
            }
        }
    }
}
