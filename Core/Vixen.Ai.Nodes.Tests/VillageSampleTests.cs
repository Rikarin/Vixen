// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Ai.Nodes.Ecs;
using Vixen.Ai.Perception;
using Vixen.Ai.Perception.Ecs;
using Vixen.Ai.Perception.Diagnostics;
using Vixen.Ai.Perception.Sensors;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Ai.Nodes.Tests;

/// <summary>A thing worth scavenging. The sample's only game rule, and it is one component.</summary>
[Component]
struct Scrap;

/// <summary>
///     P9's sample: one level, three agent kinds, one of each planner, sharing one perception model.
/// </summary>
/// <remarks>
///     <para>
///         <b>The claim at the top of doc 37, demonstrated.</b> A guard runs a behaviour tree, a
///         villager scores a utility set and a scavenger plans over a GOAP domain; all three are
///         stepped by <i>one</i> <c>AiSystem</c>, choose out of <i>one</i>
///         <c>AgentActionRegistry</c>, sense through <i>one</i> <c>PerceptionSystem</c> with one
///         config, read <i>one</i> <c>SensorSet</c>, and walk <i>one</i> navmesh. What differs
///         between them is the planner and nothing else.
///     </para>
///     <para>
///         ⚠ <b>A test rather than a <c>Samples/</c> project, and that is a deviation worth naming.</b>
///         Doc 37 asks for a sample; what its exit criterion actually measures is that the three
///         agents are <i>visibly different</i> and <i>share every system</i>, and both of those are
///         statements about positions and object identity rather than about pixels. A graphical
///         sample would need a level, art and a renderer that this document does not own, and it would
///         assert none of this. It remains a good addition on top.
///     </para>
///     <para>
///         ⚠ <b>Nothing here drives an agent.</b> The only thing the test does per frame is step the
///         three systems; where the intruder stands is set twice. So a failure is a failure of the
///         whole chain — sense, sensor, key, score or plan, action, crowd — which is what P0 to P8
///         exist to make work together.
///     </para>
/// </remarks>
public class VillageSampleTests {
    static readonly Vector3 Refuge = new(4f, 0f, 34f);
    static readonly Vector3 Depot = new(34f, 0f, 34f);
    static readonly Vector3 ScrapPile = new(34f, 0f, 6f);

    static readonly Vector3[] Beat = [
        new(8f, 0f, 8f),
        new(8f, 0f, 26f)
    ];

    [Fact]
    public void ThreeAgentsThreePlannersOneOfEverythingElse() {
        var village = new Village();

        // ── Everything is one of ─────────────────────────────────────────────────────────────
        village.Step(60);

        Assert.Equal(3, village.Agents.Population);

        Assert.Equal(
            [AiPlanner.BehaviorTree, AiPlanner.Utility, AiPlanner.Goap],
            new[] { village.Guard, village.Villager, village.Scavenger }
                .Select(agent => village.World.Read<AiAgent>(agent).Planner)
                .ToArray()
        );

        // ⚠ One registry, and one action index in two planners at once — doc 37 § D2's whole payoff.
        // A project writes the task once and gets it in a tree, in a set and in a plan.
        Assert.Same(village.Registry, village.Agents.Actions);
        Assert.Equal(village.Pause, village.Agents.Sets[0][1].Action);
        Assert.Equal(village.Pause, village.Agents.Domains[0][2].Action);

        // One perception model: three listeners, one config.
        foreach (var agent in new[] { village.Guard, village.Villager, village.Scavenger }) {
            Assert.Equal(0, village.World.Read<AiPerception>(agent).Config);
        }

        Assert.Equal(1, village.Perception.Configs.Count);

        // ── And they are visibly different ───────────────────────────────────────────────────
        var start = (
            Guard: village.Where(village.Guard),
            Villager: village.Where(village.Villager),
            Scavenger: village.Where(village.Scavenger)
        );

        // An intruder walks into the middle of the village, between the guard's beat and the
        // villager's bench.
        village.Transform(village.Intruder).Position = new(10f, 0f, 16f);
        village.Step(420);

        var here = village.Transform(village.Intruder).Position;
        var guard = village.Where(village.Guard);
        var villager = village.Where(village.Villager);
        var scavenger = village.Where(village.Scavenger);

        var report = $"guard {guard}, villager {villager}, scavenger {scavenger}, intruder {here}.";

        // The guard closed on it.
        Assert.True(
            AgentTarget.FlatDistance(guard, here) < AgentTarget.FlatDistance(start.Guard, here),
            $"the guard did not close on the intruder. {report}"
        );

        // The villager ran the other way, to the refuge it was told about by a *global* sensor.
        Assert.True(
            AgentTarget.FlatDistance(villager, here) > AgentTarget.FlatDistance(start.Villager, here),
            $"the villager did not back away. {report}"
        );

        Assert.True(
            AgentTarget.FlatDistance(villager, Refuge) < AgentTarget.FlatDistance(start.Villager, Refuge),
            $"the villager is not heading for the refuge. {report}"
        );

        // ⚠ And the scavenger ignored the intruder entirely, which is the half of "visibly different"
        // that is easy to forget: three agents that all ran away would also be three agents, and
        // would prove nothing about there being three planners.
        Assert.True(
            AgentTarget.FlatDistance(scavenger, ScrapPile) < 3f
            || AgentTarget.FlatDistance(scavenger, Depot) < AgentTarget.FlatDistance(ScrapPile, Depot),
            $"the scavenger is not about its business. {report}"
        );

        Assert.True(
            village.Agents.PlanningOf(in village.World.Read<AiAgent>(village.Scavenger))!.Plans > 0,
            "the scavenger never planned anything."
        );
    }

    /// <summary>
    ///     ⚠ <b>And they are all debugged through one surface</b>, which is doc 37 § D20's claim and
    ///     the cheapest thing in the document to get wrong: one recorder holds all three, and
    ///     <c>AiSnapshots</c> answers about each of them without knowing which planner it is looking
    ///     at.
    /// </summary>
    [Fact]
    public void OneDebugSurfaceAnswersAboutAllThree() {
        var village = new Village();

        village.Agents.Debug.Enabled = true;
        village.Step(90);

        var snapshot = new AiAgentSnapshot();
        var planners = new List<AiPlanner>();

        foreach (var agent in new[] { village.Guard, village.Villager, village.Scavenger }) {
            Assert.True(AiSnapshots.Take(village.Agents, village.World, agent, snapshot));
            Assert.True(PerceptionSnapshots.Describe(village.Perception, village.World, agent, snapshot));
            Assert.NotEqual(Symbol.None, snapshot.Asset);
            Assert.NotEmpty(snapshot.Rows);
            planners.Add(snapshot.Planner);
        }

        Assert.Equal([AiPlanner.BehaviorTree, AiPlanner.Utility, AiPlanner.Goap], planners);

        var records = new List<AgentDebugRecord>();

        AiDiagnosis.Read(village.Agents.Debug, Entity.Null, records);
        Assert.NotEmpty(records);

        // ⚠ Nothing in the village is misbehaving, and a diagnosis that fired on a working village
        // would be one people learn to ignore.
        var findings = new List<AiFinding>();

        Assert.Equal(0, AiDiagnosis.Analyse(village.Agents.Debug, findings));
    }

    /// <summary>One level, three agent kinds, and one of everything else.</summary>
    sealed class Village {
        readonly Level level = new();

        public Village() {
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

            Agents = new AiSystem(Registry, Layout) { Governor = new UnboundedGovernor() };
            Perception = Perceive();
            Perception.Agents = Agents;
            level.Agents = Agents;

            // ── One sensor set, read by all three ────────────────────────────────────────────
            // ⚠ Two globals and one local, which is § D13's whole argument in three lines: the
            // refuge and the depot are one query for the village, and the nearest scrap is one query
            // per scavenger.
            Agents.Sensors = new SensorSet()
                .AddGlobalTarget(Key("refuge"), BlackboardKey.Invalid, Sensors.Landmark(Refuge))
                .AddGlobalTarget(Key("depot"), BlackboardKey.Invalid, Sensors.Landmark(Depot))
                .AddTarget(Key("scrap"), BlackboardKey.Invalid, WorldSensors.Nearest<Scrap>());

            Agents.Trees.Add(GuardTree(chase, patrol));
            Agents.Sets.Add(VillagerSet(flee));
            Agents.Domains.Add(ScavengerDomain(collect, deposit));

            Guard = Spawn(Beat[0], AiAgent.Thinking(0));
            level.World.Add(Guard, PatrolRoute.Of(PatrolMode.PingPong, Beat));

            Villager = Spawn(new(14f, 0f, 20f), AiAgent.Scoring(0));
            Scavenger = Spawn(new(28f, 0f, 20f), AiAgent.Planning(0));

            level.World.Create(LocalTransform.At(ScrapPile), new Scrap());

            Intruder = level.World.Create(
                LocalTransform.At(new(38f, 0f, 38f)),
                AiStimuliSource.Perceivable(team: 2, senses: SenseMask.Sight)
            );
        }

        public World World => level.World;

        public AgentActionRegistry Registry { get; }

        public BlackboardLayout Layout { get; }

        public AiSystem Agents { get; }

        public PerceptionSystem Perception { get; }

        public ushort Pause { get; }

        public Entity Guard { get; }

        public Entity Villager { get; }

        public Entity Scavenger { get; }

        public Entity Intruder { get; }

        public Vector3 Where(Entity entity) => level.Where(entity);

        public ref LocalTransform Transform(Entity entity) => ref level.Transform(entity);

        /// <summary>Sense, then think, then walk — the order <c>PerceptionSystem</c> declares.</summary>
        public void Step(int frames) =>
            level.Step(frames, frame => Perception.Step(level.World, Level.Frame(frame)));

        Entity Spawn(Vector3 at, in AiAgent agent) {
            var entity = level.Walker(at);

            level.World.Add(entity, agent);
            level.World.Add(entity, AiPerception.Sensing(0, team: 1));

            return entity;
        }

        BlackboardKey Key(string name) {
            Assert.True(Layout.TryGetKey(Symbol.Intern(name), out var key));

            return key;
        }

        /// <summary>One config, shared by the guard, the villager and the scavenger.</summary>
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
        BehaviorTreeTemplate GuardTree(ushort chase, ushort patrol) =>
            BehaviorTreeCompiler.Compile(
                BehaviorTree.Asset(
                    "guard",
                    BehaviorTree.Selector(
                        "brain",
                        BehaviorTree.Task("chase", "chase")
                            .With(
                                BlackboardDecorator.Number(
                                    Key("age"),
                                    BlackboardTest.Less,
                                    0.5f,
                                    ObserverAborts.Both
                                )
                            ),
                        BehaviorTree.Task("walk", "patrol")
                    )
                ),
                Registry,
                Layout
            );

        /// <summary>Run when something is near; otherwise sit still.</summary>
        UtilitySet VillagerSet(ushort flee) =>
            new(
                Symbol.Intern("villager"),
                new UtilityAction(
                    Symbol.Intern("flee"),
                    flee,
                    new UtilityConsideration(
                        Symbol.Intern("threat"),
                        // ⚠ The perception-backed input, which is the one implementation of that seam
                        // that reads neither a key nor a lambda — and it is in an assembly Vixen.Ai
                        // may not reference.
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
                    // ⚠ The shared action: the same registry index the villager rests on. One
                    // WaitTask object, two planners — doc 37 § D2, in the sample rather than in a
                    // comment.
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
}
