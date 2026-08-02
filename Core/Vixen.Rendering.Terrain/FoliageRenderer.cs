// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Rendering.Features;

namespace Vixen.Rendering.Terrain;

/// <summary>The draw templates one foliage type's levels are drawn with.</summary>
/// <param name="Type">Which palette entry.</param>
/// <param name="Levels">
///     One draw template per level, nearest first: index count, first index, vertex offset.
/// </param>
/// <param name="Distances">
///     Where each level takes over, ascending, one fewer than <paramref name="Levels" />.
/// </param>
/// <remarks>
///     ⚠ <b>The templates come from the mesh's LOD group and this assembly does not resolve them.</b>
///     A <c>.vxfoliage</c> names a mesh; turning that into index counts is the asset system's, and a
///     renderer that looked it up would need an asset database in a class whose job is a compute
///     dispatch.
/// </remarks>
public readonly record struct FoliageDraw(int Type, DrawCommand[] Levels, float[] Distances);

/// <summary>What one cell contributed to a frame.</summary>
/// <param name="Type">Which palette entry.</param>
/// <param name="Cell">Which cell.</param>
/// <param name="First">Where its survivors begin in the frame's compacted buffer.</param>
/// <param name="Count">How many survived.</param>
/// <param name="Commands">One indirect command per level, at that level's index.</param>
public readonly record struct FoliageBatch(
    int Type,
    FoliageCellKey Cell,
    int First,
    int Count,
    DrawCommand[] Commands
);

/// <summary>
///     Turning a volume of instances into indirect draws: cells culled as objects, instances culled
///     within them.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D9], both stages.</b> A foliage cell is what the instancing feature
///         sees as one object — tight bounds, one mesh, a few thousand of them for a forest — and is
///         culled by the existing frustum path. That is enough for trees and not enough for grass,
///         because a 32 m cell of grass is fifty thousand instances and the far half of it is behind a
///         hill. So a second pass over the surviving cells' instances tests each one, compacts the
///         survivors and fills a <c>DrawIndexedIndirect</c> command.
///     </para>
///     <para>
///         <b>The LOD decision is in that second pass, and it is a deliberate divergence from
///         <c>LodRenderFeature</c>.</b> That feature is right for its case — a LOD group is several
///         render objects and it clears bits — and it cannot express "these four thousand trees in
///         this cell are at level 1 and those six hundred are at level 2", because its level is per
///         object and here it is per instance. So the pass bins each instance into its level's own
///         indirect command and a cell draws three or four times instead of once.
///     </para>
///     <para>
///         ⚠ <b>This is the CPU half, and the compute shader is the other one.</b> The compaction,
///         the binning and the fade are <see cref="InstanceCuller" />'s, which was built in [§ B2]
///         precisely so that both halves could be the same arithmetic — the device form claims slots
///         with an atomic add and is therefore unordered, which a seam test sorts away rather than
///         asserts through. What runs here is what a headless test and a machine with no compute
///         queue use, and it is the reference the dispatch is checked against.
///     </para>
/// </remarks>
public sealed class FoliageRenderer {
    readonly InstanceCuller culler = new();
    readonly List<FoliageBatch> batches = [];
    readonly List<Matrix4x4> transforms = [];
    readonly List<InstanceParameters> parameters = [];
    readonly List<InstanceBounds> bounds = [];

    /// <summary>What each surviving cell drew, in the order they were gathered.</summary>
    public IReadOnlyList<FoliageBatch> Batches => batches;

    /// <summary>Every surviving instance's transform, compacted across every cell.</summary>
    /// <remarks>
    ///     One buffer for the whole frame rather than one per cell, because a per-cell upload is a
    ///     map and an unmap per cell — a few thousand of them — to move a few kilobytes each.
    /// </remarks>
    public ReadOnlySpan<Matrix4x4> Transforms => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(transforms);

    /// <summary>And their parameters, at the same indices.</summary>
    public ReadOnlySpan<InstanceParameters> Parameters =>
        System.Runtime.InteropServices.CollectionsMarshal.AsSpan(parameters);

    /// <summary>How many cells were tested.</summary>
    public int CellsConsidered { get; private set; }

    /// <summary>How many survived the first stage.</summary>
    public int CellsDrawn { get; private set; }

    /// <summary>How many instances those cells held.</summary>
    public int InstancesConsidered { get; private set; }

    /// <summary>And how many survived the second.</summary>
    public int InstancesDrawn { get; private set; }

    /// <summary>How many indirect draws the frame issues.</summary>
    /// <remarks>
    ///     ⚠ <b>Counting the empty ones, because they are issued.</b> A level with no survivors gets a
    ///     command with a zero instance count rather than being skipped — which is what lets a caller
    ///     bind level N's mesh at slot N instead of reading back which levels survived. A profiler
    ///     reading this number is reading what the queue was handed.
    /// </remarks>
    public int Draws { get; private set; }

    /// <summary>Culls a volume against a view and builds its indirect draws.</summary>
    /// <param name="volume">The instances.</param>
    /// <param name="draws">One entry per type that is drawn. Types not listed are skipped.</param>
    /// <param name="frustum">What the camera can see.</param>
    /// <param name="viewPosition">Where it is.</param>
    /// <param name="densityScale">How much of the foliage to draw, 0…1, for a scalability setting.</param>
    /// <returns>How many instances will be drawn.</returns>
    public int Cull(
        FoliageVolume volume,
        IReadOnlyList<FoliageDraw> draws,
        in BoundingFrustum frustum,
        Vector3 viewPosition,
        float densityScale = 1f
    ) {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(draws);

        batches.Clear();
        transforms.Clear();
        parameters.Clear();

        CellsConsidered = 0;
        CellsDrawn = 0;
        InstancesConsidered = 0;
        InstancesDrawn = 0;
        Draws = 0;

        var byType = new Dictionary<int, FoliageDraw>(draws.Count);

        foreach (var draw in draws) {
            byType[draw.Type] = draw;
        }

        var view = frustum;

        foreach (var chunk in volume.Chunks) {
            if (chunk.IsEmpty || !byType.TryGetValue(chunk.Type, out var draw)) {
                continue;
            }

            CellsConsidered++;

            // Stage one: the cell as one object. A forest is a few thousand of these and most of them
            // are behind the camera.
            if (!view.Intersects(chunk.Bounds)) {
                continue;
            }

            var settings = volume.Palette[chunk.Type];

            CellsDrawn++;
            InstancesConsidered += chunk.Count;

            Fill(chunk, settings);

            var culled = culler.Cull(
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(bounds),
                [],
                new InstanceCullSettings {
                    Frustum = view,
                    ViewPosition = viewPosition,
                    StartCullDistance = settings.StartCullDistance,
                    EndCullDistance = settings.EndCullDistance,
                    DensityScale = densityScale,
                    Fade = true
                },
                draw.Distances
            );

            if (culled == 0) {
                continue;
            }

            var first = transforms.Count;
            var survivors = culler.Survivors;
            var fades = culler.Parameters;

            for (var index = 0; index < culled; index++) {
                transforms.Add(chunk.Instances[(int)survivors[index]].Transform);
                parameters.Add(fades[index]);
            }

            var commands = new DrawCommand[culler.LevelCount];
            culler.FillCommands(draw.Levels, (uint)first, commands);

            batches.Add(new(chunk.Type, chunk.Cell, first, culled, commands));

            InstancesDrawn += culled;
            Draws += commands.Length;
        }

        return InstancesDrawn;
    }

    /// <summary>What a cell's instances are, as the culler wants them.</summary>
    /// <remarks>
    ///     ⚠ <b>The bounding radius is the type's spacing scaled by the instance</b>, for the reason
    ///     <c>FoliageChunk</c>'s bounds use it: this assembly has no mesh and cannot ask one how big
    ///     it is. A radius that is too small culls an instance while part of it is on screen, so the
    ///     approximation errs upwards.
    /// </remarks>
    void Fill(FoliageChunk chunk, in FoliageType settings) {
        bounds.Clear();

        var reach = MathF.Max(settings.Radius, 0.5f);

        foreach (var instance in chunk.Instances) {
            bounds.Add(new(instance.Position, reach * instance.Scale));
        }
    }
}
