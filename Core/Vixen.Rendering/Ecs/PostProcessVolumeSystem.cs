// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Rendering.Ecs;

/// <summary>Folds the volumes the camera is in into one overlay for the frame.</summary>
/// <remarks>
///     <para>
///         <b>The whole of what a post-process volume is at run time.</b> Gather every
///         <see cref="PostProcessVolume" />, weigh each by how far the camera is from it, sort by
///         priority, and lay them over one another in that order. What comes out is a
///         <see cref="PostProcessOverlay" /> the frame's nodes read — see <c>IPostProcessTarget</c>.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, ordered by its declared access</b>, the same
///         placement <see cref="LightExtractionSystem" /> explains: <c>TransformSystem</c> writes
///         <see cref="WorldTransform" /> in the same phase and this reads it, so a volume that moved
///         this frame is tested where it now is rather than where it was.
///     </para>
///     <para>
///         ⚠ <b>The camera's position comes from the view rather than from the world.</b> The view is
///         what the frame is actually drawn through — <c>CameraExtractionSystem</c> has already
///         decided which of a scene's cameras won — so asking the world again would be a second
///         answer to a question already settled, and would disagree the moment a scene had two
///         cameras.
///     </para>
///     <para>
///         ⚠ <b>Sorted ascending and folded in that order, because the fold is not commutative.</b>
///         Priority decides which volume is on top, and "on top" means applied last. A stable sort is
///         not required and is not promised: two volumes at one priority resolve arbitrarily, which
///         the component's own remarks call a level-design mistake rather than a case to define.
///     </para>
/// </remarks>
/// <param name="view">The view whose camera position decides which volumes apply.</param>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class PostProcessVolumeSystem(RenderView view) : SystemBase, IDeclaredAccess {
    readonly QueryDescription volumes = new QueryDescription().WithAll<PostProcessVolume, WorldTransform>();
    readonly List<(int Priority, float Weight, PostProcessSettings Settings)> gathered = [];

    /// <summary>The view this reads a camera position from.</summary>
    public RenderView View { get; } = view ?? throw new ArgumentNullException(nameof(view));

    /// <summary>What the last pass folded to.</summary>
    /// <remarks>
    ///     <see cref="PostProcessOverlay.None" /> for a scene with no volumes, or one whose volumes
    ///     the camera is well outside of — and every node then keeps exactly what its document gave
    ///     it, which is what makes the whole feature cost nothing when nobody uses it.
    /// </remarks>
    public PostProcessOverlay Overlay { get; private set; }

    /// <summary>How many volumes the last pass saw.</summary>
    public int VolumeCount { get; private set; }

    /// <summary>How many of them contributed anything.</summary>
    /// <remarks>
    ///     The number that says "the volume you placed is not reaching the camera", which is the
    ///     failure this feature has: a volume with a zero weight, zero extents or a camera outside its
    ///     blend radius is invisible and looks exactly like one that is not wired up.
    /// </remarks>
    public int ContributingCount { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <see cref="LightExtractionSystem" /> gives:
    ///     naming a component type in a generic call is what assigns it an id, and on the first frame
    ///     an attribute would have nothing to look up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<WorldTransform>()
        .Read<PostProcessVolume>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Fold(context.World);
        return dependency;
    }

    /// <summary>Gathers, sorts and folds this frame's volumes.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test, a tool or an editor can fold without standing up a runner.</remarks>
    public void Fold(World world) {
        ArgumentNullException.ThrowIfNull(world);

        gathered.Clear();
        VolumeCount = 0;
        ContributingCount = 0;

        var camera = View.Position;

        foreach (var chunk in world.Chunks(volumes)) {
            var authored = chunk.ReadValues<PostProcessVolume>();
            var placements = chunk.ReadValues<WorldTransform>();

            for (var i = 0; i < chunk.Count; i++) {
                VolumeCount++;

                var volume = authored[i];
                var weight = Math.Clamp(volume.Weight, 0f, 1f) * Reach(volume, placements[i], camera);

                if (weight <= 0f || volume.Settings.IsEmpty) {
                    continue;
                }

                ContributingCount++;
                gathered.Add((volume.Priority, weight, volume.Settings));
            }
        }

        if (gathered.Count == 0) {
            Overlay = PostProcessOverlay.None;
            return;
        }

        gathered.Sort(static (a, b) => a.Priority.CompareTo(b.Priority));

        var overlay = PostProcessOverlay.None;

        foreach (var (_, weight, settings) in gathered) {
            overlay.Add(settings, weight);
        }

        Overlay = overlay;
    }

    /// <summary>How much a volume reaches a world-space point.</summary>
    /// <remarks>
    ///     <para>
    ///         The point is taken into the volume's own space first, so a rotated or scaled entity is
    ///         a rotated or scaled box. That is the reason this inverts a matrix rather than comparing
    ///         against world-space bounds: an axis-aligned test would make rotating a volume change
    ///         its shape, which is the kind of thing somebody notices only after building a level
    ///         around it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A singular transform reaches nothing rather than everything.</b> A volume scaled
    ///         to zero on an axis cannot be inverted, and the two answers available are "it contains
    ///         every point" and "it contains none" — the second is the one that looks like the mistake
    ///         it is, rather than blacking out the level.
    ///     </para>
    /// </remarks>
    static float Reach(in PostProcessVolume volume, in WorldTransform placement, Vector3 point) {
        if (volume.Unbound) {
            return 1f;
        }

        if (!Matrix4x4.Invert(placement.Value, out var inverse)) {
            return 0f;
        }

        return volume.Falloff(Matrix4x4.TransformPosition(point, inverse));
    }
}
