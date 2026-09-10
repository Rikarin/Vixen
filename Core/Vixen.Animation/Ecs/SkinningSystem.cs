// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
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
///         <b>The join is <see cref="RenderHandle" />, not a second component saying the same thing.</b>
///         ⚠ This used to query <c>SkinnedRenderer</c>, which <em>nothing in the tree ever added to an
///         entity</em> — so the query matched no chunk in any scene the engine builds and
///         <see cref="Run" /> walked nothing while every counter read healthy.
///         <see cref="MeshExtractionSystem" /> already writes the entity → <see cref="RenderObjectId" />
///         join for everything else and <see cref="MorphWeightSystem" /> already reads it; a second
///         component repeating it is a second thing that has to be filled in, and it was the one that
///         was not. See <see href="https://github.com/Rikarin/Vixen/issues/451" />.
///     </para>
///     <para>
///         <b>Two features, tried in that order, and the order is forced.</b>
///         <see cref="Virtualized" /> is asked first because it is the only one of the two that can
///         answer: <c>VirtualGeometryRenderFeature.SetBones</c> knows whether the object is one of
///         its own, where <c>SkinningRenderFeature.SetBones</c> writes into a parallel array indexed
///         by every object in the scene and cannot tell a mesh it draws from one it does not.
///         <see cref="MorphWeightSystem" /> runs its fall-through the other way round for the same
///         reason mirrored — there it is the classic feature that knows.
///     </para>
///     <para>
///         ⚠ <b>The virtualized branch is the one that is finished below this line.</b> A page vertex
///         carries four influences (<c>MeshletPages.InfluenceOffset</c>, twenty-four bytes a vertex,
///         round-tripped by <c>SkinnedClusterTests</c>); <c>MeshletBuilder</c> splits a cluster on
///         differing bone indices and records the <c>[FirstBone, FirstBone + BoneCount)</c> range a
///         traversal expands a bound by; <c>ClusterRaster.rvn</c> and <c>VisibilityResolve.rvn</c>
///         both blend the palette, gated on <c>instance.firstBone != Cull.NoBones</c>; and
///         <c>ModelImporter</c> builds a hierarchy for skinned meshes on purpose, so an imported
///         character is already extracted down that path.
///     </para>
///     <para>
///         ⚠ <b>The classic branch is not, and <see cref="Feature" /> is left here rather than used.</b>
///         <c>SurfaceVertex</c> — the interleaved vertex the suballocated mesh path uploads — carries
///         no bone indices and no bone weights, so there is nothing per vertex for a palette to be
///         blended against; and no <em>shading</em> pass in the library skins, since
///         <c>ForwardPlus.rvn</c> declares no <c>Skinned</c> permutation. Worse than useless rather
///         than merely absent: <c>SkinningRenderFeature.ValueOf</c> answers "skinned" from a non-zero
///         bone count, and <c>VertexSchema.Layout</c> throws on an attribute a stage declares and the
///         vertex format has no data for — which is exactly <c>bones0</c>/<c>weights0</c> in the
///         <c>Skinned</c> variant of <c>ShadowCaster</c>. So a host that sets <see cref="Feature" />
///         today buys a pipeline that refuses to build; it is wired here because the fall-through has
///         to have a second half, and it stays inert because nothing constructs that feature.
///     </para>
///     <para>
///         ⚠ <b>And the shadow caster is deliberately not given a palette</b>, where
///         <see cref="MorphWeightSystem" /> does give one. A virtualized entity's caster
///         (<see cref="RenderHandle.Caster" />) is an <em>ordinary</em> object drawn from
///         <c>MeshletMesh.Fallback</c> through the suballocated path — so it is the classic branch,
///         with the classic branch's missing influences, and writing a palette to it would turn the
///         permutation on for a vertex format that cannot feed it. The consequence is real and is
///         recorded rather than hidden: a skinned virtualized character's shadow is its bind pose
///         until <c>SurfaceVertex</c> carries influences. That is #451's fourth link and #141's item
///         (2), and it is the one thing here that cannot be closed by wiring.
///     </para>
///     <para>
///         ⚠ <b>Until #1221 this system was not in any game's loop at all</b>, which was a link
///         <em>below</em> the three #451 names: <c>AnimationSystems.AddAnimation</c> was called only
///         by <c>GizmoTests</c>, and no animation system carries <c>[GameSystem]</c>, so the
///         generated registry added none of them and <c>[UpdateInGroup]</c> only orders a system
///         something has already added. Widening a method nothing calls would have been a second
///         finished thing nothing calls. That link is closed —
///         <a href="https://github.com/Rikarin/Vixen/issues/1221">#1221</a>, Sample 13's
///         <c>Arena.Register</c> and the editor's <c>PlayAnimation</c> contribution both call it.
///     </para>
///     <para>
///         ⚠ <b>What that does not settle is who hands the features over.</b>
///         <see cref="Renderer" /> and <see cref="Virtualized" /> have to be set by something that
///         can see both this assembly and the renderer's, and <c>Vixen.Engine.Renderer</c> has no
///         reference to <c>Vixen.Animation</c> — so <c>WorldRenderer.Register</c> cannot be that
///         place and the application host has to be.
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
        .WithAll<AnimatorComponent, RenderHandle>();

    /// <summary>The render system whose objects the palettes belong to.</summary>
    /// <remarks>
    ///     Set rather than injected, because the render system is stood up by the host and an ECS
    ///     system is constructed by the runner; a null one means "there is no renderer this run",
    ///     which is what a headless server and most of this assembly's tests are.
    /// </remarks>
    public RenderSystem? Renderer { get; set; }

    /// <summary>The feature that holds the palette buffer for suballocated meshes.</summary>
    /// <remarks>
    ///     ⚠ <b>Inert on purpose.</b> Nothing constructs a <c>SkinningRenderFeature</c>, and a host
    ///     that did would reach a pipeline that refuses to build — see this system's remarks for why
    ///     the classic branch is the unfinished one.
    /// </remarks>
    public SkinningRenderFeature? Feature { get; set; }

    /// <summary>The feature that draws virtualized meshes. Null is a harmless no-op.</summary>
    /// <remarks>
    ///     <see cref="MorphWeightSystem.Virtualized" />'s counterpart, and the branch a skinned
    ///     character actually goes down: a mesh with a cluster hierarchy is extracted here and never
    ///     reaches <see cref="SkinningRenderFeature" /> at all.
    /// </remarks>
    public VirtualGeometryRenderFeature? Virtualized { get; set; }

    /// <summary>How many objects were given a palette by the last run.</summary>
    public int Skinned { get; private set; }

    /// <summary>How many of <see cref="Skinned" /> were virtualized rather than suballocated.</summary>
    /// <remarks>
    ///     ⚠ <b>Counted separately because the two are indistinguishable from outside.</b> A frame in
    ///     which every character is virtualized and this reads zero is a frame in which every palette
    ///     went to a feature that draws none of them — and <see cref="Skinned" /> alone would look
    ///     healthy. <see cref="MorphWeightSystem.VirtualizedCount" />'s reason exactly, one
    ///     deformation over.
    /// </remarks>
    public int VirtualizedCount { get; private set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Read<RenderHandle>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World);
        return dependency;
    }

    /// <summary>Fills every skinned object's palette from its animator's pose.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or a tool can drive one frame of skinning without a runner.</remarks>
    public void Run(World world) {
        ArgumentNullException.ThrowIfNull(world);

        Skinned = 0;
        VirtualizedCount = 0;

        // The renderer is needed by both branches — a palette is addressed through the object store —
        // so a run with no renderer is a headless one and there is nothing to write to.
        if (Renderer is null || (Feature is null && Virtualized is null)) {
            return;
        }

        // ⚠ Every frame and before the first palette, on both features. Each buffer holds one frame's
        // poses and a run is claimed by position, so a frame that never began would hand every
        // instance the previous frame's slot — and one that added without beginning would grow the
        // buffer with poses no instance points at.
        Feature?.Begin();
        Virtualized?.BeginBones();

        foreach (var chunk in world.Chunks(skinned)) {
            var entities = chunk.Entities;
            var handles = chunk.ReadValues<RenderHandle>();

            for (var index = 0; index < chunk.Count; index++) {
                // One entity at a time for the animator, and not because of style: an Animator is a
                // reference, which makes AnimatorComponent a *managed* component whose chunk column
                // holds handles into the world's store — so `ReadValues` refuses it outright.
                // MorphWeightSystem reads its weights the same way and for the same reason. The
                // handles beside it are unmanaged and do come out as a span.
                var animator = world.Read<AnimatorComponent>(entities[index]).Value;

                if (animator is null) {
                    continue;
                }

                var target = handles[index].Object;
                var count = animator.Skeleton.JointCount;
                var palette = ArrayPool<Matrix4x4>.Shared.Rent(count);

                try {
                    animator.ComputeSkinningMatrices(palette.AsSpan(0, count));

                    // ⚠ The virtualized feature first, and the fall-through is what decides between
                    // the two rather than a question about the mesh — neither this system nor the
                    // entity knows whether its mesh has a cluster hierarchy. This is the only order
                    // that works: SetBones here answers false for an object that is not one of its
                    // own, and the classic feature's SetBones cannot answer at all.
                    if (Virtualized is not null && Virtualized.SetBones(Renderer, target, palette.AsSpan(0, count))) {
                        Skinned++;
                        VirtualizedCount++;

                        continue;
                    }

                    if (Feature is not null) {
                        Feature.SetBones(Renderer, target, palette.AsSpan(0, count));
                        Skinned++;
                    }
                } finally {
                    ArrayPool<Matrix4x4>.Shared.Return(palette);
                }
            }
        }
    }
}
