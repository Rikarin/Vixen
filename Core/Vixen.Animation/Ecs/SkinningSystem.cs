// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Rendering;
using Vixen.Rendering.Features;

namespace Vixen.Animation.Ecs;

/// <summary>
///     Turns each animated entity's pose into the bone palette GPU skinning reads.
/// </summary>
/// <remarks>
///     <para>
///         The other end of the arrangement <c>SkinningRenderFeature</c> describes. That feature
///         owns the buffer, the upload and the push constant, and says explicitly that whoever fills
///         the palettes is the animation system, because there is no callback of the renderer's
///         between "animation finished" and "the first palette is written". This is the system it
///         means.
///     </para>
///     <para>
///         In <see cref="SystemPhase.PreRender" />, after <see cref="AnimationSystem" /> has run in
///         <see cref="SystemPhase.Animation" /> and after any IK a pose processor did. Palettes are
///         a per-frame thing — the feature's <c>Begin</c> resets the upload buffer and every skinned
///         object writes its own again — so a frame in which this does not run is a frame in which
///         nothing is skinned, rather than one that draws stale bones.
///     </para>
///     <para>
///         <b>Matrices are computed into a rented buffer, not a per-entity one.</b> A skeleton's
///         palette is written and immediately copied into the feature's upload buffer, so it lives
///         for the length of one call; holding one per character would be a hundred matrices of
///         permanently resident memory per instance to save an <c>ArrayPool</c> rent.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class SkinningSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription skinned = new QueryDescription()
        .WithAll<AnimatorComponent, SkinnedRenderer>();

    /// <summary>The render system whose objects the palettes belong to.</summary>
    /// <remarks>
    ///     Set rather than injected, because the render system is stood up by the host and an ECS
    ///     system is constructed by the runner; a null one means "there is no renderer this run",
    ///     which is what a headless server and most of this assembly's tests are.
    /// </remarks>
    public RenderSystem? Renderer { get; set; }

    /// <summary>The feature that holds the palette buffer.</summary>
    public SkinningRenderFeature? Feature { get; set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Read<SkinnedRenderer>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World);
        return dependency;
    }

    /// <summary>Fills every skinned object's palette from its animator's pose.</summary>
    /// <param name="world">The world.</param>
    /// <remarks>Public so a test or a tool can drive one frame of skinning without a runner.</remarks>
    public void Run(World world) {
        ArgumentNullException.ThrowIfNull(world);

        if (Renderer is null || Feature is null) {
            return;
        }

        Feature.Begin();

        foreach (var chunk in world.Chunks(skinned)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];
                var animator = world.Read<AnimatorComponent>(entity).Value;
                var target = world.Read<SkinnedRenderer>(entity).RenderObject;

                if (animator is null || !target.IsValid) {
                    continue;
                }

                var count = animator.Skeleton.JointCount;
                var palette = ArrayPool<Matrix4x4>.Shared.Rent(count);

                try {
                    animator.ComputeSkinningMatrices(palette.AsSpan(0, count));
                    Feature.SetBones(Renderer, target, palette.AsSpan(0, count));
                } finally {
                    ArrayPool<Matrix4x4>.Shared.Return(palette);
                }
            }
        }
    }
}
