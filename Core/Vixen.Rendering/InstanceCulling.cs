// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Features;

namespace Vixen.Rendering;

/// <summary>Where one instance is and how big it is, in world space.</summary>
/// <param name="Centre">Its centre.</param>
/// <param name="Radius">A sphere around it that nothing it draws escapes.</param>
public readonly record struct InstanceBounds(Vector3 Centre, float Radius);

/// <summary>What a view asks of a cell's instances.</summary>
/// <remarks>
///     ⚠ <b>Nothing in here defaults usefully, and a zeroed one culls everything.</b> That is the
///     right way round — see <see cref="Everything" /> for the value that means "keep them all". A
///     zero <see cref="EndCullDistance" /> reading as "no limit" would make a forgotten field into an
///     unbounded draw, which is the expensive direction to be wrong in.
/// </remarks>
public readonly record struct InstanceCullSettings {
    /// <summary>The view's frustum. Instances outside it are dropped.</summary>
    public BoundingFrustum Frustum { get; init; }

    /// <summary>Where the view is, which the cull distances are measured from.</summary>
    public Vector3 ViewPosition { get; init; }

    /// <summary>How far out instances begin to fade. Beyond it they are still drawn.</summary>
    public float StartCullDistance { get; init; }

    /// <summary>How far out instances are gone entirely.</summary>
    public float EndCullDistance { get; init; }

    /// <summary>
    ///     What fraction of the instances to keep, 0…1, for a scalability setting.
    /// </summary>
    /// <remarks>
    ///     <b>Applied as a stable hash of the instance index, not as a prefix or a stride.</b>
    ///     Dropping the last tenth would clear one corner of a cell; dropping every tenth would leave
    ///     a visible lattice wherever the placement was itself regular. A hash keeps whichever
    ///     instances it keeps as the scale moves, so lowering the setting thins the field rather than
    ///     rearranging it — which is what stops the quality slider from looking like a different level.
    /// </remarks>
    public float DensityScale { get; init; }

    /// <summary>Whether to fade instances in over the band before they are culled.</summary>
    public bool Fade { get; init; }

    /// <summary>Keeps everything a frustum contains, with no distance limit and no thinning.</summary>
    /// <param name="frustum">The view's frustum.</param>
    /// <param name="viewPosition">Where the view is.</param>
    /// <returns>The settings.</returns>
    public static InstanceCullSettings Everything(BoundingFrustum frustum, Vector3 viewPosition) =>
        new() {
            Frustum = frustum,
            ViewPosition = viewPosition,
            StartCullDistance = float.MaxValue,
            EndCullDistance = float.MaxValue,
            DensityScale = 1f,
            Fade = false
        };
}

/// <summary>Where one level's surviving instances are in the compacted list.</summary>
/// <param name="First">The index of its first survivor.</param>
/// <param name="Count">How many survived at this level.</param>
public readonly record struct InstanceLodRun(int First, int Count);

/// <summary>
///     Culls the instances of a batch one at a time, and compacts the survivors.
/// </summary>
/// <remarks>
///     <para>
///         <b>The second stage [docs/plan/31 § B2] names.</b>
///         <see cref="Features.InstancingRenderFeature" />'s own remarks say a batch is culled as one
///         object — "a forest drawn as one object with ten thousand transforms is culled as one
///         object, so its bounds have to enclose the whole forest" — and answer it with "batching by
///         locality is the caller's decision because only the caller knows the scene's shape". A
///         foliage cell is that caller and it does know: it is a grid. This is what happens after the
///         cell survives, and it is why a 32 m cell of grass whose far half is behind a hill does not
///         draw its far half.
///     </para>
///     <para>
///         <b>Per instance, which <see cref="Features.LodRenderFeature" /> structurally cannot be.</b>
///         That feature is right for its case — a LOD group is several render objects and it clears
///         the bits of the ones a view is not showing — and the level it picks is a property of the
///         object. Here the level is a property of the <em>instance</em>: four thousand trees in a
///         cell are at level 1 and six hundred of them are at level 2, and no amount of clearing
///         object bits expresses that. So the survivors are binned into a run per level and the cell
///         draws once per level rather than once. See [docs/plan/31 § D9].
///     </para>
///     <para>
///         <b>A CPU reference, first and deliberately.</b> [docs/plan/22 § improvement 4] — "a CPU
///         reference for the parts that fail silently" — and the whole of <see cref="GpuCulling" /> is
///         the same shape: arithmetic that a device will run, written where a test can run it without
///         a device in the room. A per-instance cull fails silently in both directions (too few and
///         the forest has holes; too many and nothing looks wrong at all, it is merely slow), so the
///         definition wants to exist before the dispatch that mirrors it.
///     </para>
///     <para>
///         ⚠ <b>The dispatch does not exist yet.</b> What is here is the definition and the
///         compaction, not a compute shader — see the class remarks in <see cref="GpuCulling" /> for
///         the pairing this is one half of, and [docs/plan/31 § T5] for where the other half lands.
///     </para>
/// </remarks>
public sealed class InstanceCuller {
    uint[] survivors = [];
    InstanceParameters[] parameters = [];
    InstanceLodRun[] runs = [];
    int[] levels = [];

    /// <summary>How many instances survived the last cull, across every level.</summary>
    public int SurvivorCount { get; private set; }

    /// <summary>How many levels the last cull binned into.</summary>
    public int LevelCount { get; private set; }

    /// <summary>
    ///     The survivors' indices into the batch, grouped by level and ascending within each group.
    /// </summary>
    /// <remarks>
    ///     Ascending within a group because a draw reading them in order walks the transform buffer
    ///     forwards, and because two runs of the same cull must be comparable to be testable. The
    ///     device form of this claims slots with an atomic add and is therefore <em>not</em> ordered,
    ///     which is a difference a seam test has to sort away rather than assert through.
    /// </remarks>
    public ReadOnlySpan<uint> Survivors => survivors.AsSpan(0, SurvivorCount);

    /// <summary>The survivors' parameters, at the same indices as <see cref="Survivors" />.</summary>
    /// <remarks>
    ///     Rewritten rather than passed through, because <see cref="InstanceParameters.Fade" /> is
    ///     decided here: an instance's distance is what this pass measured, and measuring it again
    ///     downstream would be the same arithmetic in a second place.
    /// </remarks>
    public ReadOnlySpan<InstanceParameters> Parameters => parameters.AsSpan(0, SurvivorCount);

    /// <summary>Where each level's survivors are in <see cref="Survivors" />.</summary>
    public ReadOnlySpan<InstanceLodRun> Runs => runs.AsSpan(0, LevelCount);

    /// <summary>Culls a batch and compacts what is left.</summary>
    /// <param name="instances">Where each instance is.</param>
    /// <param name="source">
    ///     Each instance's authored parameters, or empty to derive them from
    ///     <see cref="InstanceParameters.Neutral" />.
    /// </param>
    /// <param name="settings">What the view asks.</param>
    /// <param name="lodDistances">
    ///     The distance at which each level takes over, ascending. An empty span is one level. The
    ///     last level runs to <see cref="InstanceCullSettings.EndCullDistance" />.
    /// </param>
    /// <returns>How many instances survived.</returns>
    /// <exception cref="ArgumentException">
    ///     The parameters do not match the instances, or the distances are not ascending.
    /// </exception>
    public int Cull(
        ReadOnlySpan<InstanceBounds> instances,
        ReadOnlySpan<InstanceParameters> source,
        in InstanceCullSettings settings,
        ReadOnlySpan<float> lodDistances
    ) {
        if (source.Length != 0 && source.Length != instances.Length) {
            throw new ArgumentException(
                $"{instances.Length} instances need {instances.Length} parameters or none, "
                + $"not {source.Length}.",
                nameof(source)
            );
        }

        for (var level = 1; level < lodDistances.Length; level++) {
            if (lodDistances[level] < lodDistances[level - 1]) {
                throw new ArgumentException(
                    $"LOD distances must ascend; level {level} takes over at {lodDistances[level]}, "
                    + $"which is nearer than level {level - 1}'s {lodDistances[level - 1]}.",
                    nameof(lodDistances)
                );
            }
        }

        LevelCount = lodDistances.Length + 1;
        SurvivorCount = 0;

        Grow(instances.Length, LevelCount);

        // Two passes, counting then placing, so each level's survivors are contiguous and ascending
        // without a sort. One pass into per-level lists would allocate per level per cell per frame,
        // which for a forest is the allocation that dominates everything this saves.
        Array.Clear(levels, 0, LevelCount);

        var end = settings.EndCullDistance;
        var start = MathF.Min(settings.StartCullDistance, end);
        var density = Math.Clamp(settings.DensityScale, 0f, 1f);

        for (var index = 0; index < instances.Length; index++) {
            var level = LevelOf(instances[index], in settings, lodDistances, density, end, out _);

            if (level >= 0) {
                levels[level]++;
            }
        }

        var offset = 0;

        for (var level = 0; level < LevelCount; level++) {
            runs[level] = new(offset, levels[level]);
            offset += levels[level];
            levels[level] = runs[level].First;
        }

        SurvivorCount = offset;

        for (var index = 0; index < instances.Length; index++) {
            var level = LevelOf(instances[index], in settings, lodDistances, density, end, out var distance);

            if (level < 0) {
                continue;
            }

            var slot = levels[level]++;
            survivors[slot] = (uint)index;

            var authored = source.Length != 0 ? source[index] : InstanceParameters.Neutral;

            authored.Fade = settings.Fade && end > start
                ? Math.Clamp((end - distance) / (end - start), 0f, 1f)
                : 1f;

            parameters[slot] = authored;
        }

        return SurvivorCount;
    }

    /// <summary>Fills one indirect draw command per level from the last cull.</summary>
    /// <param name="mesh">The draw template: index count, first index and vertex offset per level.</param>
    /// <param name="firstInstance">
    ///     Where this batch's compacted run begins in whatever buffer the survivors were copied into.
    /// </param>
    /// <param name="commands">One command per level. Must be at least <see cref="LevelCount" /> long.</param>
    /// <exception cref="ArgumentException">There is no room for a command per level.</exception>
    /// <remarks>
    ///     A level with no survivors gets a command with a zero instance count rather than being
    ///     skipped, so the command at slot N is always level N's — which is what lets a caller bind a
    ///     level's own mesh and material by index instead of reading back which levels survived.
    /// </remarks>
    public void FillCommands(ReadOnlySpan<DrawCommand> mesh, uint firstInstance, Span<DrawCommand> commands) {
        if (mesh.Length < LevelCount) {
            throw new ArgumentException(
                $"{LevelCount} levels need {LevelCount} draw templates, not {mesh.Length}.",
                nameof(mesh)
            );
        }

        if (commands.Length < LevelCount) {
            throw new ArgumentException(
                $"{LevelCount} levels need room for {LevelCount} commands, not {commands.Length}.",
                nameof(commands)
            );
        }

        for (var level = 0; level < LevelCount; level++) {
            var run = runs[level];

            commands[level] = mesh[level] with {
                InstanceCount = (uint)run.Count,
                FirstInstance = firstInstance + (uint)run.First
            };
        }
    }

    /// <summary>Which level an instance is at, or −1 if it is culled.</summary>
    static int LevelOf(
        in InstanceBounds bounds,
        in InstanceCullSettings settings,
        ReadOnlySpan<float> lodDistances,
        float density,
        float end,
        out float distance
    ) {
        distance = Vector3.Distance(bounds.Centre, settings.ViewPosition);

        // Distance first: it is one subtract and a length against six plane tests, and for a cell
        // whose far half is out of range it rejects most of the work before the frustum sees it.
        // The radius is subtracted so an instance whose centre is just past the limit but whose
        // canopy is not does not blink out.
        if (distance - bounds.Radius >= end) {
            return -1;
        }

        if (density < 1f && !Keep(bounds, density)) {
            return -1;
        }

        if (!settings.Frustum.Intersects(new BoundingSphere(bounds.Centre, bounds.Radius))) {
            return -1;
        }

        var level = 0;

        while (level < lodDistances.Length && distance >= lodDistances[level]) {
            level++;
        }

        return level;
    }

    /// <summary>Whether a density scale keeps this instance.</summary>
    /// <remarks>
    ///     Hashed from the quantised position rather than from the loop index, so an instance keeps
    ///     its verdict when the cell is re-scattered, re-ordered or streamed back in. Hashing the
    ///     index would make a re-ordered cell thin out a different subset, which reads as the field
    ///     rearranging itself when nothing moved.
    /// </remarks>
    static bool Keep(in InstanceBounds bounds, float density) {
        var hash = (uint)BitConverter.SingleToInt32Bits(bounds.Centre.X) * 0x9E3779B1u;
        hash ^= (uint)BitConverter.SingleToInt32Bits(bounds.Centre.Y) * 0x85EBCA77u;
        hash ^= (uint)BitConverter.SingleToInt32Bits(bounds.Centre.Z) * 0xC2B2AE3Du;

        hash ^= hash >> 15;
        hash *= 0x2545F491u;
        hash ^= hash >> 13;

        return hash / (float)uint.MaxValue < density;
    }

    void Grow(int instanceCount, int levelCount) {
        if (survivors.Length < instanceCount) {
            var size = Math.Max(instanceCount, Math.Max(survivors.Length * 2, 64));
            survivors = new uint[size];
            parameters = new InstanceParameters[size];
        }

        if (runs.Length < levelCount) {
            runs = new InstanceLodRun[levelCount];
            levels = new int[levelCount];
        }
    }
}
