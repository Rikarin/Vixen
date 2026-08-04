// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>An action that counts its own ticks — in its span, which is the whole point.</summary>
sealed class CountingAction : IAgentAction {
    public int Starts;
    public int Aborts;

    public void Start(in AgentContext context, Span<byte> state) => Starts++;

    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        ref var counted = ref MemoryMarshal.AsRef<State>(state);

        counted.Ticks++;
        counted.Seconds += delta;

        return ActionStatus.Running;
    }

    public void Abort(in AgentContext context, Span<byte> state) => Aborts++;

    public static int TicksOf(Span<byte> state) => MemoryMarshal.AsRef<State>(state).Ticks;

    public static float SecondsOf(Span<byte> state) => MemoryMarshal.AsRef<State>(state).Seconds;

    public static int Size => Marshal.SizeOf<State>();

    struct State {
        public int Ticks;
        public float Seconds;
    }
}

/// <summary>An action that succeeds after a fixed number of ticks.</summary>
sealed class FinishingAction(int after) : IAgentAction {
    public void Start(in AgentContext context, Span<byte> state) { }

    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        ref var counted = ref MemoryMarshal.AsRef<int>(state);

        return ++counted >= after ? ActionStatus.Succeeded : ActionStatus.Running;
    }

    public void Abort(in AgentContext context, Span<byte> state) { }
}

public class AgentActionRegistryTests {
    [Fact]
    public void AnActionIsFoundByNameAndByIndex() {
        var registry = new AgentActionRegistry();
        var index = registry.Register("wait", new CountingAction(), CountingAction.Size);

        Assert.True(registry.TryGetIndex(Symbol.Intern("wait"), out var found));
        Assert.Equal(index, found);
        Assert.Equal(Symbol.Intern("wait"), registry.NameOf(index));
        Assert.Equal(CountingAction.Size, registry.StateSize(index));
        Assert.Equal(CountingAction.Size, registry.MaximumStateSize);
    }

    [Fact]
    public void TheSameActionMayBeRegisteredTwiceUnderDifferentNames() {
        var registry = new AgentActionRegistry();
        var action = new CountingAction();
        var small = registry.Register("small", action, 4);
        var large = registry.Register("large", action, 64);

        Assert.NotEqual(small, large);
        Assert.Same(registry[small], registry[large]);
        Assert.Equal(64, registry.MaximumStateSize);
    }

    [Fact]
    public void ADuplicateNameOrAnUnknownIndexIsRefused() {
        var registry = new AgentActionRegistry();

        registry.Register("wait", new CountingAction());

        Assert.Throws<InvalidOperationException>(() => registry.Register("wait", new CountingAction()));
        Assert.Throws<ArgumentOutOfRangeException>(() => registry[7]);
        Assert.Throws<ArgumentNullException>(() => registry.Register("null", null!));
    }
}

public class AiSystemTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("ticks", BlackboardValueType.Int)
        .Build();

    [Fact]
    public void AnAgentRunsItsActionAndKeepsItsCountInItsOwnSpan() {
        var (world, system, action) = Build(new UnboundedGovernor());
        var entities = Spawn(world, 3, action);

        for (var frame = 0; frame < 5; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(3, system.Population);
        Assert.Equal(3, action.Starts);

        foreach (var entity in entities) {
            Assert.Equal(5, CountingAction.TicksOf(system.Memory.Resolve(world.Read<AiAgent>(entity).Memory)));
        }
    }

    /// <summary>
    ///     The template/instance test: one action object, a hundred agents, each driven to its own
    ///     state. This is the one that fails if any action keeps state on itself.
    /// </summary>
    [Fact]
    public void AHundredAgentsOnOneActionHoldAHundredIndependentStates() {
        var (world, system, action) = Build(new UnboundedGovernor());
        var entities = Spawn(world, 100, action);

        // Each agent is disabled after a different number of frames, so every one of the hundred
        // ends on a different count. One shared field would give a hundred identical answers.
        for (var frame = 0; frame < 100; frame++) {
            system.Step(world, Frame(frame));

            world.Get<AiAgent>(entities[frame]).Enabled = false;
        }

        for (var index = 0; index < entities.Length; index++) {
            var ticks = CountingAction.TicksOf(system.Memory.Resolve(world.Read<AiAgent>(entities[index]).Memory));

            Assert.Equal(index + 1, ticks);
        }
    }

    /// <summary>
    ///     ⚠ A governed agent gets the time it actually waited, not the frame's. Without this a
    ///     <c>Wait(2 s)</c> under a one-in-four budget silently takes eight.
    /// </summary>
    [Fact]
    public void AnAgentGetsTheTimeSinceItLastTickedRatherThanTheFrames() {
        var (world, system, action) = Build(new RoundRobinGovernor { Budget = 1, MaximumInterval = 1_000 });
        var entities = Spawn(world, 4, action);

        for (var frame = 0; frame < 40; frame++) {
            system.Step(world, Frame(frame));
        }

        foreach (var entity in entities) {
            ref readonly var agent = ref world.Read<AiAgent>(entity);
            var state = system.Memory.Resolve(agent.Memory);

            Assert.Equal(10, CountingAction.TicksOf(state));

            // Ten turns out of forty frames of a hundredth of a second each. The ten deltas have to
            // account for all forty frames rather than for ten of them — less whatever has piled up
            // since the agent's last turn and has not been handed to it yet.
            Assert.Equal(0.40f, CountingAction.SecondsOf(state) + agent.Accumulated, 3);
            Assert.InRange(CountingAction.SecondsOf(state), 0.36f, 0.40f);
        }
    }

    [Fact]
    public void OnlyTheScheduledAgentsTick() {
        var (world, system, action) = Build(new RoundRobinGovernor { Budget = 2, MaximumInterval = 1_000 });
        var entities = Spawn(world, 8, action);

        system.Step(world, Frame(0));

        var ticked = entities.Count(
            entity => CountingAction.TicksOf(system.Memory.Resolve(world.Read<AiAgent>(entity).Memory)) > 0
        );

        Assert.Equal(2, ticked);
        Assert.Equal(2, system.LastSchedule.Count);
        Assert.Equal(6, system.LastSchedule.Skipped);
    }

    [Fact]
    public void ADisabledAgentDoesNotThinkAndDoesNotAccumulate() {
        var (world, system, action) = Build(new UnboundedGovernor());
        var entity = Spawn(world, 1, action)[0];

        world.Get<AiAgent>(entity).Enabled = false;

        for (var frame = 0; frame < 10; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(0, action.Starts);
        Assert.Equal(0, CountingAction.TicksOf(system.Memory.Resolve(world.Read<AiAgent>(entity).Memory)));
    }

    [Fact]
    public void AnAgentThatJoinsGetsABoardOfItsOwn() {
        var (world, system, action) = Build(new UnboundedGovernor());
        var entities = Spawn(world, 2, action);

        system.Step(world, Frame(0));

        var first = system.BlackboardOf(world.Read<AiAgent>(entities[0]))!;
        var second = system.BlackboardOf(world.Read<AiAgent>(entities[1]))!;

        Assert.NotSame(first, second);

        first.SetInt(Layout.Key("ticks"), 4);

        Assert.Equal(4, first.GetInt(Layout.Key("ticks")));
        Assert.False(second.IsSet(Layout.Key("ticks")));
    }

    [Fact]
    public void ADestroyedAgentGivesItsMemoryAndItsSlotBack() {
        var (world, system, action) = Build(new UnboundedGovernor());
        var entities = Spawn(world, 4, action);

        system.Step(world, Frame(0));
        Assert.Equal(4, system.Memory.RentedCount);

        world.Destroy(entities[1]);
        world.Destroy(entities[2]);
        system.Step(world, Frame(1));

        Assert.Equal(2, system.Population);
        Assert.Equal(2, system.Memory.RentedCount);

        // The freed slots are handed back out rather than growing the table for ever.
        Spawn(world, 2, action);
        system.Step(world, Frame(2));

        Assert.Equal(4, system.Population);
        Assert.Equal(4, system.Memory.RentedCount);
        Assert.Equal(4, system.Memory.BlockCount);
    }

    [Fact]
    public void AFinishedActionIsRestartedRatherThanLeftFinished() {
        var world = new World(nameof(AFinishedActionIsRestartedRatherThanLeftFinished));
        var registry = new AgentActionRegistry();
        var index = registry.Register("finish", new FinishingAction(2), sizeof(int));
        var system = new AiSystem(registry, Layout) { Governor = new UnboundedGovernor() };
        var entity = world.Create(AiAgent.Running(index));

        for (var frame = 0; frame < 6; frame++) {
            system.Step(world, Frame(frame));
        }

        // Two ticks to succeed, then a restart, so the sixth frame is the third success.
        Assert.Equal(ActionStatus.Succeeded, world.Read<AiAgent>(entity).Status);
        Assert.False(world.Read<AiAgent>(entity).Started);
    }

    [Fact]
    public void SeedsAreKeyedOnTheEntityRatherThanOnTheJoinOrder() {
        var (world, system, action) = Build(new UnboundedGovernor());
        var entities = Spawn(world, 16, action);

        system.Step(world, Frame(0));

        foreach (var entity in entities) {
            Assert.Equal(AgentRandom.SeedOf(entity), world.Read<AiAgent>(entity).Seed);
        }

        Assert.True(
            entities.Select(entity => world.Read<AiAgent>(entity).Seed).Distinct().Count() >= 15,
            "sixteen agents produced fewer than fifteen distinct streams."
        );
    }

    [Fact]
    public void TheDebugRecorderIsOffUntilItIsTurnedOn() {
        var (world, system, action) = Build(new UnboundedGovernor());

        Spawn(world, 2, action);
        system.Step(world, Frame(0));

        Assert.False(system.Debug.Enabled);
        Assert.Equal(0, system.Debug.Count);

        system.Debug.Enabled = true;
        system.Step(world, Frame(1));

        Assert.Equal(2, system.Debug.Count);
    }

    [Fact]
    public void TheSystemDeclaresWhatItWrites() {
        var (_, system, _) = Build(new UnboundedGovernor());

        Assert.Contains(ComponentType<AiAgent>.Id, system.Access.Writes);
    }

    static (World World, AiSystem System, CountingAction Action) Build(IAgentGovernor governor) {
        var world = new World("ai-test");
        var registry = new AgentActionRegistry();
        var action = new CountingAction();

        registry.Register("count", action, CountingAction.Size);

        return (world, new(registry, Layout) { Governor = governor }, action);
    }

    static Entity[] Spawn(World world, int count, CountingAction action) {
        _ = action;

        var entities = new Entity[count];

        for (var index = 0; index < count; index++) {
            entities[index] = world.Create(AiAgent.Running(0));
        }

        return entities;
    }

    static GameTime Frame(int index) =>
        new(TimeSpan.FromSeconds(index * 0.01), TimeSpan.FromSeconds(0.01), TimeSpan.FromSeconds(0.01), index, 1f);
}
