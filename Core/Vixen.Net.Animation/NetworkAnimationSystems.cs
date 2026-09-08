// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;
using Vixen.Net.Replication;

namespace Vixen.Net.Animation;

/// <summary>Gives every networked character a bone selection, so the pose path is not a no-op.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Both pose systems bail out on a null
///         <see cref="NetworkBoneSelection.Joints" />, and nothing in the repository ever wrote
///         one</b> (<see href="https://github.com/Rikarin/Vixen/issues/481" />). So a game could
///         register the replicators, schedule the systems and attach <see cref="NetworkBones" /> to
///         every character, and the whole path would do nothing — no exception, no counter, no bytes.
///     </para>
///     <para>
///         <b>It adds the component as well as filling it</b>, for the same reason the physics
///         registration adds the character pass whether or not there are characters: the alternative
///         is a second thing a game has to know to do, and forgetting it is invisible.
///     </para>
///     <para>
///         <b>Once per character, not once per tick.</b> The selection is a function of the rig, so it
///         is computed the first time a character is seen with an animator and never again — and a
///         character whose <c>Animator</c> has not been attached yet is simply seen again next tick.
///     </para>
///     <para>
///         ⚠ <b>The policy has to give the same answer on every peer, and that is not a style rule.</b>
///         The selection is not replicated, by design, because it comes from content both ends have.
///         A <see cref="Policy" /> that read anything else — a distance, a camera, a random subset —
///         would have the two ends unpacking one wire layout into different joints, which reads as a
///         character folded inside out rather than as a wiring mistake.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
[UpdateBefore(typeof(NetworkBonesCaptureSystem))]
[UpdateBefore(typeof(NetworkBonesApplySystem))]
public sealed class NetworkBoneSelectionSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription posed = new QueryDescription().WithAll<AnimatorComponent, NetworkBones, NetworkId>();

    readonly List<Entity> pending = [];

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Read<NetworkId>()
        .Write<NetworkBoneSelection>()
        .Build();

    /// <summary>Which joints of a rig go on the wire. Defaults to <see cref="NetworkBoneSelector.Trunk" />.</summary>
    public Func<Skeleton, int[]> Policy { get; set; } = skeleton => NetworkBoneSelector.Trunk(skeleton);

    /// <summary>How many characters have been given a selection.</summary>
    public long SelectedCount { get; private set; }

    /// <summary>How many joints those selections came to, added up.</summary>
    public long JointCount { get; private set; }

    /// <summary>
    ///     How many characters were skipped because their animator has not been attached yet.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A handful is ordinary and a number that keeps climbing is a character that will never
    ///     pose.</b> An <c>AnimatorComponent</c> holds a handle to a managed <c>Animator</c> and it is
    ///     null until the content is loaded — so this counts the frames a character spent waiting for
    ///     a rig. Left uncounted, a rig that never arrives is indistinguishable from a game that never
    ///     scheduled this system, and both look like a character standing in its bind pose.
    /// </remarks>
    public long WaitingCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Select(context.World);

        return dependency;
    }

    /// <summary>Gives a selection to every networked character that has not got one.</summary>
    /// <param name="world">The world.</param>
    /// <returns>How many were given one.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public int Select(World world) {
        ArgumentNullException.ThrowIfNull(world);

        pending.Clear();

        foreach (var chunk in world.Chunks(posed)) {
            pending.AddRange(chunk.Entities);
        }

        var given = 0;

        // Collected first, because adding a component is a structural change and the chunks above are
        // being walked.
        foreach (var entity in pending) {
            if (!world.IsAlive(entity)) {
                continue;
            }

            if (world.TryGet<NetworkBoneSelection>(entity, out var selection) && selection.Joints is not null) {
                continue;
            }

            if (world.Read<AnimatorComponent>(entity).Value is not { } animator) {
                WaitingCount++;

                continue;
            }

            var joints = Policy(animator.Skeleton);

            if (world.Has<NetworkBoneSelection>(entity)) {
                world.Get<NetworkBoneSelection>(entity).Joints = joints;
            } else {
                world.Add(entity, new NetworkBoneSelection { Joints = joints });
            }

            SelectedCount++;
            JointCount += joints.Length;
            given++;
        }

        return given;
    }
}

/// <summary>The networked-animation passes, as a set a game registers in one line.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The assembly had no registration surface at all</b>
///         (<see href="https://github.com/Rikarin/Vixen/issues/481" />): four systems and two
///         replicators, constructed only by <c>NetworkBonesTests</c>, with nothing of the
///         <c>AddPhysics</c> / <c>AddAnimation</c> shape that every other subsystem in this engine is
///         reached by. So the README's bandwidth analysis described a path no shipped configuration
///         could take.
///     </para>
///     <para>
///         <b>All four passes are added, on both ends.</b> A capture system on a client and an apply
///         system on a server each walk a query and then refuse every entity in it, because
///         <c>IsAuthority</c> is the exact complement on the two peers — which costs a sweep and buys
///         a peer that changes role, a listen server that holds both at once, and one registration
///         call instead of two that can disagree.
///     </para>
/// </remarks>
public static class NetworkAnimationSystems {
    /// <summary>Adds the selection, pose and animator passes to a runner.</summary>
    /// <param name="runner">The runner.</param>
    /// <returns>The runner, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runner" /> is null.</exception>
    /// <remarks>
    ///     The order between them is the phases' rather than this list's — the two pose passes declare
    ///     <c>LateUpdate</c> and the two animator passes declare <c>PreRender</c>, and the selection
    ///     runs before both pose passes because it is what stops them being no-ops.
    /// </remarks>
    public static SystemRunner AddNetworkAnimation(this SystemRunner runner) {
        ArgumentNullException.ThrowIfNull(runner);

        return runner
            .Add(new NetworkBoneSelectionSystem())
            .Add(new NetworkBonesCaptureSystem())
            .Add(new NetworkBonesApplySystem())
            .Add(new NetworkAnimatorCaptureSystem())
            .Add(new NetworkAnimatorApplySystem());
    }

    /// <summary>Adds the networked-animation passes to a loop.</summary>
    /// <param name="loop">The loop.</param>
    /// <returns>The loop, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    public static EngineLoop AddNetworkAnimation(this EngineLoop loop) {
        ArgumentNullException.ThrowIfNull(loop);

        loop.Systems.AddNetworkAnimation();

        return loop;
    }

    /// <summary>Registers the three replicators the passes write and read.</summary>
    /// <param name="registry">The registry. Must be the same on both peers.</param>
    /// <param name="precision">
    ///     How many bits each bone slot is worth, or null for full precision. ⚠ The same table on both
    ///     ends: it is part of the wire layout rather than a local quality setting, and two peers with
    ///     different tables disagree about where every record after the first one starts.
    /// </param>
    /// <returns>The registry, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry" /> is null.</exception>
    /// <remarks>
    ///     Separate from the system registration, and deliberately: a registry is built once at
    ///     startup and hashed into the session's content hash, while systems belong to a loop. A game
    ///     that ran one and not the other would be refused at the handshake or would schedule passes
    ///     that write a component nothing sends — and the second of those is silent, which is why this
    ///     is named rather than folded in.
    /// </remarks>
    public static ReplicationRegistry AddNetworkAnimation(
        this ReplicationRegistry registry,
        NetworkBonePrecision? precision = null
    ) {
        ArgumentNullException.ThrowIfNull(registry);

        registry.Register(new NetworkBonesReplicator(precision ?? NetworkBonePrecision.Full));
        registry.Register(new NetworkAnimatorReplicator());
        registry.Register(new NetworkAnimatorParametersReplicator());

        return registry;
    }
}
