// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Ai.Perception;
using Vixen.Ai.Perception.Ecs;
using Vixen.Ai.Perception.Sensors;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Ai.Nodes.Tests;

/// <summary>
///     Doc 37's Part 4, enforced: every seam implemented twice, and the second one different enough to
///     prove the shape is not the default's shape wearing a mask.
/// </summary>
/// <remarks>
///     <para>
///         Doc 34's P9, verbatim — including where it lives. This is the only test project that can
///         see all three shipped assemblies at once (<c>Vixen.Ai</c>, <c>Vixen.Ai.Perception</c> and
///         <c>Vixen.Ai.Nodes</c>), and a seam test that could only see one of them would pass by not
///         looking.
///     </para>
/// </remarks>
public class SeamTests {
    /// <summary>
    ///     ⚠ <b>The rule the plan says is enforced in review, enforced by a test instead.</b> Review
    ///     catches this on the day the interface is added and never again; the assemblies can be asked
    ///     every build. A seam whose only implementation is the default is a seam shaped like the
    ///     default, and nobody finds that out until the second implementation is somebody's deadline.
    /// </summary>
    [Theory]
    // The action surface and the two planners' scoring halves.
    [InlineData(typeof(IAgentAction))]
    [InlineData(typeof(IUtilityInput))]
    [InlineData(typeof(IResponseCurve))]
    [InlineData(typeof(IUtilitySelector))]
    [InlineData(typeof(IScoredCandidateSet<>))]
    [InlineData(typeof(IFactorSource))]
    // Doc 37 § D13's four sensor kinds.
    [InlineData(typeof(IWorldSensor))]
    [InlineData(typeof(ITargetSensor))]
    [InlineData(typeof(IGlobalWorldSensor))]
    [InlineData(typeof(IGlobalTargetSensor))]
    // Perception.
    [InlineData(typeof(IOcclusionTester))]
    [InlineData(typeof(IPerceptionGovernor))]
    [InlineData(typeof(IPerceptionFilter))]
    [InlineData(typeof(IBlackboardBinding))]
    // GOAP.
    [InlineData(typeof(IReplanPolicy))]
    [InlineData(typeof(IActionCostModel))]
    [InlineData(typeof(IGoapWorldSource))]
    [InlineData(typeof(IGoapTargetSensor))]
    // The scheduler, and P8's queries.
    [InlineData(typeof(IAgentGovernor))]
    [InlineData(typeof(IQueryGenerator))]
    [InlineData(typeof(IQueryTest))]
    public void EverySeamIsImplementedTwice(Type seam) {
        var shipped = Shipped(seam);
        var tested = Implementations(typeof(SeamTests).Assembly, seam);

        Assert.True(shipped.Count > 0, $"{seam.Name} has no implementation in any shipped assembly at all.");

        Assert.True(
            shipped.Count + tested.Count >= 2,
            $"{seam.Name} has {shipped.Count} shipped and {tested.Count} test implementation(s): "
            + $"{string.Join(", ", shipped.Concat(tested).Select(type => type.Name))}. "
            + "Part 4 asks for at least two, and one of them somewhere other than the default."
        );
    }

    /// <summary>
    ///     ⚠ <b>And every seam is reachable through its interface rather than through a default.</b> An
    ///     interface implemented twice and consumed only as its default is a seam nobody is forced
    ///     through, which the plan says rots — so the properties are asked for by type.
    /// </summary>
    [Fact]
    public void NoDefaultIsReachableExceptThroughItsInterface() {
        Assert.Equal(typeof(IAgentGovernor), Property(typeof(AiSystem), nameof(AiSystem.Governor)));
        Assert.Equal(typeof(IReplanPolicy), Property(typeof(AiSystem), nameof(AiSystem.ReplanPolicy)));
        Assert.Equal(typeof(IActionCostModel), Property(typeof(AiSystem), nameof(AiSystem.Costs)));
        Assert.Equal(typeof(IOcclusionTester), Property(typeof(PerceptionSystem), nameof(PerceptionSystem.Occlusion)));
        Assert.Equal(
            typeof(IPerceptionGovernor),
            Property(typeof(PerceptionSystem), nameof(PerceptionSystem.Governor))
        );

        Assert.Equal(typeof(IUtilitySelector), Property(typeof(UtilitySet), nameof(UtilitySet.Selector)));
        Assert.Equal(typeof(IResponseCurve), Property(typeof(QueryTest), nameof(QueryTest.Curve)));
        Assert.Equal(typeof(IQueryTest), Property(typeof(QueryTest), nameof(QueryTest.Test)));
    }

    /// <summary>Every non-abstract implementation of a seam across the three shipped assemblies.</summary>
    static List<Type> Shipped(Type seam) => [
        .. Implementations(typeof(AiSystem).Assembly, seam),
        .. Implementations(typeof(PerceptionSystem).Assembly, seam),
        .. Implementations(typeof(WorldSensors).Assembly, seam),
        .. Implementations(typeof(AiGameplayDebugger).Assembly, seam)
    ];

    /// <summary>
    ///     ⚠ <b>Open generics are matched by definition and not by <c>IsAssignableFrom</c></b>, which
    ///     answers false for <c>UtilitySet</c> against <c>IScoredCandidateSet&lt;&gt;</c> and would
    ///     have made that row pass by finding nothing rather than by finding two.
    /// </summary>
    static List<Type> Implementations(Assembly assembly, Type seam) => [
        .. assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .Where(
                type => seam.IsGenericTypeDefinition
                    ? type.GetInterfaces().Any(face => face.IsGenericType && face.GetGenericTypeDefinition() == seam)
                    : seam.IsAssignableFrom(type)
            )
    ];

    static Type? Property(Type owner, string name) => owner.GetProperty(name)?.PropertyType;

    // ── Each one that earns it, exercised through the interface ──────────────

    /// <summary>
    ///     A utility input that reads neither a key nor a lambda: it counts what the agent can see,
    ///     out of an assembly <c>Vixen.Ai</c> cannot reference.
    /// </summary>
    [Fact]
    public void AUtilityInputMayReadSomethingVixenAiCannotSee() {
        var level = new Level();
        var perception = new PerceptionSystem();

        perception.Configs.Add(new() { Name = Symbol.Intern("watcher") });

        var watcher = level.World.Create(LocalTransform.At(Vector3.Zero), AiPerception.Sensing(0));

        // In front of it and on another team: the default cone is 90° about −Z and the default filter
        // is affiliation, so a source beside it or on its own team is correctly not seen — and either
        // mistake would have made this a test about perception rather than about the seam.
        level.World.Create(LocalTransform.At(new(0f, 0f, -3f)), AiStimuliSource.Perceivable(1));
        level.World.Create(LocalTransform.At(new(1f, 0f, -4f)), AiStimuliSource.Perceivable(1));

        // A few steps with a real delta: the shipped config senses on an interval with a random
        // deviation, so a single step of a zero-length frame is not guaranteed to be a sensing one.
        for (var frame = 0; frame < 8; frame++) {
            perception.Step(level.World, Level.Frame(frame));
        }

        var context = new AgentContext(level.World, watcher, new(BlackboardLayout.Empty), null, GameTime.Zero, 0);
        var crowded = PerceptionInputs.PerceivedCount(perception, most: 2);
        var near = PerceptionInputs.NearestPerceived(perception, range: 10f);

        Assert.Equal(1f, crowded.Read(in context), 3);

        // Three metres of ten: a reading no blackboard key was written for and no lambda was passed.
        Assert.Equal(0.3f, near.Read(in context), 2);

        // ⚠ And nothing sensed reads as *far*, not as near — under the zero rule the opposite would
        // make an agent flee from nothing for ever.
        var blind = level.World.Create(LocalTransform.At(new(500f, 0f, 0f)));
        var elsewhere = new AgentContext(level.World, blind, new(BlackboardLayout.Empty), null, GameTime.Zero, 0);

        Assert.Equal(1f, near.Read(in elsewhere), 3);
    }

    /// <summary>
    ///     ⚠ <b>A global sensor runs once a pass and a local one runs per agent, which is the whole of
    ///     § D13.</b> Sixteen agents, one pass: one global query and sixteen local ones.
    /// </summary>
    [Fact]
    public void AGlobalSensorIsOneQueryWhereALocalOneIsAThousand() {
        var level = new Level();
        var layout = new BlackboardLayoutBuilder()
            .Add("crowd", BlackboardValueType.Float)
            .Add("mine", BlackboardValueType.Float)
            .Build();

        var globals = 0;
        var locals = 0;

        var sensors = new SensorSet()
            .AddGlobal(
                layout.Key("crowd"),
                Sensors.GlobalWorld(
                    (world, time) => {
                        globals++;

                        return 1f;
                    }
                )
            )
            .Add(
                layout.Key("mine"),
                Sensors.World(
                    (in AgentContext context) => {
                        locals++;

                        return 2f;
                    }
                )
            );

        var registry = new AgentActionRegistry();

        registry.Register("idle", new Idling());

        var system = new AiSystem(registry, layout) { Sensors = sensors, Governor = new UnboundedGovernor() };

        for (var index = 0; index < 16; index++) {
            level.World.Create(AiAgent.Running(0), LocalTransform.At(new(index, 0f, 0f)));
        }

        system.Step(level.World, GameTime.Zero);

        Assert.Equal(1, globals);
        Assert.Equal(16, locals);
        Assert.Equal(1, sensors.Passes);
    }

    /// <summary>
    ///     ⚠ <b>A global's answer is cached at the top of the pass</b>, so two agents standing beside
    ///     each other cannot see different weather — which is the class of bug nobody looks for.
    /// </summary>
    [Fact]
    public void EveryAgentInAPassSeesTheSameGlobalReading() {
        var level = new Level();
        var layout = new BlackboardLayoutBuilder().Add("night", BlackboardValueType.Float).Build();
        var drifting = 0f;

        var sensors = new SensorSet().AddGlobal(
            layout.Key("night"),
            Sensors.GlobalWorld((world, time) => drifting += 1f)
        );

        var registry = new AgentActionRegistry();

        registry.Register("idle", new Idling());

        var system = new AiSystem(registry, layout) { Sensors = sensors, Governor = new UnboundedGovernor() };
        var agents = new List<Entity>();

        for (var index = 0; index < 4; index++) {
            agents.Add(level.World.Create(AiAgent.Running(0), LocalTransform.At(new(index, 0f, 0f))));
        }

        system.Step(level.World, GameTime.Zero);

        foreach (var agent in agents) {
            var board = system.BlackboardOf(in level.World.Read<AiAgent>(agent))!;

            Assert.Equal(1f, board.GetFloat(layout.Key("night")), 3);
        }
    }

    /// <summary>
    ///     A target sensor that finds a thing rather than a place, and clears both keys when there is
    ///     nothing. ⚠ A key still holding the apple that was eaten walks an agent to where an apple
    ///     used to be.
    /// </summary>
    [Fact]
    public void ATargetSensorWritesAPlaceAndAThingAndClearsBothWhenThereIsNone() {
        var level = new Level();
        var layout = new BlackboardLayoutBuilder()
            .Add("where", BlackboardValueType.Vector3)
            .Add("what", BlackboardValueType.Entity)
            .Build();

        var board = new Blackboard(layout);
        var agent = level.World.Create(LocalTransform.At(Vector3.Zero));
        var apple = level.World.Create(LocalTransform.At(new(2f, 0f, 0f)), new CoverSpot());

        var sensors = new SensorSet()
            .AddTarget(layout.Key("where"), layout.Key("what"), WorldSensors.Nearest<CoverSpot>());

        var context = new AgentContext(level.World, agent, board, null, GameTime.Zero, 0);

        sensors.Apply(in context);

        Assert.True(board.IsSet(layout.Key("where")));
        Assert.Equal(apple, board.GetEntity(layout.Key("what")));

        level.World.Destroy(apple);
        sensors.Apply(in context);

        Assert.False(board.IsSet(layout.Key("where")));
        Assert.False(board.IsSet(layout.Key("what")));
    }

    /// <summary>An action that is not a task: no tree, no set, no plan, just the interface.</summary>
    [Fact]
    public void AnActionNeedNotBeATask() {
        var world = new World("seam-action");

        using (world) {
            var action = new Idling();
            var context = new AgentContext(world, world.Create(), new(BlackboardLayout.Empty), null, GameTime.Zero, 0);

            action.Start(in context, []);

            Assert.Equal(ActionStatus.Running, action.Tick(in context, [], 0.1f));
        }
    }

    /// <summary>An action that runs and never stops, for a test that only wants the plumbing.</summary>
    sealed class Idling : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Running;

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}
