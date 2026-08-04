// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Core;
using Vixen.Ecs;

namespace Vixen.Ai.Tests;

/// <summary>A task that records what happened to it, in its span, per agent.</summary>
/// <remarks>
///     The instrumented leaf every tree test is built out of. It answers with whatever it was
///     constructed with; what varies per agent — how many times it started, ticked and was aborted —
///     is in the span, because a field here would be the exact bug the span exists to prevent.
/// </remarks>
sealed class ProbeTask(ActionStatus result = ActionStatus.Running, int ticksToFinish = 1) : IAgentAction {
    public static int StateSize => Marshal.SizeOf<ProbeState>();

    /// <summary>
    ///     Totals across every agent and every entry, on the shared object.
    /// </summary>
    /// <remarks>
    ///     ⚠ Deliberately the thing an action must never do, kept here because a test needs to count
    ///     what the span cannot: <c>Start</c> is handed a <i>zeroed</i> span, so a per-agent counter
    ///     reads 1 however many times the node was re-entered. The span counters below are what the
    ///     template/instance test asserts on, and they are the ones that matter.
    /// </remarks>
    public int TotalStarts { get; private set; }

    public int TotalAborts { get; private set; }

    public void Start(in AgentContext context, Span<byte> state) {
        ref var probe = ref MemoryMarshal.AsRef<ProbeState>(state);

        probe.Starts++;
        TotalStarts++;
    }

    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        ref var probe = ref MemoryMarshal.AsRef<ProbeState>(state);

        probe.Ticks++;
        probe.Seconds += delta;

        return probe.Ticks >= ticksToFinish ? result : ActionStatus.Running;
    }

    public void Abort(in AgentContext context, Span<byte> state) {
        ref var probe = ref MemoryMarshal.AsRef<ProbeState>(state);

        probe.Aborts++;
        TotalAborts++;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProbeState {
        public int Starts;
        public int Ticks;
        public int Aborts;
        public float Seconds;
    }
}

/// <summary>A tree, its blackboard, its agent and a clock, with one method to step it.</summary>
sealed class TreeHarness : IDisposable {
    readonly World world = new("tree-test");

    TreeHarness(BehaviorTreeTemplate template, BlackboardLayout layout, AgentActionRegistry actions) {
        Actions = actions;
        Layout = layout;
        Memory = new();
        Board = new(layout);
        Entity = world.Create();
        Tree = new(template, Memory);
    }

    public AgentActionRegistry Actions { get; }

    public BlackboardLayout Layout { get; }

    public AgentMemoryPool Memory { get; }

    public Blackboard Board { get; }

    public Entity Entity { get; }

    public BehaviorTreeInstance Tree { get; }

    public AgentDebugRecorder Debug { get; } = new();

    public float Now { get; private set; }

    public int Frame { get; private set; }

    /// <summary>Builds a harness over an authored tree.</summary>
    public static TreeHarness For(
        BehaviorNodeDefinition root,
        BlackboardLayout layout,
        Action<AgentActionRegistry>? register = null
    ) {
        // A registry per harness, so the shared totals above belong to one test rather than to the
        // whole assembly.
        var actions = new AgentActionRegistry();

        register?.Invoke(actions);

        var template = BehaviorTreeCompiler.Compile(BehaviorTree.Asset("test", root), actions, layout);

        return new(template, layout, actions);
    }

    /// <summary>An action registry with the probes every test uses.</summary>
    public static AgentActionRegistry Probes(AgentActionRegistry actions) {
        actions.Register("running", new ProbeTask(), ProbeTask.StateSize);
        actions.Register("succeed", new ProbeTask(ActionStatus.Succeeded), ProbeTask.StateSize);
        actions.Register("fail", new ProbeTask(ActionStatus.Failed), ProbeTask.StateSize);
        actions.Register("succeed-after-2", new ProbeTask(ActionStatus.Succeeded, 2), ProbeTask.StateSize);

        return actions;
    }

    /// <summary>How many times a named action has been started, across every entry.</summary>
    public int TotalStarts(string action) =>
        Actions.TryGetIndex(Symbol.Intern(action), out var index) ? ((ProbeTask)Actions[index]).TotalStarts : 0;

    /// <summary>How many times it has been aborted.</summary>
    public int TotalAborts(string action) =>
        Actions.TryGetIndex(Symbol.Intern(action), out var index) ? ((ProbeTask)Actions[index]).TotalAborts : 0;

    public AgentContext Context(float delta = 1f / 60f) => new(
        world,
        Entity,
        Board,
        null,
        new(TimeSpan.FromSeconds(Now), TimeSpan.FromSeconds(delta), TimeSpan.FromSeconds(delta), Frame, 1f),
        AgentRandom.SeedOf(Entity),
        Actions,
        Debug
    );

    /// <summary>Steps the tree once, advancing the clock.</summary>
    public ActionStatus Step(float delta = 1f / 60f) {
        var context = Context(delta);
        var status = Tree.Step(in context, delta);

        Now += delta;
        Frame++;

        return status;
    }

    /// <summary>Steps it several times.</summary>
    public void Steps(int count, float delta = 1f / 60f) {
        for (var index = 0; index < count; index++) {
            Step(delta);
        }
    }

    /// <summary>What a task node recorded, for this agent.</summary>
    public ProbeTask.ProbeState Probe(int node) =>
        MemoryMarshal.Read<ProbeTask.ProbeState>(Tree.StateOf(node));

    /// <summary>The name of whatever node is active.</summary>
    public string Active =>
        Tree.ActiveNode < 0 ? "<none>" : Tree.Template[Tree.ActiveNode].Name.ToString();

    public void Dispose() => world.Dispose();
}
