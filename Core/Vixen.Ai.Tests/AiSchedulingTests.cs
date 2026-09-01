// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Ai;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Testing;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     P0's first exit criterion, measured rather than asserted by inspection: a hand-built agent
///     with one action, running under the governor at ten thousand entities, with zero steady-state
///     allocation.
/// </summary>
public class AiSchedulingTests {
    const int Population = 10_000;

    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("target", BlackboardValueType.Entity)
        .Add("distance", BlackboardValueType.Float)
        .Add("alert", BlackboardValueType.Bool)
        .Build();

    /// <summary>
    ///     ⚠ <b>Zero, not "small".</b> Everything on this path owns its own storage: the memory pool
    ///     carves pages, the blackboards are allocated on join, the governor returns a struct and the
    ///     schedule is a window rather than a list. A steady-state byte here would be a byte per
    ///     agent per frame, which at ten thousand agents is a collection every few seconds — the kind
    ///     of cost that never shows up in a profile as one line.
    /// </summary>
    [Fact]
    public void TenThousandAgentsAllocateNothingInASteadyStateFrame() {
        var (world, system) = Build(new RoundRobinGovernor { Budget = 512, MaximumInterval = 32 });

        for (var index = 0; index < Population; index++) {
            world.Create(AiAgent.Running(0));
        }

        var frame = 0;

        // The joins, the blackboards and the pool's pages all happen in the first step, and the
        // warm-up in Measured is what puts them behind us. What is being measured is the frame after
        // the population stopped changing, which is what a game spends its time in.
        Measured.NothingAllocated(() => system.Step(world, Frame(frame++)), warmUp: 20, passes: 200);

        Assert.Equal(Population, system.Population);
    }

    /// <summary>
    ///     The governor is what a budget buys, so the number it buys is recorded rather than assumed:
    ///     at a budget of 512 against ten thousand agents, a frame ticks 512 of them and every one of
    ///     them is back inside 32 frames.
    /// </summary>
    [Fact]
    public void TheBudgetIsSpentAndTheFloorIsHeldAtTenThousandAgents() {
        var (world, system) = Build(new RoundRobinGovernor { Budget = 512, MaximumInterval = 32 });

        for (var index = 0; index < Population; index++) {
            world.Create(AiAgent.Running(0));
        }

        for (var frame = 0; frame < 64; frame++) {
            system.Step(world, Frame(frame));
        }

        Assert.Equal(512, system.LastSchedule.Count);
        Assert.False(system.LastSchedule.OverBudget);

        // 64 frames at 512 a frame is 32 768 turns over 10 000 agents, so every agent has had at
        // least three — and none has had more than four, which is what "round-robin" means.
        var turns = new List<int>(Population);

        foreach (var chunk in world.Chunks(new QueryDescription().WithAll<AiAgent>())) {
            var agents = chunk.ReadValues<AiAgent>();

            for (var index = 0; index < chunk.Count; index++) {
                turns.Add(Ticks(system, agents[index]));
            }
        }

        Assert.Equal(Population, turns.Count);
        Assert.InRange(turns.Min(), 3, 4);
        Assert.InRange(turns.Max(), 3, 4);
    }

    static int Ticks(AiSystem system, in AiAgent agent) =>
        MemoryMarshal.Read<int>(system.Memory.Resolve(agent.Memory));

    static (World World, AiSystem System) Build(IAgentGovernor governor) {
        var world = new World("ai-scheduling");
        var registry = new AgentActionRegistry();

        registry.Register("count", new TickCountingAction(), sizeof(int));

        return (world, new AiSystem(registry, Layout) { Governor = governor });
    }

    static GameTime Frame(int index) =>
        new(TimeSpan.FromSeconds(index / 60.0), TimeSpan.FromSeconds(1 / 60.0), TimeSpan.FromSeconds(1 / 60.0), index, 1f);

    /// <summary>Four bytes of state and nothing else, so the measurement is of the schedule.</summary>
    sealed class TickCountingAction : IAgentAction {
        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
            MemoryMarshal.AsRef<int>(state)++;

            return ActionStatus.Running;
        }

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}
