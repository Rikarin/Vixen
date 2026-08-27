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

    /// <summary>The feature that draws virtualized meshes. Null is a harmless no-op.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second half, and its absence is what made a morphed cluster mesh draw at
    ///         rest.</b> A mesh with a cluster hierarchy is extracted down
    ///         <c>VirtualGeometryRenderFeature</c>'s path and never reaches
    ///         <see cref="MorphRenderFeature.Attach" />, so every weight written here was applied to
    ///         nothing — with no counter saying so, because the counters were the other feature's and
    ///         it had nothing attached to count.
    ///     </para>
    ///     <para>
    ///         The two are tried in order and the first that claims the object wins. They cannot both
    ///         claim one: an object is extracted down one path or the other, and which one it is is
    ///         decided by whether its mesh has a hierarchy at all.
    ///     </para>
    /// </remarks>
    public VirtualGeometryRenderFeature? Virtualized { get; set; }

    /// <summary>The render system the virtualized feature's records live in.</summary>
    /// <remarks>
    ///     Needed only by <see cref="Virtualized" />, whose per-object state is a
    ///     <c>RenderDataKey</c> array rather than a dictionary the feature owns — so the weights go
    ///     through the store the way a draw record does.
    /// </remarks>
    public RenderSystem? Renderer { get; set; }

    /// <summary>How many entities were given weights by the last run.</summary>
    public int Weighted { get; private set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Write<BlendShapeWeights>()
        .Read<RenderHandle>()
        .Build();

    /// <summary>How many entities were told what their mesh calls its shapes by the last run.</summary>
    public int Bound { get; private set; }

    /// <summary>How many of <see cref="Weighted" /> were virtualized rather than suballocated.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted separately because the two are indistinguishable from outside and were not
    ///     always both real.</b> A frame in which every morphed head is virtualized and this reads zero
    ///     is the defect this system was extended to close, and it is exactly the frame in which
    ///     <see cref="Weighted" /> alone would look healthy.
    /// </remarks>
    public int VirtualizedCount { get; private set; }

    /// <summary>How many shadow casters were given the same weights their object got.</summary>
    /// <remarks>
    ///     ⚠ <b>A virtualized entity's expression has to be written twice, to two features, from one
    ///     array.</b> The cluster gather morphs the paged vertices for the camera and
    ///     <see cref="MorphRenderFeature" /> morphs the fallback vertices for the shadow, and the two
    ///     are the same shapes over the same source numbering — so a frame that wrote one and not the
    ///     other is a face whose shadow is its rest pose. Nothing about the picture says which of the
    ///     two was missed; this number does. See <c>RenderHandle.Caster</c>.
    /// </remarks>
    public int CastersWeighted { get; private set; }

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
        Bound = 0;
        VirtualizedCount = 0;
        CastersWeighted = 0;

        if (Feature is null && Virtualized is null) {
            return;
        }

        // ⚠ Every frame and before the first weight, exactly as BeginBones is. The buffer holds one
        // frame's expressions and a run is claimed by position, so a frame that added without beginning
        // would grow it with expressions no instance points at — and a frame that never began would
        // hand every instance the previous frame's slot.
        if (Virtualized is not null && Renderer is not null) {
            Virtualized.BeginMorphs();
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

                // ⚠ And the other direction, which is what makes "animate a blend shape from a clip"
                // possible at all: a clip names a shape and this component is addressed by slot, and
                // the feature is the only thing that has seen both. Published once — a binding a
                // caller wrote by hand is a statement, not a stale value to correct — and only when
                // the mesh has actually attached, which is why an entity that appeared this frame is
                // bound on the next one.
                if (world.Read<BlendShapeWeights>(entities[index]).Shapes is null) {
                    var shapes = Feature is not null ? Feature.ShapesOf(handles[index].Object) : [];

                    if (shapes.Length == 0 && Virtualized is not null && Renderer is not null) {
                        shapes = Virtualized.ShapesOf(Renderer, handles[index].Object);
                    }

                    if (shapes.Length > 0) {
                        world.Get<BlendShapeWeights>(entities[index]).Shapes = shapes.ToArray();
                        Bound++;
                    }
                }

                // Null is at rest and is what a zeroed column reads as — see the component. Passing the
                // empty span rather than skipping is deliberate: it is what returns a face to rest when
                // a script clears the array, where a skip would leave the last expression on it for
                // ever.
                // ⚠ The shadow caster first, and outside the either/or below. A virtualized entity has
                // *two* objects that morph — the paged one the camera sees and the fallback one the
                // shadow stages draw — and the fall-through that picks between the two features is a
                // choice about the first only. Writing the weights here, before that choice, is what
                // makes a morphed face's shadow morph with it; leaving it to either branch would give
                // the caster the rest pose on whichever branch forgot, silently.
                if (Feature is not null
                    && handles[index].HasCaster
                    && Feature.SetWeights(handles[index].Caster, weights ?? [])) {
                    CastersWeighted++;
                }

                if (Feature is not null && Feature.SetWeights(handles[index].Object, weights ?? [])) {
                    Weighted++;

                    continue;
                }

                // ⚠ The other path, and the fall-through is what decides between them rather than a
                // question about the mesh. SetWeights answers false for an object it never attached,
                // which is exactly the object the other feature has — so neither system has to know
                // what a cluster hierarchy is.
                if (Virtualized is not null
                    && Renderer is not null
                    && Virtualized.SetMorphWeights(Renderer, handles[index].Object, weights ?? [])) {
                    Weighted++;
                    VirtualizedCount++;
                }
            }
        }
    }
}
