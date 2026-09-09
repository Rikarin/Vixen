// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Features;

namespace Vixen.Engine.Renderer;

/// <summary>
///     Registers each authored LOD group with the feature that chooses between its levels.
/// </summary>
/// <remarks>
///     <para>
///         <b>The producer <c>LodRenderFeature</c> was written for and never had.</b> That feature
///         decides which level of a group a view sees and hides the rest, and it does nothing at all
///         until somebody calls <c>Add</c> and <c>Assign</c> — which, until this system, only a test
///         did. A scene therefore had no discrete LOD at any distance, on every mesh that is not
///         virtualized, silently and with every counter healthy.
///     </para>
///     <para>
///         <b>A group is a parent and its levels are its children.</b>
///         <see cref="LodGroupComponent" /> carries the thresholds and <see cref="LodLevel" /> says
///         which level a child is; the group's identity is the parent entity, because
///         <c>LodRenderFeature</c> chooses one level per group and two objects sharing a threshold
///         list must not share a choice.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.PreRender" />, after <see cref="MeshExtractionSystem" /></b>,
///         which is <c>MorphWeightSystem</c>'s reason exactly: a child that appeared this frame has
///         no <see cref="RenderHandle" /> until that system ran, and a membership written for an
///         object that does not exist names whatever takes the slot. The declared access is what
///         orders them — this reads <see cref="RenderHandle" />, which that one writes.
///     </para>
///     <para>
///         ⚠ <b>Restated every frame rather than stamped once.</b> A render object does not survive
///         <c>MeshExtractionSystem.Resettle</c> — a document reload in the editor is exactly that —
///         so a membership written once would be pointing at a dead index by the second load. The
///         walk is over the entities carrying <see cref="LodLevel" /> and no others, so a scene with
///         no LOD in it pays a walk over no chunks.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class LodExtractionSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription levels = new QueryDescription().WithAll<LodLevel, Parent, RenderHandle>();
    readonly QueryDescription authored = new QueryDescription().WithAll<LodGroupComponent>();

    /// <summary>Which group index each group's parent entity was given.</summary>
    readonly Dictionary<Entity, int> registered = [];

    /// <summary>Reused per run so that a steady-state frame allocates nothing.</summary>
    readonly List<Entity> departed = [];
    readonly HashSet<Entity> present = [];

    /// <summary>The feature that chooses a level. Null is a harmless no-op.</summary>
    /// <remarks>
    ///     Set rather than injected, <c>MorphWeightSystem.Feature</c>'s reason: the feature is the
    ///     renderer's and an ECS runner has no renderer, so a null one means "there is no renderer
    ///     this run" — which is what a headless server is.
    /// </remarks>
    public LodRenderFeature? Feature { get; set; }

    /// <summary>The render system the memberships live in.</summary>
    public RenderSystem? Renderer { get; set; }

    /// <summary>How many groups are registered right now.</summary>
    /// <remarks>
    ///     ⚠ <b>Registered, not authored.</b> A group whose children have not been extracted yet is
    ///     counted here as soon as its parent is seen, because the thresholds are the parent's and do
    ///     not wait on a render object. <see cref="Assigned" /> is the number that waits.
    /// </remarks>
    public int GroupCount => registered.Count;

    /// <summary>How many levels were given a membership by the last run.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that is zero when this system is doing nothing.</b> A scene whose LOD
    ///     parents are all registered and whose children are all still waiting on their meshes reads
    ///     <see cref="GroupCount" /> healthy and this zero, which is exactly the frame in which no
    ///     level is ever hidden.
    /// </remarks>
    public int Assigned { get; private set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<LodGroupComponent>()
        .Read<LodLevel>()
        .Read<Parent>()
        .Read<RenderHandle>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World);
        return dependency;
    }

    /// <summary>Registers every authored group and gives every extracted level its membership.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or an editor can drive one frame of this without a runner.</remarks>
    public void Run(World world) {
        ArgumentNullException.ThrowIfNull(world);

        Assigned = 0;

        if (Feature is null || Renderer is null) {
            return;
        }

        Register(world);
        Assign(world);
    }

    /// <summary>Gives every group's parent an index, and takes the index back when it goes.</summary>
    void Register(World world) {
        present.Clear();

        foreach (var chunk in world.Chunks(authored)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                var entity = entities[index];

                present.Add(entity);

                if (registered.ContainsKey(entity)) {
                    continue;
                }

                // ⚠ One entity at a time: an array field makes LodGroupComponent a *managed*
                // component, so its values live in the world's store and `ReadValues` refuses the
                // column outright. BlendShapeWeights is read the same way for the same reason.
                var thresholds = world.Read<LodGroupComponent>(entity).Thresholds ?? [];

                registered[entity] = Feature!.Add(thresholds);
            }
        }

        // ⚠ And the other direction, which is what stops a streaming level walking the group list up
        // for ever: a group whose parent has died or dropped the component gives its slot back, and
        // the next Add takes it. Collected first and removed after, because the dictionary is what
        // is being walked.
        departed.Clear();

        foreach (var (entity, group) in registered) {
            if (present.Contains(entity)) {
                continue;
            }

            departed.Add(entity);
            Feature!.Release(group);
        }

        foreach (var entity in departed) {
            registered.Remove(entity);
        }
    }

    /// <summary>Writes each extracted level's membership.</summary>
    void Assign(World world) {
        foreach (var chunk in world.Chunks(levels)) {
            var handles = chunk.ReadValues<RenderHandle>();
            var parents = chunk.ReadValues<Parent>();
            var members = chunk.ReadValues<LodLevel>();

            for (var index = 0; index < chunk.Count; index++) {
                // A child whose parent carries no group is not in one. That is the ordinary reading
                // of the hierarchy rather than a mistake — a level dragged out of its group keeps its
                // LodLevel and stops being a level, and is drawn.
                if (!registered.TryGetValue(parents[index].Value, out var group)) {
                    continue;
                }

                Feature!.Assign(Renderer!, handles[index].Object, group, Math.Max(members[index].Level, 0));
                Assigned++;

                // ⚠ The shadow caster too, and outside no branch. A virtualized entity draws its
                // shadow through a second render object — see RenderHandle.Caster — and a caster
                // left out of the group is a level that is hidden and still casts, which is a shadow
                // with no object under it. MorphWeightSystem writes its caster for the same reason.
                if (handles[index].HasCaster) {
                    Feature!.Assign(
                        Renderer!,
                        handles[index].Caster,
                        group,
                        Math.Max(members[index].Level, 0)
                    );
                }
            }
        }
    }
}
