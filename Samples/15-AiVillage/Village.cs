// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Ai.Nodes;
using Vixen.Ai.Nodes.Ecs;
using Vixen.Ai.Perception;
using Vixen.Ai.Perception.Ecs;
using Vixen.Ai.Perception.Sensors;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Frames;
using Vixen.Engine.Transforms;
using Vixen.Navigation;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Baking;
using Vixen.Navigation.Ecs;

namespace Vixen.Samples.AiVillage;

/// <summary>A thing worth scavenging. The sample's only game rule, and it is one component.</summary>
/// <remarks>
///     ⚠ <b><c>[Component]</c> without <c>[DataContract]</c>, because nothing places one in a
///     scene.</b> The village is built in code, so the serializer half of the pair would be a
///     declaration with no reader.
/// </remarks>
[Component]
public struct Scrap;

/// <summary>
///     Three agents, one of each planner, sharing every system — doc 37's claim, in a game.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is <c>VillageSampleTests</c>' fixture, moved into a <c>Samples/</c> project,
///         which is the thing doc 37 § P9 named as owed and did not build.</b> That phase's exit
///         criterion is met by a test — <i>"a sample as a <c>Samples/</c> project, and that is a
///         deviation worth naming"</i> — and its own note says a real sample <i>"remains a good
///         addition on top of this rather than instead of it"</i>. Until this existed,
///         <c>grep -rl "Vixen.Ai" Samples/</c> was empty: eleven ✅ rows in <c>docs/overview.md</c>
///         rested on a runtime half that had never executed outside a test fixture.
///     </para>
///     <para>
///         ⚠ <b>What is different here from the fixture is the thing worth having.</b> A test steps
///         three systems in a loop it wrote itself; this hands them to an <c>EngineLoop</c> and lets
///         the engine's own phases and <c>[UpdateBefore]</c> decide the order. That is the part
///         nobody had checked — <c>PerceptionSystem</c> declares
///         <c>[UpdateBefore(typeof(AiSystem))]</c> and no scheduler had ever been asked to honour
///         it, because every caller in the tree called <c>Step</c> by hand in the order it wanted.
///     </para>
///     <para>
///         ⚠ <b>One of everything else.</b> One <c>AiSystem</c>, one <c>AgentActionRegistry</c>, one
///         <c>BlackboardLayout</c>, one <c>PerceptionSystem</c> with one config, one
///         <c>SensorSet</c> and one navmesh. What differs between the guard, the villager and the
///         scavenger is the planner and nothing else — which is doc 37 § D2's whole payoff, and the
///         reason <see cref="Pause" /> is deliberately shared between the villager's utility set and
///         the scavenger's GOAP domain.
///     </para>
/// </remarks>
public sealed class Village {
    /// <summary>Where the villager runs to. A global sensor tells every agent about it.</summary>
    public static readonly Vector3 Refuge = new(4f, 0f, 34f);

    /// <summary>Where the scavenger takes what it finds.</summary>
    public static readonly Vector3 Depot = new(34f, 0f, 34f);

    /// <summary>And where it finds it.</summary>
    public static readonly Vector3 ScrapPile = new(34f, 0f, 6f);

    /// <summary>The guard's beat, walked as a ping-pong.</summary>
    public static readonly Vector3[] Beat = [
        new(8f, 0f, 8f),
        new(8f, 0f, 26f)
    ];

    readonly NavMesh mesh;

    // Reused: a snapshot is a picture rather than a view, so one buffer serves every reading.
    readonly AiAgentSnapshot snapshot = new();

    /// <summary>Builds the level, the systems and the three agents.</summary>
    /// <param name="world">The world to spawn into.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public Village(World world) {
        ArgumentNullException.ThrowIfNull(world);

        World = world;

        // ── The level ────────────────────────────────────────────────────────────────────────
        // Forty-eight metres of flat floor, baked at start-up from four vertices. A sample that
        // shipped a mesh would be a sample about the content pipeline.
        Vector3[] corners = [
            new(0f, 0f, 0f),
            new(0f, 0f, 48f),
            new(48f, 0f, 48f),
            new(48f, 0f, 0f)
        ];

        int[] triangles = [0, 1, 2, 0, 2, 3];

        mesh = new NavMesh(NavMeshParams.Single);
        mesh.AddTile(NavMeshBaker.Bake(corners, triangles, new() { AgentRadius = 0.6f })!);
        Navigation = new NavigationSystem(new Crowd(mesh));

        // ── One registry, and the actions every planner chooses out of ───────────────────────
        Registry = new AgentActionRegistry();
        Layout = new BlackboardLayoutBuilder()
            .Add("target", BlackboardValueType.Entity)
            .Add("seen", BlackboardValueType.Vector3)
            .Add("age", BlackboardValueType.Float)
            .Add("refuge", BlackboardValueType.Vector3)
            .Add("scrap", BlackboardValueType.Vector3)
            .Add("depot", BlackboardValueType.Vector3)
            .Build();

        Pause = Registry.Register("pause", new WaitTask(0.4f), WaitTask.StateSize);

        var chase = Registry.Register("chase", new MoveToTask(Key("target"), 2f, 1f), MoveToTask.StateSize);
        var flee = Registry.Register("flee", new MoveToTask(Key("refuge"), 2f, 2f), MoveToTask.StateSize);
        var collect = Registry.Register("collect", new MoveToTask(Key("scrap"), 2f, 2f), MoveToTask.StateSize);
        var deposit = Registry.Register("deposit", new MoveToTask(Key("depot"), 2f, 2f), MoveToTask.StateSize);
        var patrol = Registry.Register("patrol", new PatrolTask(1.5f), PatrolTask.StateSize);

        // ⚠ UnboundedGovernor, so every agent thinks every frame. Three agents is under any budget
        // the RoundRobinGovernor would impose, but saying so is the difference between a sample
        // whose timings are the engine's and one whose timings are an accident of the default.
        Agents = new AiSystem(Registry, Layout) { Governor = new UnboundedGovernor() };

        Perception = Perceive();
        Perception.Agents = Agents;

        // ── One sensor set, read by all three ────────────────────────────────────────────────
        // Two globals and one local, which is doc 37 § D13's argument in three lines: the refuge
        // and the depot are one query for the village, cached once a pass so two agents standing
        // together cannot disagree about where the refuge is; the nearest scrap is one query per
        // scavenger.
        Agents.Sensors = new SensorSet()
            .AddGlobalTarget(Key("refuge"), BlackboardKey.Invalid, Sensors.Landmark(Refuge))
            .AddGlobalTarget(Key("depot"), BlackboardKey.Invalid, Sensors.Landmark(Depot))
            .AddTarget(Key("scrap"), BlackboardKey.Invalid, WorldSensors.Nearest<Scrap>());

        Agents.Trees.Add(GuardTree(chase, patrol));
        Agents.Sets.Add(VillagerSet(flee));
        Agents.Domains.Add(ScavengerDomain(collect, deposit));

        // ── And the three of them ────────────────────────────────────────────────────────────
        Guard = Spawn(Beat[0], AiAgent.Thinking(0));
        World.Add(Guard, PatrolRoute.Of(PatrolMode.PingPong, Beat));

        Villager = Spawn(new(14f, 0f, 20f), AiAgent.Scoring(0));
        Scavenger = Spawn(new(28f, 0f, 20f), AiAgent.Planning(0));

        World.Create(LocalTransform.At(ScrapPile), new Scrap());

        // The one thing in the level that is not an agent and not scenery. Where it stands is what
        // the guard and the villager are deciding about.
        Intruder = World.Create(
            LocalTransform.At(Intrusion.Start),
            AiStimuliSource.Perceivable(team: 2, senses: SenseMask.Sight)
        );

        Script = new IntruderSystem(Intruder);
    }

    /// <summary>The world everything is in.</summary>
    public World World { get; }

    /// <summary>The one registry all three planners choose out of.</summary>
    public AgentActionRegistry Registry { get; }

    /// <summary>The one key table.</summary>
    public BlackboardLayout Layout { get; }

    /// <summary>The one system that steps every agent.</summary>
    public AiSystem Agents { get; }

    /// <summary>The one perception model.</summary>
    public PerceptionSystem Perception { get; }

    /// <summary>What walks the agents along the mesh once something has decided where to go.</summary>
    public NavigationSystem Navigation { get; }

    /// <summary>The wait shared by the villager's set and the scavenger's domain.</summary>
    public ushort Pause { get; }

    /// <summary>Runs a behaviour tree.</summary>
    public Entity Guard { get; }

    /// <summary>Scores a utility set.</summary>
    public Entity Villager { get; }

    /// <summary>Plans over a GOAP domain.</summary>
    public Entity Scavenger { get; }

    /// <summary>What the first two are deciding about.</summary>
    public Entity Intruder { get; }

    /// <summary>Hands the three systems to the engine.</summary>
    /// <param name="loop">The frame loop.</param>
    /// <exception cref="ArgumentNullException"><paramref name="loop" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Three <c>Add</c> calls and no ordering argument, which is the point of doing this in
    ///     a game rather than in a fixture.</b> <c>PerceptionSystem</c> carries
    ///     <c>[UpdateBefore(typeof(AiSystem))]</c> and both are in <c>SystemPhase.Update</c>; the
    ///     scheduler reads those and sorts them. Every existing caller of this stack called
    ///     <c>Step</c> by hand in the order it had decided on, so the declaration had never once
    ///     been the thing that put them in order.
    /// </remarks>
    public void Register(EngineLoop loop) {
        ArgumentNullException.ThrowIfNull(loop);

        loop.Add(Script);
        loop.Add(Perception);
        loop.Add(Agents);
        loop.Add(Navigation);
    }

    /// <summary>What walks the intruder, and the clock the sample's log is stamped with.</summary>
    public IntruderSystem Script { get; }

    /// <summary>Where the agent is.</summary>
    /// <param name="entity">Which one.</param>
    /// <returns>Its position.</returns>
    public Vector3 Where(Entity entity) => World.Read<LocalTransform>(entity).Position;

    /// <summary>What the agent is doing, by name.</summary>
    /// <param name="entity">Which one.</param>
    /// <returns>The action's name, or <see cref="Symbol.None" /> when there is no such agent.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Through <c>AiSnapshots</c> rather than off <c>AiAgent.Action</c>, and that is a
    ///         correction rather than a preference.</b> Reading the component directly works for a
    ///         utility agent and for a bare action and reports <i>nothing at all</i> for a behaviour
    ///         tree: the tree owns the running node, so a guard patrolling its beat quite correctly
    ///         leaves <c>Started</c> false on the component. The first draft of this sample logged
    ///         zero decisions for the guard and a hundred and twenty for the villager, which looked
    ///         like a broken tree and was a broken reader.
    ///     </para>
    ///     <para>
    ///         <c>AiAgentSnapshot</c> is doc 37 § P7's answer to exactly this — one shape all three
    ///         planners fill — and taking one is explicitly free of consequence: it goes through
    ///         <c>UtilitySet.Score</c> rather than <c>Choose</c>, so it does not advance a decision
    ///         clock or start a cooldown. <c>TakingASnapshotDoesNotChangeWhatTheAgentDecides</c> is
    ///         that asserted.
    ///     </para>
    /// </remarks>
    public Symbol Doing(Entity entity) =>
        AiSnapshots.Take(Agents, World, entity, snapshot) ? snapshot.Action : Symbol.None;

    /// <summary>Puts the intruder where its script says it is at this moment.</summary>
    /// <param name="elapsed">Seconds since the village started, accumulated from the frame deltas.</param>
    public void MoveIntruder(double elapsed) =>
        World.Get<LocalTransform>(Intruder).Position = Intrusion.At(elapsed);

    /// <summary>Puts an agent on the mesh, standing still until something decides otherwise.</summary>
    /// <remarks>
    ///     ⚠ <b>The destination is seeded to the agent's own position, and a zeroed one is not
    ///     "none".</b> <c>NavigationDestination</c> is a <c>Vector3</c> and a version and carries no
    ///     "has one" flag, so <c>default</c> is the world origin and the crowd walks there. An agent
    ///     whose planner has not issued a destination yet — the villager, whose highest-scoring
    ///     action is a <c>Wait</c> until it perceives something — therefore sets off for (0, 0, 0)
    ///     the moment the first frame ticks, and arrives before the thing it was waiting for ever
    ///     comes into range.
    ///
    ///     It is the zero-value trap in its purest form: nothing errors, every system does its job,
    ///     and the symptom is an agent that "ignores" a threat it can no longer see because it
    ///     wandered into a corner forty metres away.
    /// </remarks>
    Entity Spawn(Vector3 at, in AiAgent agent) {
        var entity = World.Create(
            LocalTransform.At(at),
            NavigationAgent.Default(),
            new NavigationDestination { Value = at }
        );

        World.Add(entity, new NavigationState { Position = at });
        World.Add(entity, agent);
        World.Add(entity, AiPerception.Sensing(0, team: 1));

        return entity;
    }

    BlackboardKey Key(string name) => Layout.Key(name);

    /// <summary>One config, shared by the guard, the villager and the scavenger.</summary>
    /// <remarks>
    ///     ⚠ <b><c>RandomDeviation = 0</c> and <c>Occlusion = false</c>, both on purpose.</b> A
    ///     sample must not be random — two runs of <c>--vixen-frames 600</c> have to produce the
    ///     same log — and there is no geometry on this floor to occlude anything, so a solver would
    ///     be a dependency paid for a test that always passes.
    /// </remarks>
    PerceptionSystem Perceive() {
        var system = new PerceptionSystem();

        system.Configs.Add(
            new PerceptionConfig {
                Name = Symbol.Intern("villager-eyes"),
                Senses = SenseMask.Sight,
                Sight = new() { Radius = 14f, LoseSightRadius = 16f, ConeDegrees = 360f, Occlusion = false },
                RandomDeviation = 0f,
                Filter = PerceptionFilters.Hostiles,
                Binding = new TargetLocationAgeBinding(SenseMask.Sight, Key("target"), Key("seen"), Key("age"))
            }
        );

        return system;
    }

    /// <summary>Chase what was seen recently; otherwise walk the beat.</summary>
    /// <remarks>
    ///     ⚠ <b><c>ObserverAborts.Both</c> is what makes this react rather than poll.</b> The
    ///     decorator observes <c>age</c>, so writing that key is what pulls the guard off its patrol
    ///     — and doc 37 § D6's abort is two integer comparisons against the task's pre-order range,
    ///     serviced at the top of the next step rather than inside the write.
    /// </remarks>
    BehaviorTreeTemplate GuardTree(ushort chase, ushort patrol) =>
        BehaviorTreeCompiler.Compile(
            BehaviorTree.Asset(
                "guard",
                BehaviorTree.Selector(
                    "brain",
                    BehaviorTree.Task("chase", "chase")
                        .With(BlackboardDecorator.Number(Key("age"), BlackboardTest.Less, 0.5f, ObserverAborts.Both)),
                    BehaviorTree.Task("walk", "patrol")
                )
            ),
            Registry,
            Layout
        );

    /// <summary>Run when something is near; otherwise sit still.</summary>
    /// <remarks>
    ///     ⚠ <b>The curve is what makes this a judgement rather than a threshold.</b>
    ///     <c>NearestPerceived</c> reads 1 at zero distance and 0 at the sight radius; a slope of
    ///     −1 with a shift of 1 turns that into "closer is worse", and the villager's two actions
    ///     are then compared rather than switched between.
    /// </remarks>
    UtilitySet VillagerSet(ushort flee) =>
        new(
            Symbol.Intern("villager"),
            new UtilityAction(
                Symbol.Intern("flee"),
                flee,
                new UtilityConsideration(
                    Symbol.Intern("threat"),
                    PerceptionInputs.NearestPerceived(Perception, SenseMask.Sight, 14f),
                    new ResponseCurve { Slope = -1f, Shift = 1f }
                )
            ) { Weight = 2f },
            new UtilityAction(
                Symbol.Intern("rest"),
                Pause,
                new UtilityConsideration(Symbol.Intern("calm"), UtilityInputs.Constant(0.35f), ResponseCurve.Identity)
            )
        );

    /// <summary>Fetch the scrap, then take it to the depot.</summary>
    /// <remarks>
    ///     ⚠ <b>The third action shares the villager's registry index</b>, which is doc 37 § D2 in
    ///     the sample rather than in a comment: one <c>WaitTask</c> object, chosen by a utility set
    ///     and by a plan.
    /// </remarks>
    GoapDomain ScavengerDomain(ushort collect, ushort deposit) {
        var keys = new GoapWorldKeys(
            new(Symbol.Intern("at-scrap"), GoapWorldSources.From((in AgentContext c) => Near(in c, ScrapPile))),
            new(Symbol.Intern("delivered"), GoapWorldSources.From((in AgentContext c) => Near(in c, Depot)))
        );

        var atScrap = new GoapWorldKey(0);
        var delivered = new GoapWorldKey(1);

        return new(
            Symbol.Intern("scavenger"),
            keys,
            [
                new GoapAction(Symbol.Intern("collect"), collect, [], new GoapEffect(atScrap, true)),
                new GoapAction(
                    Symbol.Intern("deposit"),
                    deposit,
                    [new(atScrap, GoapComparison.GreaterOrEqual, 1)],
                    new GoapEffect(delivered, true)
                ),
                new GoapAction(Symbol.Intern("wait"), Pause, [], new GoapEffect(delivered, false))
            ],
            [new GoapGoal(Symbol.Intern("delivered"), [new(delivered, GoapComparison.GreaterOrEqual, 1)])]
        );
    }

    /// <summary>Whether the agent is standing on something, as a world key.</summary>
    static int Near(in AgentContext context, Vector3 place) =>
        context.World.Has<LocalTransform>(context.Entity)
        && AgentTarget.FlatDistance(context.World.Read<LocalTransform>(context.Entity).Position, place) < 3f
            ? 1
            : 0;
}
