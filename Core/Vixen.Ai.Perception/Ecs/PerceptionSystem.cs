// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;

namespace Vixen.Ai.Perception.Ecs;

/// <summary>Works out what every listener can notice, as cheaply as the three bounds allow.</summary>
/// <remarks>
///     <para>
///         <b>The order of the tests is the design</b>, and doc 37 § D15 makes all three mandatory
///         rather than tuning: a <see cref="StimuliGrid" /> instead of a scan, a per-listener update
///         rate with a random deviation, and distance-based rate reduction. Within a pass the
///         cheapest test comes first — the filter, then the radius, then the cone, then the trace —
///         because the last one is a raycast and everything above it exists to stop it happening.
///     </para>
///     <para>
///         Runs in <see cref="SystemPhase.Update" /> and before <see cref="AiSystem" />, so a
///         behaviour tree steps on this frame's perception rather than last frame's. That matters
///         more than it sounds: the two systems are the abort loop — a sense writes a key, the key
///         has observers, and an observer interrupts a branch — and running them the other way round
///         puts a frame of lag inside it.
///     </para>
///     <para>
///         ⚠ <b>A listener is not required to be an <c>AiAgent</c>.</b> The blackboard binding is the
///         only part that needs one, and it is skipped when <see cref="Agents" /> is unset or the
///         entity has no agent — so a camera, a trap or a trigger can perceive without deciding.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Update)]
[UpdateBefore(typeof(AiSystem))]
public sealed class PerceptionSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription listenerQuery = new QueryDescription().WithAll<AiPerception, LocalTransform>();
    readonly QueryDescription sourceQuery = new QueryDescription().WithAll<AiStimuliSource, LocalTransform>();

    // Per-slot, keyed on AiPerception.ListenerIndex.
    readonly List<Entity> owners = [];
    readonly List<PerceivedTargets?> perceived = [];
    readonly List<long> seen = [];
    readonly List<uint> passes = [];

    // The last event sequence each listener consumed, rather than the clock it last sensed at. See
    // StimulusEvent.Sequence for why a clock cannot do this job.
    readonly List<long> consumed = [];
    readonly Stack<int> freeSlots = new();

    // Rebuilt every frame from the queries. Parallel arrays rather than a struct list, because the
    // grid indexes positions and the sense tests index the other two, and splitting them keeps the
    // broad phase's hot loop reading one contiguous array of twelve-byte values.
    readonly List<Entity> sourceEntities = [];
    readonly List<Vector3> sourcePoints = [];
    readonly List<AiStimuliSource> sourceData = [];

    // Only the event senses need it — an event carries a source entity and nothing about it, and
    // asking the filter about a team means finding that entity's row. A linear scan here would be
    // per event per listener, which is the one place in this file where a dictionary is cheaper.
    readonly Dictionary<Entity, int> sourceLookup = [];

    readonly List<Entity> listenerEntities = [];
    readonly List<Vector3> listenerPoints = [];
    readonly List<int> listenerSlots = [];
    readonly List<byte> listenerTeams = [];

    readonly StimuliGrid sources = new();
    readonly StimuliGrid neighbours = new();
    readonly List<int> candidates = [];
    readonly List<int> allies = [];
    readonly List<StimulusEvent> events = [];

    long tick;
    long sequence;
    float clock;
    bool relaying;

    /// <summary>Creates the system.</summary>
    /// <param name="configs">The configurations its listeners may name, or null for an empty library.</param>
    public PerceptionSystem(PerceptionLibrary? configs = null) => Configs = configs ?? new PerceptionLibrary();

    /// <summary>What its listeners sense with, by index.</summary>
    public PerceptionLibrary Configs { get; }

    /// <summary>How often each listener senses. Replaceable, because what an agent is worth is a game's decision.</summary>
    public IPerceptionGovernor Governor { get; set; } = FixedRateGovernor.Instance;

    /// <summary>What stops sight. <see cref="OpenSightlines" /> until a game hands it a physics world.</summary>
    public IOcclusionTester Occlusion { get; set; } = OpenSightlines.Instance;

    /// <summary>Where the agents that matter are — usually the player.</summary>
    /// <remarks>
    ///     Read by <see cref="DistanceLodGovernor" />. Unset means every listener is treated as being
    ///     at the focus, which is what makes "nobody told me where the player is" mean full rate
    ///     rather than the slowest band.
    /// </remarks>
    public Vector3? Focus { get; set; }

    /// <summary>Where the blackboards are, for <see cref="PerceptionConfig.Binding" />.</summary>
    /// <remarks>Null is a supported configuration: perception then writes no keys and only its own lists.</remarks>
    public AiSystem? Agents { get; set; }

    /// <summary>Whether to use the <see cref="StimuliGrid" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Off is for measuring, not for shipping.</b> It exists so that P3's exit criterion can
    ///     run the same frame both ways and report both numbers, which is a claim that has to be
    ///     re-checkable rather than a paragraph in a document.
    /// </remarks>
    public bool BroadPhase { get; set; } = true;

    /// <summary>How long an event stays available for a listener that has not sensed yet, in seconds.</summary>
    /// <remarks>
    ///     Wants to be at least the slowest interval any listener runs at, or the slowest listeners
    ///     miss events the fast ones caught — which reads in-game as distant guards being deaf.
    /// </remarks>
    public float EventMemory { get; set; } = 1f;

    /// <summary>What the last frame cost.</summary>
    public PerceptionStats LastStats { get; private set; }

    /// <summary>How many listeners have joined.</summary>
    public int Population { get; private set; }

    /// <summary>The clock the stamps and ages are against, in seconds since the system started.</summary>
    /// <remarks>
    ///     Accumulated from the frame delta rather than read off <c>GameTime.Total</c>, so that a test
    ///     or a tool stepping the system by hand gets ages that agree with the deltas it passed in.
    /// </remarks>
    public float Clock => clock;

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Write<AiPerception>()
        .Read<AiStimuliSource>()
        .Read<LocalTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Step(context.World, context.Time);

        return dependency;
    }

    /// <summary>Runs one frame against a world.</summary>
    /// <param name="world">The world.</param>
    /// <param name="time">The clock.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test or a tool can sense without standing up a runner.</remarks>
    public void Step(World world, GameTime time) {
        ArgumentNullException.ThrowIfNull(world);

        var delta = time.DeltaSeconds;

        clock += delta;

        Join(world);
        Gather(world);
        Advance(world, delta);
        Forget();
        Reap(world);
        tick++;
    }

    /// <summary>What a listener knows, or null if it has not joined.</summary>
    /// <param name="listener">The listener's component.</param>
    /// <returns>Its perceived list.</returns>
    public PerceivedTargets? PerceivedBy(in AiPerception listener) =>
        (uint)listener.ListenerIndex < (uint)perceived.Count ? perceived[listener.ListenerIndex] : null;

    /// <summary>What a listener knows, by entity.</summary>
    /// <param name="world">The world.</param>
    /// <param name="listener">The entity.</param>
    /// <returns>Its perceived list, or null if it is not a listener.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public PerceivedTargets? PerceivedBy(World world, Entity listener) {
        ArgumentNullException.ThrowIfNull(world);

        return world.IsAlive(listener) && world.Has<AiPerception>(listener)
            ? PerceivedBy(world.Get<AiPerception>(listener))
            : null;
    }

    /// <summary>Where an entity is, as this system reads it.</summary>
    /// <param name="world">The world.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>Its position, or the origin if it has no transform.</returns>
    /// <remarks>
    ///     ⚠ <b><c>LocalTransform</c> rather than <c>WorldTransform</c>, and the nodes read it through
    ///     here so that they cannot disagree with the pass.</b> This system runs before the transform
    ///     pass — it has to, so that a sense's write reaches a tree in the same frame — which means
    ///     <c>WorldTransform</c> is last frame's. A parented listener therefore senses from its local
    ///     position, which is the one limitation of running early and it is worth naming.
    /// </remarks>
    public static Vector3 PositionOf(World world, Entity entity) {
        ArgumentNullException.ThrowIfNull(world);

        return world.IsAlive(entity) && world.Has<LocalTransform>(entity)
            ? world.Get<LocalTransform>(entity).Position
            : Vector3.Zero;
    }

    /// <summary>Makes a noise anything in range can hear.</summary>
    /// <param name="source">Who made it.</param>
    /// <param name="at">Where.</param>
    /// <param name="loudness">How loud, as a multiple of the hearing sense's range.</param>
    /// <remarks>What <c>MakeNoiseTask</c> calls, and what a footstep, a gunshot or a door calls directly.</remarks>
    public void ReportNoise(Entity source, Vector3 at, float loudness = 1f) =>
        events.Add(new(AiSense.Hearing, source, Entity.Null, at, loudness, clock, sequence++));

    /// <summary>Tells one listener it was hurt.</summary>
    /// <param name="source">Who did it.</param>
    /// <param name="victim">Who it happened to.</param>
    /// <param name="at">Where the hit landed.</param>
    /// <param name="amount">How much.</param>
    /// <remarks>
    ///     ⚠ There is no radius and no filter beyond the threshold. Damage is the one sense that works
    ///     from behind, out of range and through a wall — which is the entire reason for having it,
    ///     and why an agent shot in the back turns round instead of standing there.
    /// </remarks>
    public void ReportDamage(Entity source, Entity victim, Vector3 at, float amount = 1f) =>
        events.Add(new(AiSense.Damage, source, victim, at, amount, clock, sequence++));

    /// <summary>Assigns slots to listeners that have not got one.</summary>
    void Join(World world) {
        foreach (var chunk in world.Chunks(listenerQuery)) {
            var values = chunk.Values<AiPerception>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                ref var listener = ref values[index];

                if (Live(in listener, entities[index])) {
                    seen[listener.ListenerIndex] = tick;

                    continue;
                }

                var slot = freeSlots.Count > 0 ? freeSlots.Pop() : NewSlot();

                listener.ListenerIndex = slot;

                perceived[slot] ??= new PerceivedTargets();
                perceived[slot]!.Clear();
                owners[slot] = entities[index];
                seen[slot] = tick;
                passes[slot] = 0;

                // Behind everything still in the buffer, so a listener that joins a frame after a
                // gunshot still hears it. The alternative — starting at the current sequence — makes
                // whether a spawned guard notices the fight it spawned into depend on which system
                // ran first that frame.
                consumed[slot] = -1;

                // ⚠ Spread on join rather than started at zero. A wave of guards spawned in one frame
                // would otherwise share a phase for ever — every one of them sensing on the same tick,
                // which is a frame that costs the whole population and a schedule whose average says
                // nothing about its worst case.
                listener.Countdown = listener.Config < Configs.Count
                    ? Configs[listener.Config].Interval
                    * AgentRandom.Value(entities[index], AgentRandom.SeedOf(entities[index]), 0x5E4)
                    : 0f;

                Population++;
            }
        }
    }

    /// <summary>Rebuilds the source and listener tables, and the grids over them.</summary>
    void Gather(World world) {
        sourceEntities.Clear();
        sourcePoints.Clear();
        sourceData.Clear();
        sourceLookup.Clear();
        listenerEntities.Clear();
        listenerPoints.Clear();
        listenerSlots.Clear();
        listenerTeams.Clear();
        relaying = false;

        foreach (var chunk in world.Chunks(sourceQuery)) {
            var values = chunk.ReadValues<AiStimuliSource>();
            var transforms = chunk.ReadValues<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!values[index].Enabled || values[index].Senses == SenseMask.None) {
                    continue;
                }

                sourceLookup[entities[index]] = sourceEntities.Count;
                sourceEntities.Add(entities[index]);
                sourcePoints.Add(transforms[index].Position);
                sourceData.Add(values[index]);
            }
        }

        var widest = 0f;

        foreach (var chunk in world.Chunks(listenerQuery)) {
            var values = chunk.ReadValues<AiPerception>();
            var transforms = chunk.ReadValues<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!values[index].Enabled || values[index].Config >= Configs.Count) {
                    continue;
                }

                var config = Configs[values[index].Config];

                widest = MathF.Max(widest, config.MaxRadius);
                relaying |= config.Senses.Has(AiSense.Team);

                listenerEntities.Add(entities[index]);
                listenerPoints.Add(transforms[index].Position);
                listenerSlots.Add(values[index].ListenerIndex);
                listenerTeams.Add(values[index].Team);
            }
        }

        // The cell wants to be about the query radius: much smaller and a query walks hundreds of
        // empty cells, much larger and it returns most of the level. Clamped so that a config with a
        // silly radius cannot produce a grid with one cell in it or a million.
        var cell = Math.Clamp(widest, 2f, 128f);

        if (BroadPhase) {
            sources.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(sourcePoints), cell);
        } else {
            sources.Clear();
        }

        if (relaying) {
            neighbours.Build(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(listenerPoints), cell);
        } else {
            neighbours.Clear();
        }
    }

    /// <summary>Counts every listener down, and senses for the ones whose turn it is.</summary>
    void Advance(World world, float delta) {
        var stats = new PerceptionStats(listenerEntities.Count, sourceEntities.Count, 0, 0, 0, 0, 0, 0);

        foreach (var chunk in world.Chunks(listenerQuery)) {
            var values = chunk.Values<AiPerception>();
            var transforms = chunk.ReadValues<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                ref var listener = ref values[index];

                if (!listener.Enabled || listener.Config >= Configs.Count) {
                    continue;
                }

                listener.Countdown -= delta;

                if (listener.Countdown > 0f) {
                    continue;
                }

                var position = transforms[index].Position;

                // Refilled before the pass runs, so an exception in a game's own filter or binding
                // cannot leave a listener with a negative countdown that senses every frame for ever.
                listener.Countdown = Jittered(in listener, entities[index], position);

                stats = Sense(world, entities[index], in listener, in transforms[index], stats);
            }
        }

        LastStats = stats;
    }

    /// <summary>One listener's pass.</summary>
    PerceptionStats Sense(
        World world,
        Entity entity,
        ref readonly AiPerception listener,
        ref readonly LocalTransform transform,
        PerceptionStats stats
    ) {
        var config = Configs[listener.Config];
        var slot = listener.ListenerIndex;
        var board = perceived[slot]!;
        var position = transform.Position;
        var me = new PerceptionParticipant(entity, listener.Team, position);
        var since = consumed[slot];

        board.BeginPass();
        passes[slot]++;
        consumed[slot] = sequence - 1;
        stats = stats with { Passes = stats.Passes + 1 };

        var radius = config.MaxRadius;

        if (radius > 0f) {
            int examined;
            var cells = 0;

            if (BroadPhase) {
                examined = sources.Query(position, radius, candidates, out cells);
            } else {
                examined = Scan(position, radius);
            }

            stats = stats with {
                Examined = stats.Examined + examined,
                Cells = stats.Cells + cells,
                Candidates = stats.Candidates + candidates.Count
            };

            foreach (var candidate in candidates) {
                stats = Near(world, config, board, in me, in transform, candidate, stats);
            }
        }

        Heard(config, board, in me, since);
        Relayed(config, board, in me, slot);
        board.Expire(clock, config.Memory);
        Bind(world, entity, config, board, position);

        return stats;
    }

    /// <summary>The senses that are a property of where something is: sight and touch.</summary>
    PerceptionStats Near(
        World world,
        PerceptionConfig config,
        PerceivedTargets board,
        ref readonly PerceptionParticipant me,
        ref readonly LocalTransform transform,
        int candidate,
        PerceptionStats stats
    ) {
        var data = sourceData[candidate];
        var them = new PerceptionParticipant(sourceEntities[candidate], data.Team, sourcePoints[candidate]);
        var offset = them.Position - me.Position;
        var distance = offset.Length();

        if (config.Senses.Has(AiSense.Touch)
            && data.Senses.Has(AiSense.Touch)
            && distance <= config.Touch.Radius
            && config.Filter.CanPerceive(in me, in them, AiSense.Touch)) {
            board.Report(them.Entity, AiSense.Touch, them.Position, data.Strength, clock, config.MaxPerceived);
        }

        if (!config.Senses.Has(AiSense.Sight)
            || !data.Senses.Has(AiSense.Sight)
            || !config.Filter.CanPerceive(in me, in them, AiSense.Sight)) {
            return stats;
        }

        // The lose-sight radius, and the whole of doc 37 § D15's second warning: the radius that keeps
        // a target is larger than the radius that finds it, so a target on the boundary is not found
        // and lost several times a second.
        var seeing = board.WasPerceived(them.Entity, AiSense.Sight);

        if (distance > config.Sight.RadiusFor(seeing)) {
            return stats;
        }

        stats = stats with { ConeTests = stats.ConeTests + 1 };

        var facing = Facing(in transform);

        if (distance > 1e-4f && Vector3.Dot(offset / distance, facing) < config.Sight.ConeCosine) {
            return stats;
        }

        if (config.Sight.Occlusion) {
            var eye = me.Position + (Vector3.Up * config.Sight.EyeHeight);
            var aim = them.Position + (Vector3.Up * sourceData[candidate].Height);

            stats = stats with { Traces = stats.Traces + 1 };

            if (!Occlusion.IsClear(world, me.Entity, them.Entity, eye, aim)) {
                return stats;
            }
        }

        board.Report(them.Entity, AiSense.Sight, them.Position, data.Strength, clock, config.MaxPerceived);

        return stats;
    }

    /// <summary>The senses that are events: hearing and damage.</summary>
    void Heard(PerceptionConfig config, PerceivedTargets board, ref readonly PerceptionParticipant me, long since) {
        foreach (var stimulus in events) {
            // Strictly newer, so an event already consumed is not consumed again — which would make a
            // single gunshot read as still being heard a pass later.
            if (stimulus.Sequence <= since || stimulus.Source == me.Entity || !config.Senses.Has(stimulus.Sense)) {
                continue;
            }

            if (stimulus.Sense == AiSense.Damage) {
                if (stimulus.Target != me.Entity || stimulus.Strength < config.Damage.Threshold) {
                    continue;
                }
            } else if ((stimulus.Position - me.Position).Length() > config.Hearing.Range * MathF.Max(0f, stimulus.Strength)) {
                continue;
            }

            var them = new PerceptionParticipant(stimulus.Source, TeamOf(stimulus.Source), stimulus.Position);

            if (!config.Filter.CanPerceive(in me, in them, stimulus.Sense)) {
                continue;
            }

            board.Report(
                stimulus.Source,
                stimulus.Sense,
                stimulus.Position,
                stimulus.Strength,
                stimulus.Stamp,
                config.MaxPerceived
            );
        }
    }

    /// <summary>What an ally within range has just noticed.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A relay is never relayed.</b> Only targets an ally perceived <i>itself</i> are
    ///         copied, so a line of guards cannot pass a sighting down the level one hop a pass —
    ///         which looks like the whole map waking up several seconds after one guard saw something,
    ///         with no guard anywhere having seen it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One target per ally, the freshest, and that is a bound rather than a
    ///         simplification.</b> Copying an ally's whole current list makes the relay cost
    ///         <c>listeners × allies × targets</c>, which measured at more than twice the entire rest
    ///         of the pass at five hundred agents — the one place the three bounds in doc 37 § D15 did
    ///         not reach. It is also the better model: an ally shouts <i>"contact, north"</i>, which is
    ///         one thing, rather than synchronising its memory with everybody in earshot.
    ///     </para>
    /// </remarks>
    void Relayed(PerceptionConfig config, PerceivedTargets board, ref readonly PerceptionParticipant me, int slot) {
        if (!config.Senses.Has(AiSense.Team) || !relaying) {
            return;
        }

        neighbours.Query(me.Position, config.Team.Range, allies, out _);

        foreach (var index in allies) {
            var other = listenerSlots[index];

            if (other == slot || listenerTeams[index] != me.Team || (uint)other >= (uint)perceived.Count) {
                continue;
            }

            if (perceived[other] is not { } theirs) {
                continue;
            }

            if (!theirs.TryShout(out var target) || target.Source == me.Entity) {
                continue;
            }

            var them = new PerceptionParticipant(target.Source, TeamOf(target.Source), target.LastKnownLocation);

            if (config.Filter.CanPerceive(in me, in them, AiSense.Team)) {
                board.Report(
                    target.Source,
                    AiSense.Team,
                    target.LastKnownLocation,
                    target.Strength,
                    target.Stamp,
                    config.MaxPerceived
                );
            }
        }
    }

    /// <summary>Writes the pass into the agent's blackboard, if it has one and a binding was configured.</summary>
    void Bind(World world, Entity entity, PerceptionConfig config, PerceivedTargets board, Vector3 position) {
        if (config.Binding is not { } binding || Agents is not { } agents || !world.Has<AiAgent>(entity)) {
            return;
        }

        if (agents.BlackboardOf(world.Get<AiAgent>(entity)) is { } blackboard) {
            binding.Write(board, blackboard, position, clock);
        }
    }

    /// <summary>Drops events nobody can still be waiting for.</summary>
    void Forget() {
        for (var index = events.Count - 1; index >= 0; index--) {
            if (clock - events[index].Stamp > EventMemory) {
                events.RemoveAt(index);
            }
        }
    }

    /// <summary>Releases the slots of listeners that are gone.</summary>
    void Reap(World world) {
        for (var slot = 0; slot < owners.Count; slot++) {
            if (owners[slot].IsNull || seen[slot] == tick) {
                continue;
            }

            if (world.IsAlive(owners[slot]) && world.Has<AiPerception>(owners[slot])) {
                continue;
            }

            owners[slot] = Entity.Null;
            perceived[slot]?.Clear();
            freeSlots.Push(slot);
            Population--;
        }
    }

    /// <summary>Every source, distance-tested by hand. What <see cref="BroadPhase" /> being off means.</summary>
    int Scan(Vector3 position, float radius) {
        var squared = radius * radius;

        candidates.Clear();

        for (var index = 0; index < sourcePoints.Count; index++) {
            if ((sourcePoints[index] - position).LengthSquared() <= squared) {
                candidates.Add(index);
            }
        }

        return sourcePoints.Count;
    }

    float Jittered(ref readonly AiPerception listener, Entity entity, Vector3 position) {
        var config = Configs[listener.Config];
        var interval = Interval(in listener, entity, position);
        var deviation = MathF.Max(0f, config.RandomDeviation);

        if (deviation <= 0f) {
            return interval;
        }

        var jitter = AgentRandom.Range(
            entity,
            AgentRandom.SeedOf(entity),
            passes[listener.ListenerIndex],
            -deviation,
            deviation
        );

        // Floored rather than clamped to the interval, because a deviation larger than the interval is
        // a configuration somebody meant — "roughly every half second, give or take a second" — and a
        // countdown of zero or less would sense every frame.
        return MathF.Max(1e-3f, interval + jitter);
    }

    float Interval(ref readonly AiPerception listener, Entity entity, Vector3 position) {
        var config = Configs[listener.Config];
        var distance = Focus is { } focus ? (position - focus).Length() : 0f;

        return MathF.Max(1e-3f, Governor.IntervalFor(config, distance));
    }

    byte TeamOf(Entity source) => sourceLookup.TryGetValue(source, out var index) ? sourceData[index].Team : (byte)0;

    static Vector3 Facing(ref readonly LocalTransform transform) {
        // A zeroed LocalTransform has a zero quaternion, which rotates every vector to nothing — so an
        // entity somebody built with `new()` would face nowhere and see nothing, silently.
        var rotation = transform.Rotation;

        return rotation == default ? Vector3.Forward : Quaternion.Transform(Vector3.Forward, rotation);
    }

    bool Live(ref readonly AiPerception listener, Entity entity) =>
        (uint)listener.ListenerIndex < (uint)owners.Count && owners[listener.ListenerIndex] == entity;

    int NewSlot() {
        owners.Add(Entity.Null);
        perceived.Add(null);
        seen.Add(-1);
        passes.Add(0);
        consumed.Add(-1);

        return owners.Count - 1;
    }
}
