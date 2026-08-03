// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Vixen.Terrain;

using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Rendering.Terrain;
/// <summary>
///     A heightfield answering the question a scatter asks.
/// </summary>
/// <remarks>
///     <para>
///         <b>The adapter [docs/plan/31 § D1] leaves out of both kernels on purpose.</b>
///         <c>Vixen.Foliage</c> cannot name a terrain — a roof of moss would then depend on a
///         heightfield — and <c>Vixen.Terrain</c> has never heard of a blade of grass. So the join
///         lives in the one assembly that already references both, and it is thirty lines rather than
///         an assembly of its own.
///     </para>
///     <para>
///         ⚠ <b>Outside the terrain is <see cref="FoliageSurface.Missed" />, and the explicit bounds
///         test is not redundant.</b> <c>TerrainPick.HeightAt</c> clamps rather than refusing, which
///         is right for a brush that has to aim somewhere; used unguarded here it would answer for
///         every position in the world, so a field would stretch to the horizon at the height of the
///         terrain's border.
///     </para>
///     <para>
///         ⚠ <b>A layer whose name is not in the stack answers a weight of zero.</b> Nothing grows,
///         which is a field somebody notices and fixes; answering one instead would carpet a whole
///         terrain because of a typo, and would do it on the same day the layer was renamed.
///     </para>
/// </remarks>
public sealed class TerrainSurface : IFoliageSurface {
    readonly TerrainMap terrain;
    readonly Dictionary<string, int> byName = new(StringComparer.Ordinal);

    /// <summary>Wraps a terrain.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="origin">Where its low corner is in world space.</param>
    /// <exception cref="ArgumentNullException">There is no terrain.</exception>
    public TerrainSurface(TerrainMap terrain, Vector3 origin = default) {
        ArgumentNullException.ThrowIfNull(terrain);

        this.terrain = terrain;
        Origin = origin;

        Rebind();
    }

    /// <summary>Where the terrain's low corner is in world space.</summary>
    public Vector3 Origin { get; }

    /// <summary>The terrain being answered for.</summary>
    /// <remarks>
    ///     ⚠ <b>Exposed so a host can tell "the same ground" from "a surface built over it".</b> The
    ///     layer-name cache is rebuilt by the constructor, so an editor that made a new surface every
    ///     frame would rebuild it every frame — and the only way to avoid that is to be able to ask
    ///     whether the one it is holding is still about this terrain.
    /// </remarks>
    public TerrainMap Terrain => terrain;

    /// <summary>Re-reads the layer names.</summary>
    /// <remarks>
    ///     Called by the constructor and by whatever adds or removes a layer. A cache rather than a
    ///     lookup per candidate, because a cell of grass is a few thousand of them and the name is
    ///     the same for every one.
    /// </remarks>
    public void Rebind() {
        byName.Clear();

        var names = terrain.Weights.Names;

        for (var layer = 0; layer < names.Count; layer++) {
            byName[names[layer]] = layer;
        }
    }

    /// <inheritdoc />
    public FoliageSurface SampleAt(Vector2 position, string layer) {
        var description = terrain.Description;
        var local = new Vector2(position.X - Origin.X, position.Y - Origin.Z);

        if (local.X < 0f || local.Y < 0f || local.X > description.WidthX || local.Y > description.WidthZ) {
            return FoliageSurface.Missed;
        }

        var scale = description.MetresPerQuad;
        var quadX = (int)MathF.Floor(local.X / scale);
        var quadZ = (int)MathF.Floor(local.Y / scale);

        // A hole is not ground. The quad is what the renderer drops, so it is what a scatter has to
        // agree with — a blade standing in a cave mouth is the artefact holes exist to remove.
        if (!terrain.Holes.IsEmpty && terrain.Holes.IsQuadMissing(quadX, quadZ)) {
            return FoliageSurface.Missed;
        }

        var height = TerrainPick.HeightAt(terrain, local.X, local.Y);

        return new(
            new(position.X, height + Origin.Y, position.Y),
            NormalAt(local, scale),
            WeightAt(local, scale, layer),
            true
        );
    }

    /// <summary>The surface normal, by central difference over one quad.</summary>
    /// <remarks>
    ///     ⚠ <b>The world-space run is in the middle term, and leaving it out is what makes a normal
    ///     that changes with the resolution rather than with the ground.</b> It is the same expression
    ///     <c>Terrain.rvn</c>'s <c>Normal</c> uses, so a slope rejection here and the shading there
    ///     are reading the same surface.
    /// </remarks>
    Vector3 NormalAt(Vector2 local, float scale) {
        var left = TerrainPick.HeightAt(terrain, local.X - scale, local.Y);
        var right = TerrainPick.HeightAt(terrain, local.X + scale, local.Y);
        var down = TerrainPick.HeightAt(terrain, local.X, local.Y - scale);
        var up = TerrainPick.HeightAt(terrain, local.X, local.Y + scale);

        var normal = new Vector3(left - right, 2f * scale, down - up);

        return normal.IsZero ? Vector3.UnitY : Vector3.Normalize(normal);
    }

    /// <summary>How much of a named layer is painted at a position, bilinearly.</summary>
    /// <remarks>
    ///     ⚠ <b>Bilinear rather than nearest, because the shader that draws the ground is.</b> A
    ///     nearest-sampled density curve puts a visible staircase along the foot of every field, on
    ///     the sample grid, which is a pattern with a period no artist painted.
    /// </remarks>
    float WeightAt(Vector2 local, float scale, string layer) {
        if (string.IsNullOrEmpty(layer)) {
            return 1f;
        }

        if (!byName.TryGetValue(layer, out var index)) {
            return 0f;
        }

        var description = terrain.Description;
        var sampleX = Math.Clamp(local.X / scale, 0f, description.SamplesX - 1f);
        var sampleZ = Math.Clamp(local.Y / scale, 0f, description.SamplesZ - 1f);

        var x0 = (int)MathF.Floor(sampleX);
        var z0 = (int)MathF.Floor(sampleZ);
        var x1 = Math.Min(x0 + 1, description.SamplesX - 1);
        var z1 = Math.Min(z0 + 1, description.SamplesZ - 1);

        var fx = sampleX - x0;
        var fz = sampleZ - z0;

        var weights = terrain.Weights;
        var top = float.Lerp(weights.FractionAt(index, x0, z0), weights.FractionAt(index, x1, z0), fx);
        var bottom = float.Lerp(weights.FractionAt(index, x0, z1), weights.FractionAt(index, x1, z1), fx);

        return float.Lerp(top, bottom, fz);
    }
}
