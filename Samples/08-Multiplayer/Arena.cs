// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Net.Motion;
using Vixen.Net.Replication;
using Vixen.Net.Rpc;
using Vixen.Net.Sessions;
using Vixen.Net.Time;

namespace Vixen.Samples.Multiplayer;

/// <summary>One fighter, as the server holds it.</summary>
internal sealed class Fighter {
    /// <summary>Whose it is.</summary>
    public required PlayerId Player { get; init; }

    /// <summary>What the network calls it.</summary>
    public required NetworkId Id { get; init; }

    /// <summary>What this world calls it.</summary>
    public required Entity Entity { get; init; }

    /// <summary>Where its remote calls arrive and leave from.</summary>
    public required AvatarController Controller { get; init; }

    /// <summary>The last direction its owner asked for.</summary>
    public Vector3 Intent { get; set; }

    /// <summary>The last yaw its owner asked for, in radians.</summary>
    public float Facing { get; set; }

    /// <summary>Whether the trigger was pulled since the last step.</summary>
    public bool Firing { get; set; }

    /// <summary>Ticks left before this fighter comes back, or zero if it is alive.</summary>
    public int RespawnIn { get; set; }

    /// <summary>How many ticks since anything was heard from its owner.</summary>
    public int Silent { get; set; }
}

/// <summary>The game, on the server, and nowhere else.</summary>
/// <remarks>
///     <para>
///         Server-authoritative in the only sense that means anything: this is the only code that
///         writes a position, a health or a score. A client's copy is whatever the last snapshot
///         said, and its input is a request.
///     </para>
///     <para>
///         <b>Where lag compensation would go, and does not.</b> <see cref="Resolve" /> casts its ray
///         against where the fighters are <i>now</i>. The shooter aimed at where they saw them, which
///         is their interpolation delay plus half their round trip in the past — so at 100 ms and 6
///         m/s a crossing target is about 60 cm from where the shot is judged, and a fast player
///         misses shots they saw land. Fixing it means keeping a ring of positions keyed by
///         <see cref="Tick" /> and rewinding to the shooter's tick before the cast, which is Phase 9's
///         one deferred item: it rewinds colliders, and <c>Vixen.Physics</c> is Phase 8. The tick
///         history it needs is keyed by a type that already exists, and this method is the one that
///         would change.
///     </para>
/// </remarks>
internal sealed class Arena {
    /// <summary>How far from the middle a fighter may get.</summary>
    public const float Radius = 40f;

    /// <summary>Metres a second, at full input.</summary>
    public const float Speed = 6f;

    /// <summary>How far a shot carries.</summary>
    public const float ShotRange = 25f;

    /// <summary>How far off the line of the shot still counts as a hit.</summary>
    public const float ShotRadius = 1.5f;

    /// <summary>What one hit costs. Three of them is a kill.</summary>
    public const byte ShotDamage = 34;

    /// <summary>What a fighter starts with, and comes back with.</summary>
    public const byte FullHealth = 100;

    /// <summary>How long a fighter stays down.</summary>
    public const int RespawnTicks = 30;

    /// <summary>
    ///     How long a fighter keeps moving after its owner stops asking it to.
    /// </summary>
    /// <remarks>
    ///     Half a second, and it is a rule rather than a convenience. Input is unreliable and
    ///     supersedes itself, so "keep going until told otherwise" would mean a player whose
    ///     connection died mid-stride walks into the sea, and a player whose last three packets were
    ///     lost overshoots by a fifth of a second. Intent expires; the server is the one holding the
    ///     stopwatch.
    /// </remarks>
    public const int InputTimeoutTicks = 15;

    readonly World world;
    readonly NetworkIdAllocator ids;
    readonly ReplicationServer replication;
    readonly RpcRouter router;
    readonly float step;
    readonly Dictionary<uint, Fighter> byNetworkId = [];
    readonly Dictionary<uint, Fighter> byPlayer = [];
    readonly List<Fighter> fighters = [];

    int spawnIndex;

    /// <summary>Everybody in the match.</summary>
    public IReadOnlyList<Fighter> Fighters => fighters;

    /// <summary>How many shots have been resolved.</summary>
    public int ShotsFired { get; private set; }

    /// <summary>How many of them hit something.</summary>
    public int ShotsHit { get; private set; }

    /// <summary>How many fighters have died.</summary>
    public int Deaths { get; private set; }

    /// <summary>Creates the game.</summary>
    /// <param name="world">The server's world.</param>
    /// <param name="ids">Where networked ids come from.</param>
    /// <param name="replication">What is told when an entity stops existing.</param>
    /// <param name="router">Where remote calls arrive and leave from.</param>
    /// <param name="rate">How long one tick is worth.</param>
    public Arena(
        World world,
        NetworkIdAllocator ids,
        ReplicationServer replication,
        RpcRouter router,
        TickRate rate
    ) {
        this.world = world;
        this.ids = ids;
        this.replication = replication;
        this.router = router;
        step = (float)rate.Duration.TotalSeconds;
    }

    /// <summary>Puts a player into the match.</summary>
    /// <param name="player">Who joined.</param>
    /// <returns>Their fighter.</returns>
    public Fighter Spawn(PlayerId player) {
        var id = ids.Next();
        var team = (byte)(fighters.Count % 2);

        var entity = world.Create(
            id,
            new NetworkTransform { Position = SpawnPoint(), Rotation = Quaternion.Identity },
            new Combatant { Owner = player.Value, Team = team },
            new Vitals { Health = FullHealth }
        );

        var controller = new AvatarController(id, router, this);
        router.Register(id, controller);

        // Ownership is what makes RequireOwnership mean anything, and it is set here rather than
        // being implied by who the entity was spawned for: the two are the same today and are not
        // the same the moment a vehicle changes hands.
        router.Ownership.SetOwner(id, player);

        var fighter = new Fighter {
            Player = player,
            Id = id,
            Entity = entity,
            Controller = controller
        };

        fighters.Add(fighter);
        byNetworkId[id.Value] = fighter;
        byPlayer[player.Value] = fighter;

        return fighter;
    }

    /// <summary>Takes a player out of the match.</summary>
    /// <param name="player">Who left.</param>
    /// <returns>Whether they were in it.</returns>
    /// <remarks>
    ///     Four things have to be told, and forgetting any one of them is a leak that only shows up
    ///     after an hour: the world, the replicator's captured state, the router's dispatch table,
    ///     and the ownership map. The clients are told by omission — an entity the interest resolver
    ///     stops returning is one the snapshot lists as removed.
    /// </remarks>
    public bool Remove(PlayerId player) {
        if (!byPlayer.Remove(player.Value, out var fighter)) {
            return false;
        }

        fighters.Remove(fighter);
        byNetworkId.Remove(fighter.Id.Value);

        if (world.IsAlive(fighter.Entity)) {
            world.Destroy(fighter.Entity);
        }

        replication.Despawn(fighter.Id);
        router.Forget(fighter.Id);
        router.Ownership.Forget(fighter.Id);
        replication.Forget(player);

        return true;
    }

    /// <summary>Takes a movement request. Called from the generated dispatch, on the server.</summary>
    /// <param name="id">Whose.</param>
    /// <param name="x">Sideways.</param>
    /// <param name="z">Forwards.</param>
    /// <param name="facing">The yaw they are looking along.</param>
    public void Steer(NetworkId id, float x, float z, float facing) {
        if (!byNetworkId.TryGetValue(id.Value, out var fighter)) {
            return;
        }

        // Clamped rather than trusted. The quantized range already bounds each component, but the
        // pair of them is a diagonal that is longer than one — which is the oldest speed hack there
        // is, and it does not need a modified client to exploit.
        var intent = new Vector3(x, 0f, z);

        if (intent.LengthSquared() > 1f) {
            intent = Vector3.Normalize(intent);
        }

        fighter.Intent = intent;
        fighter.Facing = facing;
        fighter.Silent = 0;
    }

    /// <summary>Takes a shot. Called from the generated dispatch, on the server.</summary>
    /// <param name="id">Who fired.</param>
    /// <param name="shooter">Who the router says asked, which is not what the packet said.</param>
    public void Fire(NetworkId id, PlayerId shooter) {
        if (byNetworkId.TryGetValue(id.Value, out var fighter) && fighter.Player == shooter) {
            fighter.Firing = true;
        }
    }

    /// <summary>Runs one tick of the game.</summary>
    public void Step() {
        Move();
        Resolve();
        Respawn();
    }

    void Move() {
        foreach (var fighter in fighters) {
            if (fighter.RespawnIn > 0) {
                continue;
            }

            if (++fighter.Silent > InputTimeoutTicks) {
                fighter.Intent = Vector3.Zero;
            }

            var position = world.Read<NetworkTransform>(fighter.Entity).Position + (fighter.Intent * Speed * step);

            if (position.LengthSquared() > Radius * Radius) {
                position = Vector3.Normalize(position) * Radius;
            }

            var rotation = Quaternion.FromAxisAngle(Vector3.UnitY, fighter.Facing);

            if (position == world.Read<NetworkTransform>(fighter.Entity).Position
                && rotation == world.Read<NetworkTransform>(fighter.Entity).Rotation) {
                // Nothing moved, so nothing is written. Writing the same value back would mark the
                // chunk changed and put this fighter in every capture for the rest of the match,
                // which is how a change-version filter is turned back into a full state sync by
                // accident.
                continue;
            }

            // Get rather than Read: this is the write that marks the chunk, and it is the whole
            // reason the capture below is O(what moved) rather than O(the match).
            ref var transform = ref world.Get<NetworkTransform>(fighter.Entity);
            transform.Position = position;
            transform.Rotation = rotation;
        }
    }

    void Resolve() {
        foreach (var shooter in fighters) {
            if (!shooter.Firing) {
                continue;
            }

            shooter.Firing = false;

            if (shooter.RespawnIn > 0) {
                continue;
            }

            ShotsFired++;

            var from = world.Read<NetworkTransform>(shooter.Entity).Position;
            var along = new Vector3(MathF.Sin(shooter.Facing), 0f, MathF.Cos(shooter.Facing));
            var victim = Nearest(shooter, from, along);

            if (victim is null) {
                continue;
            }

            ShotsHit++;
            Damage(shooter, victim);
        }
    }

    Fighter? Nearest(Fighter shooter, in Vector3 from, in Vector3 along) {
        Fighter? best = null;
        var bestDistance = float.MaxValue;

        foreach (var candidate in fighters) {
            if (ReferenceEquals(candidate, shooter) || candidate.RespawnIn > 0) {
                continue;
            }

            var to = world.Read<NetworkTransform>(candidate.Entity).Position - from;
            var distance = Vector3.Dot(to, along);

            if (distance <= 0f || distance > ShotRange || distance >= bestDistance) {
                continue;
            }

            if (Vector3.DistanceSquared(to, along * distance) > ShotRadius * ShotRadius) {
                continue;
            }

            best = candidate;
            bestDistance = distance;
        }

        return best;
    }

    void Damage(Fighter shooter, Fighter victim) {
        ref var vitals = ref world.Get<Vitals>(victim.Entity);
        var fatal = vitals.Health <= ShotDamage;

        vitals.Health = fatal ? (byte)0 : (byte)(vitals.Health - ShotDamage);

        if (fatal) {
            vitals.Deaths++;
            victim.RespawnIn = RespawnTicks;
            victim.Intent = Vector3.Zero;
            Deaths++;

            ref var credit = ref world.Get<Vitals>(shooter.Entity);
            credit.Score++;
        }

        // The effect, not the fact: the damage itself is in the Vitals above and arrives however
        // long it takes. This is the spark, and it is allowed to be lost.
        victim.Controller.Rpc.Hit(shooter.Id.Value, fatal);
    }

    void Respawn() {
        foreach (var fighter in fighters) {
            if (fighter.RespawnIn == 0 || --fighter.RespawnIn > 0) {
                continue;
            }

            ref var transform = ref world.Get<NetworkTransform>(fighter.Entity);
            transform.Position = SpawnPoint();
            transform.Rotation = Quaternion.Identity;

            // Put somewhere rather than moved there. Without this the clients interpolate across
            // the arena — a two-second glide from where they died to where they came back.
            transform.TeleportCount++;

            ref var vitals = ref world.Get<Vitals>(fighter.Entity);
            vitals.Health = FullHealth;
        }
    }

    Vector3 SpawnPoint() {
        var angle = spawnIndex++ * (MathF.Tau / 8f);

        return new(MathF.Cos(angle) * (Radius * 0.7f), 0f, MathF.Sin(angle) * (Radius * 0.7f));
    }
}
