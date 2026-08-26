// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Rendering.Ecs;

namespace Vixen.Animation.Ecs;

/// <summary>
///     Carries the blend-shape weights an animator sampled onto the component a frame draws from.
/// </summary>
/// <remarks>
///     <para>
///         <b>The last link of "animate a blend shape from a clip".</b> The importer turns a morph
///         channel into a scalar track, <see cref="AnimationClip" /> samples it,
///         <c>ClipMotion</c> collects it into <see cref="Animator.MorphWeights" /> as the blend tree
///         is evaluated — and this is what puts the answer where <c>MorphWeightSystem</c> will find
///         it. Without it the whole chain is a number nothing reads.
///     </para>
///     <para>
///         <b>Name to slot, through <c>BlendShapeWeights.Shapes</c>.</b> A clip names a shape because
///         a slot is not stable across a re-export, and the component is addressed by slot because
///         that is what the scatter dispatches over. The binding between them is published by
///         <c>MorphWeightSystem</c> out of what the render feature actually attached, so this system
///         needs no renderer of its own and works the same in a test, in the editor and on a headless
///         server.
///     </para>
///     <para>
///         ⚠ <b>Only the slots the animator named are written.</b> A shape no clip mentioned keeps
///         whatever was on the component — a value a script or an inspector set — because a clip
///         saying nothing about a shape is not the same as it asking for zero. That distinction is
///         the whole reason <see cref="MorphWeightBuffer" /> separates membership from value, and
///         losing it here would make playing any clip wipe every hand-set expression on the face.
///     </para>
///     <para>
///         ⚠ <b>In <see cref="SystemPhase.PreRender" />, before <see cref="MorphWeightSystem" /> and
///         after the animators have run.</b> The weights are produced in
///         <see cref="SystemPhase.Animation" /> and consumed by that system, so this sits between
///         them; the <see cref="UpdateBeforeAttribute" /> is what holds the second half, because the
///         two are registered from different places — this by <see cref="AnimationSystems" /> and
///         that by the renderer — and registration order cannot be relied on across them.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
[UpdateBefore(typeof(MorphWeightSystem))]
public sealed class BlendShapeAnimationSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription animated = new QueryDescription()
        .WithAll<AnimatorComponent, BlendShapeWeights>();

    /// <summary>How many entities had a weight written by the last run.</summary>
    public int Driven { get; private set; }

    /// <summary>
    ///     How many shapes an animator asked for that the entity's mesh does not have, last run.
    /// </summary>
    /// <remarks>
    ///     Reported rather than thrown, on <see cref="AnimationClip.UnresolvedChannels" />' terms: a
    ///     clip authored on a face with more shapes than this mesh has is an ordinary thing to play,
    ///     and a non-zero count here is how somebody notices they are playing a head's clip on a body.
    /// </remarks>
    public int Unbound { get; private set; }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Write<BlendShapeWeights>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Run(context.World);
        return dependency;
    }

    /// <summary>Writes every animated entity's sampled weights onto its component.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or a tool can drive one frame of this without a runner.</remarks>
    public void Run(World world) {
        ArgumentNullException.ThrowIfNull(world);

        Driven = 0;
        Unbound = 0;

        foreach (var chunk in world.Chunks(animated)) {
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                // ⚠ One entity at a time, because both of these are managed components: an array
                // field puts the value in the world's store and a four-byte handle in the chunk, so
                // `ReadValues` refuses them outright. MorphWeightSystem and SkinningSystem read
                // theirs the same way and for the same reason.
                var entity = entities[index];
                var animator = world.Read<AnimatorComponent>(entity).Value;

                if (animator is null || animator.MorphWeights.Count == 0) {
                    continue;
                }

                if (world.Read<BlendShapeWeights>(entity).Shapes is not { Length: > 0 } shapes) {
                    // No binding yet, which is what an entity looks like before the render feature
                    // has attached its mesh. Not an error and not counted as one — the frame after
                    // extraction has one.
                    continue;
                }

                var matched = Apply(animator.MorphWeights, shapes, ref world.Get<BlendShapeWeights>(entity));

                Unbound += animator.MorphWeights.Count - matched;

                // Counted only when something landed. An animator driving shapes this mesh has none of
                // wrote nothing, and calling that "driven" would hide exactly the case Unbound exists
                // to report — a head's clip playing on a body.
                if (matched > 0) {
                    Driven++;
                }
            }
        }
    }

    /// <summary>Lands a buffer's weights on a component, by name.</summary>
    /// <param name="buffer">What the animator sampled.</param>
    /// <param name="shapes">What the mesh calls each slot.</param>
    /// <param name="component">The component to write.</param>
    /// <returns>How many slots were written — the shapes the buffer and the mesh have in common.</returns>
    /// <remarks>
    ///     <para>
    ///         Separated from the query so that the part with the decisions in it can be tested
    ///         against a buffer and an array rather than against a world, a renderer and a frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The array is grown to the mesh's slot count and never shrunk.</b> A shorter one is
    ///         read as zero for the rest — <c>BlendShapeWeights</c>'s own rule — so an entity that was
    ///         given one number by hand and is then animated needs the room before slot four can be
    ///         written at all.
    ///     </para>
    /// </remarks>
    public static int Apply(
        MorphWeightBuffer buffer,
        ReadOnlySpan<string> shapes,
        ref BlendShapeWeights component
    ) {
        ArgumentNullException.ThrowIfNull(buffer);

        var weights = component.Weights;

        if (weights is null || weights.Length < shapes.Length) {
            Array.Resize(ref weights, shapes.Length);
            component.Weights = weights;
        }

        var matched = 0;

        for (var slot = 0; slot < shapes.Length; slot++) {
            if (buffer.TryGet(shapes[slot], out var weight)) {
                weights[slot] = weight;
                matched++;
            }
        }

        return matched;
    }
}
