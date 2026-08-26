// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Rendering.Features;

namespace Vixen.Rendering.Ecs;

/// <summary>
///     Carries each entity's blend-shape weights into the feature that dispatches for them.
/// </summary>
/// <remarks>
///     <para>
///         <b>The other end of the arrangement <see cref="MorphRenderFeature" /> describes</b>, and the
///         same shape as <c>SkinningSystem</c> next door in <c>Vixen.Animation</c>: the feature owns
///         the buffer, the pipeline and the dispatch, and says explicitly that whoever fills the
///         numbers is a system, because there is no callback of the renderer's between "the animation
///         finished" and "the first weight is written".
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, after <see cref="MeshExtractionSystem" />
///         because the object has to exist before it can be given weights</b> — an entity that
///         appeared this frame has no <see cref="RenderHandle" /> until that system ran, and a weight
///         written for an object that does not exist is silently dropped. The declared access is what
///         orders them: this reads <see cref="RenderHandle" />, which that one writes.
///     </para>
///     <para>
///         ⚠ <b>Weights are pushed every frame and the feature does the comparing.</b> A system that
///         only pushed on change would be a second place that decides what "changed" means, and the
///         first place — <see cref="MorphRenderFeature.SetWeights" /> — is the one that also knows
///         whether the vertices have ever been written at all.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class MorphWeightSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription weighted = new QueryDescription().WithAll<BlendShapeWeights, RenderHandle>();

    /// <summary>The feature that holds the morphed vertices. Null is a harmless no-op.</summary>
    /// <remarks>
    ///     Set rather than injected, <c>SkinningSystem.Feature</c>'s reason: the feature owns a device
    ///     buffer and an ECS runner has no device, so a null one means "there is no renderer this run".
    /// </remarks>
    public MorphRenderFeature? Feature { get; set; }

    /// <summary>How many entities were given weights by the last run.</summary>
    public int Weighted { get; private set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<BlendShapeWeights>()
        .Read<RenderHandle>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World);
        return dependency;
    }

    /// <summary>Pushes every weighted entity's weights at the feature.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or a tool can drive one frame of this without a runner.</remarks>
    public void Run(World world) {
        ArgumentNullException.ThrowIfNull(world);

        Weighted = 0;

        if (Feature is null) {
            return;
        }

        foreach (var chunk in world.Chunks(weighted)) {
            var entities = chunk.Entities;
            var handles = chunk.ReadValues<RenderHandle>();

            for (var index = 0; index < chunk.Count; index++) {
                // ⚠ One entity at a time, and not because of style. An array field makes
                // BlendShapeWeights a *managed* component: its values live in the world's store and the
                // chunk column holds four-byte handles into it, so `ReadValues` refuses outright — see
                // Chunk.PublicColumn. SkinningSystem reads its animator the same way and for the same
                // reason. The handles beside it are unmanaged and do come out as a span.
                var weights = world.Read<BlendShapeWeights>(entities[index]).Weights;

                // Null is at rest and is what a zeroed column reads as — see the component. Passing the
                // empty span rather than skipping is deliberate: it is what returns a face to rest when
                // a script clears the array, where a skip would leave the last expression on it for
                // ever.
                if (Feature.SetWeights(handles[index].Object, weights ?? [])) {
                    Weighted++;
                }
            }
        }
    }
}
