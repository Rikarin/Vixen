// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Ecs;
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Net.Replication;
using Vixen.Net.Rules;
using Vixen.Net.Sessions;

namespace Vixen.Net.Animation;

/// <summary>Reads an animator into the components the wire carries.</summary>
/// <remarks>
///     <para>
///         Runs on whichever peer decides how a character animates — which is the same
///         <c>NetworkRules.Write</c> question a rigid body asks, for the same reason: one policy per
///         object rather than one per subsystem.
///     </para>
///     <para>
///         <b>Only the first layer's state is sent.</b> A layered animator is a base machine plus
///         additive or masked ones — a wave while running, a flinch while aiming — and those layers
///         are driven by the same parameters that are already on the wire. Sending every layer's
///         position would be sending several answers to a question the receiver can work out, and
///         the base layer is the one that cannot be derived because it is what everything else is
///         layered onto.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class NetworkAnimatorCaptureSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription animated = new QueryDescription()
        .WithAll<AnimatorComponent, NetworkAnimator, NetworkAnimatorParameters, NetworkId>();

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<AnimatorComponent>()
        .Read<NetworkId>()
        .Write<NetworkAnimator>()
        .Write<NetworkAnimatorParameters>()
        .Build();

    /// <summary>Who decides what, and who this peer is. Null is server-authoritative.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>Which player this peer is, or <see cref="PlayerId.None" /> for a server.</summary>
    public PlayerId Local { get; set; } = PlayerId.None;

    /// <summary>How many animators have been published.</summary>
    public long PublishedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Publish(context.World);

        return dependency;
    }

    /// <summary>Reads every networked animator this peer decides.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Publish(World world) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Chunks(animated)) {
            var states = chunk.Values<NetworkAnimator>();
            var parameters = chunk.Values<NetworkAnimatorParameters>();
            var ids = chunk.ReadValues<NetworkId>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!IsAuthority(ids[index])) {
                    continue;
                }

                // One entity at a time. An Animator is a managed component, so the chunk holds a
                // handle rather than the object and there is no span of them to sweep — which is the
                // right storage for it: it is a graph of layers and clips, not a value.
                if (world.Read<AnimatorComponent>(entities[index]).Value is not { } animator) {
                    continue;
                }

                Read(animator, ref states[index], ref parameters[index]);
                PublishedCount++;
            }
        }
    }

    /// <summary>Whether this peer decides how an object animates.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it does.</returns>
    public bool IsAuthority(NetworkId id) =>
        Rules is { } rules
            ? rules.MayWrite(id, Local)
            : NetworkRules.Allows(RuleAudience.ServerOnly, Local, isOwner: false);

    static void Read(Animator animator, ref NetworkAnimator state, ref NetworkAnimatorParameters parameters) {
        state.Speed = animator.Speed;

        if (animator.Layers.Count > 0 && animator.Layers[0].States is { } machine) {
            state.State = (ushort)machine.CurrentState;
            state.NormalizedTime = machine.NormalizedTime;
        }

        var count = Math.Min(animator.Parameters.Count, NetworkAnimatorReplicator.MaxParameters);
        parameters.Count = (byte)count;

        for (var index = 0; index < count; index++) {
            parameters.Values[index] = animator.Parameters.GetFloat(index);
        }
    }
}

/// <summary>Drives an animator from what arrived.</summary>
/// <remarks>
///     <para>
///         <b>The parameters are written and the animator is left to run.</b> That is the whole
///         point of sending inputs: the receiving animator evaluates its own transitions, blends its
///         own layers and produces its own pose, so everything the animation system does locally
///         keeps working — events fire, root motion is computed, IK runs.
///     </para>
///     <para>
///         <b>The state is a correction, not a command, and only when it disagrees.</b> Calling
///         <c>Play</c> every tick would restart the state every tick and nothing would ever animate.
///         So the state is applied only when the receiver's machine is somewhere else — which is a
///         late joiner, a client that missed a parameter edge, or a genuine divergence — and it is
///         applied with a short crossfade rather than a cut, because a correction the player can see
///         is worse than a correction that takes a tenth of a second.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.PreRender)]
public sealed class NetworkAnimatorApplySystem : SystemBase, IDeclaredAccess {
    /// <summary>How long a corrected state takes to blend in.</summary>
    /// <remarks>
    ///     Short enough to be a correction rather than a transition, long enough not to pop. A cut
    ///     is visible on anything a player is looking at, and the thing being corrected is by
    ///     definition something that was already wrong — making it wrong <i>and</i> jarring is the
    ///     worse of the two.
    /// </remarks>
    public const float CorrectionCrossfade = 0.1f;

    readonly QueryDescription animated = new QueryDescription()
        .WithAll<AnimatorComponent, NetworkAnimator, NetworkAnimatorParameters, NetworkId>();

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<NetworkAnimator>()
        .Read<NetworkAnimatorParameters>()
        .Read<NetworkId>()
        .Read<AnimatorComponent>()
        .Build();

    /// <summary>Who decides what, and who this peer is. Null is server-authoritative.</summary>
    public NetworkRulesRegistry? Rules { get; set; }

    /// <summary>Which player this peer is, or <see cref="PlayerId.None" /> for a server.</summary>
    public PlayerId Local { get; set; } = PlayerId.None;

    /// <summary>How many animators have been driven from the wire.</summary>
    public long AppliedCount { get; private set; }

    /// <summary>How many were somewhere else and had their state corrected.</summary>
    /// <remarks>
    ///     Worth watching. A few is late joiners and lost packets; a lot means the receiving
    ///     animator is not reaching the same state from the same parameters, which is the
    ///     determinism assumption <see cref="NetworkAnimator" /> documents failing quietly.
    /// </remarks>
    public long CorrectedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Apply(context.World);

        return dependency;
    }

    /// <summary>Drives every networked animator this peer does not decide.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public void Apply(World world) {
        ArgumentNullException.ThrowIfNull(world);

        foreach (var chunk in world.Chunks(animated)) {
            var states = chunk.ReadValues<NetworkAnimator>();
            var parameters = chunk.ReadValues<NetworkAnimatorParameters>();
            var ids = chunk.ReadValues<NetworkId>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (IsAuthority(ids[index])) {
                    continue;
                }

                // Managed, so reached one at a time — see the capture system's note.
                if (world.Read<AnimatorComponent>(entities[index]).Value is not { } animator) {
                    continue;
                }

                Drive(animator, states[index], parameters[index]);
                AppliedCount++;
            }
        }
    }

    /// <summary>Whether this peer decides how an object animates.</summary>
    /// <param name="id">The object.</param>
    /// <returns>Whether it does.</returns>
    public bool IsAuthority(NetworkId id) =>
        Rules is { } rules
            ? rules.MayWrite(id, Local)
            : NetworkRules.Allows(RuleAudience.ServerOnly, Local, isOwner: false);

    void Drive(Animator animator, in NetworkAnimator state, in NetworkAnimatorParameters parameters) {
        animator.Speed = state.Speed;

        var count = Math.Min(parameters.Count, animator.Parameters.Count);

        for (var index = 0; index < count; index++) {
            // Through SetFloat, which converts to whatever the parameter was declared as — an int
            // truncates and a bool takes the zero test. One representation on the wire, three at
            // rest, and the conversion is the animation system's own.
            animator.Parameters.SetFloat(index, parameters.Values[index]);
        }

        if (animator.Layers.Count == 0 || animator.Layers[0].States is not { } machine) {
            return;
        }

        // Only when it disagrees. Playing every tick would restart the state every tick and nothing
        // would ever animate — which is the obvious implementation and is wrong in a way that looks
        // like the animation being broken rather than like the network being wrong.
        if (machine.CurrentState != state.State) {
            machine.Play(state.State, CorrectionCrossfade, state.NormalizedTime);
            CorrectedCount++;
        }
    }
}
