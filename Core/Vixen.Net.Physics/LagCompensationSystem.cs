// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Net.Engine;
using Vixen.Net.Time;
using PhysicsBody = global::Vixen.Physics.Ecs.PhysicsBody;
using PhysicsWritebackSystem = global::Vixen.Physics.Ecs.PhysicsWritebackSystem;

namespace Vixen.Net.Physics;

/// <summary>Marks a body whose past the server keeps, so a shot can be judged against it.</summary>
/// <remarks>
///     <para>
///         <b>A tag rather than a call, because the alternative is a call nobody makes.</b>
///         <c>LagCompensator.Track</c> and <c>Forget</c> are the honest primitives and they need a
///         <c>BodyHandle</c>, which a game gets from a component it did not write; asking every spawn
///         and every despawn to remember them is how a body ends up untracked for a match and how a
///         destroyed one's history is kept for an hour.
///     </para>
///     <para>
///         <b>Put it on what gets shot at</b> — players, vehicles, anything moving fast enough that a
///         client's view of it is meaningfully behind the server's. Not on the level: a wall does not
///         move, so its history is thirty-two copies of one pose and rewinding it costs the same as
///         rewinding a player.
///     </para>
/// </remarks>
public struct LagCompensated : ITagComponent;

/// <summary>Fills the pose ring, once a tick, for every body that asked to be in it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>LagCompensator.Capture</c> was never called in any process.</b> The compensator,
///         the ring, <c>ClampFor</c> and the rewind scope were all built and all correct, and the
///         only file in the repository that constructed one was
///         <c>Vixen.Net.Physics.Tests/LagCompensationTests.cs</c> — so no rewind had ever run outside
///         a unit test and the memory shape, the clamping and the restore ordering had never met a
///         frame. See <see href="https://github.com/Rikarin/Vixen/issues/515" />.
///     </para>
///     <para>
///         <b>The answer that issue asks for is "by the package", and this is it.</b> Wiring it in a
///         sample would have proved one game's arrangement; wiring it here means a server adds one
///         system and tags what gets shot at, and the tracking follows the world rather than the call
///         sites. What stays the game's is the rewind itself — <c>RewindScope</c> around the ray
///         cast, at the tick the shooter claimed — because only the game knows what a shot is.
///     </para>
///     <para>
///         <b>After the writeback and before the replication capture</b>, which is where
///         <c>LagCompensator.Capture</c>'s own remarks say to put it: the history and the snapshot are
///         recording the same instant for two different reasons, and a history that lags the snapshot
///         by a tick rewinds to a world the client was never sent.
///     </para>
///     <para>
///         ⚠ <b>It refuses to run during a rewind rather than skipping one.</b> That is
///         <c>Capture</c>'s own exception and it is deliberately not caught here: a capture taken
///         mid-rewind records the historical poses as the present, the ring fills with its own past,
///         and the result is a hit-registration bug that rots slowly. A scope left undisposed is a
///         bug in the game's shot code and is worth a stack trace at the call site.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateAfter(typeof(PhysicsWritebackSystem))]
[UpdateBefore(typeof(NetworkTransformCaptureSystem))]
public sealed class LagCompensationSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription compensated = new QueryDescription().WithAll<LagCompensated, PhysicsBody>();

    readonly LagCompensator compensator;
    readonly TickManager clock;
    readonly HashSet<uint> live = [];
    readonly HashSet<uint> tracked = [];
    readonly List<uint> dropped = [];

    /// <summary>Creates the system.</summary>
    /// <param name="compensator">The ring to fill. The game holds it, because the game rewinds it.</param>
    /// <param name="clock">
    ///     The session's clock. <see cref="TickManager.Current" /> is the tick a pose is filed under,
    ///     which is the same tick <c>ReplicationServer.Capture</c> stamps on the snapshot.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public LagCompensationSystem(LagCompensator compensator, TickManager clock) {
        ArgumentNullException.ThrowIfNull(compensator);
        ArgumentNullException.ThrowIfNull(clock);

        this.compensator = compensator;
        this.clock = clock;
    }

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare().Read<LagCompensated>().Build();

    /// <summary>The ring this fills.</summary>
    public LagCompensator Compensator => compensator;

    /// <summary>How many ticks have been recorded.</summary>
    public long CapturedTicks { get; private set; }

    /// <summary>How many bodies have been taken into the ring since this started.</summary>
    public long AdoptedCount { get; private set; }

    /// <summary>How many have been let go, because the tag or the body went away.</summary>
    /// <remarks>
    ///     ⚠ <b>The tag going away is not the same as the body dying</b>, and both have to be handled
    ///     here. <c>LagCompensator.Capture</c> drops a body whose handle the physics world no longer
    ///     knows — that is the destroyed case, and it is already right. What it cannot see is an
    ///     entity that is alive and has had <see cref="LagCompensated" /> removed: a spectator, a
    ///     corpse, a vehicle nobody is in. Left to the compensator that body would be captured for
    ///     the rest of the match.
    /// </remarks>
    public long ReleasedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Capture(context.World, clock.Current);

        return dependency;
    }

    /// <summary>Reconciles what is tracked with what is tagged, then records this tick.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="at">The tick being recorded.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <exception cref="InvalidOperationException">A rewind is in progress. See the type's remarks.</exception>
    public void Capture(World world, Tick at) {
        ArgumentNullException.ThrowIfNull(world);

        live.Clear();

        foreach (var chunk in world.Chunks(compensated)) {
            var bodies = chunk.ReadValues<PhysicsBody>();

            for (var index = 0; index < chunk.Count; index++) {
                var body = bodies[index].Handle;

                if (body.IsNone) {
                    // The bridge has not built the body yet. Tracking a none-handle would put an
                    // entry in the ring that never fills and never expires.
                    continue;
                }

                live.Add(body.Value);

                if (compensator.IsTracked(body)) {
                    continue;
                }

                compensator.Track(body);
                tracked.Add(body.Value);
                AdoptedCount++;
            }
        }

        Release();
        compensator.Capture(at);
        CapturedTicks++;
    }

    void Release() {
        dropped.Clear();

        foreach (var value in tracked) {
            if (!live.Contains(value)) {
                dropped.Add(value);
            }
        }

        foreach (var value in dropped) {
            tracked.Remove(value);

            if (compensator.Forget(new(value))) {
                ReleasedCount++;
            }
        }
    }
}
