// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Ai.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Xunit;

namespace Vixen.Ai.Tests;

public class BehaviorTreeServiceTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("hunger", BlackboardValueType.Float)
        .Build();

    [Fact]
    public void AServiceRunsOnItsIntervalWhileItsBranchIsActive() {
        var sensor = new CountingSensor();

        using var harness = TreeHarness.For(
            BehaviorTree.Selector("root", BehaviorTree.Task("busy", "running"))
                .With(new UpdateBlackboardService(sensor, Layout.Key("hunger")), 0.5f),
            Layout,
            actions => TreeHarness.Probes(actions)
        );

        // Due at once on entry, so a branch just chosen acts on fresh data rather than on whatever
        // was there an interval ago. Quarter-second steps because they are exact in binary: an
        // interval walked down in tenths lands a hair above zero and the test would be about floats.
        harness.Step(0.25f);
        Assert.Equal(1, sensor.Calls);

        harness.Step(0.25f);
        Assert.Equal(1, sensor.Calls);

        harness.Step(0.25f);
        Assert.Equal(2, sensor.Calls);
    }

    [Fact]
    public void AServiceStopsWhenItsBranchDoes() {
        var sensor = new CountingSensor();
        var gate = new BlackboardLayoutBuilder().Add("gate", BlackboardValueType.Bool).Build();

        using var harness = TreeHarness.For(
            BehaviorTree.Selector(
                "root",
                BehaviorTree.Selector("watched", BehaviorTree.Task("busy", "running"))
                    .With(BlackboardDecorator.Bool(gate.Key("gate"), true, ObserverAborts.Self))
                    .With(new UpdateBlackboardService(sensor, gate.Key("gate")), 0.5f),
                BehaviorTree.Task("idle", "running")
            ),
            gate,
            actions => TreeHarness.Probes(actions)
        );

        harness.Board.SetBool(gate.Key("gate"), true);
        harness.Step(0.25f);
        Assert.Equal(1, sensor.Calls);

        harness.Board.SetBool(gate.Key("gate"), false);
        harness.Steps(20, 0.25f);

        Assert.Equal("idle", harness.Active);
        Assert.Equal(1, sensor.Calls);
    }

    /// <summary>
    ///     ⚠ Without a deviation, every agent spawned in the same frame ticks its service in the same
    ///     frame for ever — a 0.5 s perception update becomes a spike every thirty frames.
    /// </summary>
    [Fact]
    public void ARandomDeviationSpreadsAPopulationsServicesOut() {
        var sensor = new RecordingSensor();
        var actions = TreeHarness.Probes(new());
        var template = BehaviorTreeCompiler.Compile(
            BehaviorTree.Asset(
                "watcher",
                BehaviorTree.Selector("root", BehaviorTree.Task("busy", "running"))
                    .With(new UpdateBlackboardService(sensor, Layout.Key("hunger")), 0.5f, 0.2f)
            ),
            actions,
            Layout
        );

        using var world = new World("deviation");
        var pool = new AgentMemoryPool();
        var board = new Blackboard(Layout);
        var agents = new List<(BehaviorTreeInstance Tree, Entity Entity)>();

        // One world and one population, because the jitter is keyed on the agent — twenty-four
        // separate worlds would give twenty-four entities with the same id and the same draw, which
        // is the bug this test would then fail to see.
        for (var index = 0; index < 24; index++) {
            var entity = world.Create();

            agents.Add((new(template, pool), entity));
        }

        for (var frame = 0; frame < 24; frame++) {
            foreach (var (tree, entity) in agents) {
                var context = new AgentContext(
                    world,
                    entity,
                    board,
                    null,
                    new(TimeSpan.FromSeconds(frame * 0.05), TimeSpan.FromSeconds(0.05), TimeSpan.FromSeconds(0.05), frame, 1f),
                    AgentRandom.SeedOf(entity),
                    actions
                );

                sensor.Frame = frame;
                tree.Step(in context, 0.05f);
            }
        }

        // The first pass is on entry for everybody; what matters is that the second one is not.
        var spread = sensor.Frames.Where(frame => frame > 0).Distinct().Count();

        Assert.True(spread > 1, $"every agent ticked its service on the same frame ({spread} distinct).");
    }

    sealed class CountingSensor : IWorldSensor {
        public int Calls { get; private set; }

        public void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key) {
            Calls++;

            if (blackboard.Layout[key].Type == BlackboardValueType.Float) {
                blackboard.SetFloat(key, Calls);
            }
        }
    }

    sealed class RecordingSensor : IWorldSensor {
        public int Frame { get; set; }

        public List<int> Frames { get; } = [];

        public void Sense(in AgentContext context, Blackboard blackboard, BlackboardKey key) => Frames.Add(Frame);
    }
}

public class BehaviorTreeTaskTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("wait", BlackboardValueType.Float)
        .Add("count", BlackboardValueType.Int)
        .Add("where", BlackboardValueType.Vector3)
        .Add("state", BlackboardValueType.Symbol)
        .Add("tree", BlackboardValueType.Symbol)
        .Build();

    [Fact]
    public void WaitWaitsAndThenSucceeds() {
        using var harness = TreeHarness.For(
            BehaviorTree.Sequence("root", BehaviorTree.Task("pause", "wait")),
            Layout,
            actions => {
                TreeHarness.Probes(actions);
                actions.Register("wait", new WaitTask(0.3f), WaitTask.StateSize);
            }
        );

        harness.Steps(2, 0.1f);
        Assert.Equal("pause", harness.Active);

        harness.Step(0.1f);
        Assert.Equal(ActionStatus.Succeeded, harness.Tree.LastResult);
    }

    [Fact]
    public void WaitBlackboardTimeReadsItsDurationFromAKey() {
        using var harness = TreeHarness.For(
            BehaviorTree.Sequence("root", BehaviorTree.Task("pause", "wait-key")),
            Layout,
            actions => {
                TreeHarness.Probes(actions);
                actions.Register(
                    "wait-key",
                    new WaitBlackboardTimeTask(Layout.Key("wait")),
                    WaitBlackboardTimeTask.StateSize
                );
            }
        );

        harness.Board.SetFloat(Layout.Key("wait"), 0.25f);
        harness.Steps(2, 0.1f);
        Assert.Equal("pause", harness.Active);

        harness.Step(0.1f);
        Assert.Equal(ActionStatus.Succeeded, harness.Tree.LastResult);
    }

    [Fact]
    public void FinishWithEndsTheBranchAtOnce() {
        using var harness = TreeHarness.For(
            BehaviorTree.Selector("root", BehaviorTree.Task("stop", "finish-fail"), BehaviorTree.Task("after", "succeed")),
            Layout,
            actions => {
                TreeHarness.Probes(actions);
                actions.Register("finish-fail", new FinishWithTask(ActionStatus.Failed));
            }
        );

        harness.Step();

        Assert.Equal(ActionStatus.Succeeded, harness.Tree.LastResult);
        Assert.Equal(1, harness.Probe(2).Starts);
    }

    [Fact]
    public void SetAndClearBlackboardValueWriteTheKeysTheyName() {
        using var harness = TreeHarness.For(
            BehaviorTree.Sequence(
                "root",
                BehaviorTree.Task("set-count", "set-count"),
                BehaviorTree.Task("set-where", "set-where"),
                BehaviorTree.Task("set-state", "set-state"),
                BehaviorTree.Task("clear", "clear")
            ),
            Layout,
            actions => {
                TreeHarness.Probes(actions);
                actions.Register("set-count", SetBlackboardValueTask.Number(Layout.Key("count"), 7f));
                actions.Register("set-where", SetBlackboardValueTask.At(Layout.Key("where"), new(1f, 2f, 3f)));
                actions.Register("set-state", SetBlackboardValueTask.Word(Layout.Key("state"), Symbol.Intern("hunt")));
                actions.Register("clear", new ClearBlackboardValueTask(Layout.Key("count")));
            }
        );

        harness.Step();

        Assert.False(harness.Board.IsSet(Layout.Key("count")));
        Assert.Equal(new Vector3(1f, 2f, 3f), harness.Board.GetVector3(Layout.Key("where")));
        Assert.Equal(Symbol.Intern("hunt"), harness.Board.GetSymbol(Layout.Key("state")));
    }

    [Fact]
    public void CopyingOneKeyIntoAnotherFailsWhenTheSourceIsUnset() {
        using var harness = TreeHarness.For(
            BehaviorTree.Sequence("root", BehaviorTree.Task("copy", "copy")),
            Layout,
            actions => {
                TreeHarness.Probes(actions);
                actions.Register("copy", SetBlackboardValueTask.Copy(Layout.Key("count"), Layout.Key("count")));
            }
        );

        harness.Step();
        Assert.Equal(ActionStatus.Failed, harness.Tree.LastResult);
    }

    [Fact]
    public void LogNarratesIntoTheDebugRecord() {
        using var harness = TreeHarness.For(
            BehaviorTree.Sequence("root", BehaviorTree.Task("say", "log")),
            Layout,
            actions => {
                TreeHarness.Probes(actions);
                actions.Register("log", new LogTask(Symbol.Intern("giving-up")));
            }
        );

        harness.Debug.Enabled = true;
        harness.Step();

        var records = new AgentDebugRecord[32];
        var written = harness.Debug.CopyTo(records);

        Assert.Contains(records[..written], record => record.Action == Symbol.Intern("giving-up"));
    }

    /// <summary>
    ///     A dynamic subtree gets an instance of its own, because a tree named by a key is not known
    ///     until the agent runs and therefore cannot be spliced.
    /// </summary>
    [Fact]
    public void RunSubtreeDynamicRunsTheTreeAKeyNames() {
        var library = new BehaviorTreeLibrary();
        var actions = TreeHarness.Probes(new());

        actions.Register(
            "run-dynamic",
            new RunSubtreeDynamicTask(Layout.Key("tree"), library),
            RunSubtreeDynamicTask.StateSize
        );

        library.Add(
            BehaviorTreeCompiler.Compile(
                BehaviorTree.Asset("inner", BehaviorTree.Sequence("inner-root", BehaviorTree.Task("deep", "running"))),
                actions,
                Layout
            )
        );

        using var harness = TreeHarness.For(
            BehaviorTree.Selector(
                "root",
                BehaviorTree.Task("call", "run-dynamic"),
                BehaviorTree.Task("fallback", "succeed")
            ),
            Layout,
            registry => {
                TreeHarness.Probes(registry);
                registry.Register(
                    "run-dynamic",
                    new RunSubtreeDynamicTask(Layout.Key("tree"), library),
                    RunSubtreeDynamicTask.StateSize
                );
            }
        );

        // No tree named: the task fails and the selector moves on.
        harness.Step();
        Assert.Equal(1, harness.Probe(2).Starts);

        harness.Board.SetSymbol(Layout.Key("tree"), Symbol.Intern("inner"));
        harness.Steps(3);

        Assert.Equal("call", harness.Active);
    }
}

public class AiSystemTreeTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("alert", BlackboardValueType.Bool)
        .Build();

    [Fact]
    public void AnAgentNamingATreeRunsIt() {
        var (world, system) = Build();
        var entity = world.Create(AiAgent.Thinking(0));

        system.Step(world, Frame(0));

        var tree = system.TreeOf(world.Read<AiAgent>(entity));

        Assert.NotNull(tree);
        Assert.Equal("patrol", tree!.Template[tree.ActiveNode].Name.ToString());
        Assert.Equal(ActionStatus.Running, world.Read<AiAgent>(entity).Status);

        world.Dispose();
        system.Dispose();
    }

    [Fact]
    public void ATreeAgentAndAnActionAgentShareOneSystem() {
        var (world, system) = Build();
        var thinker = world.Create(AiAgent.Thinking(0));
        var doer = world.Create(AiAgent.Running(system.Actions.TryGetIndex(Symbol.Intern("running"), out var index) ? index : (ushort)0));

        system.Step(world, Frame(0));

        Assert.Equal(2, system.Population);
        Assert.NotNull(system.TreeOf(world.Read<AiAgent>(thinker)));
        Assert.Null(system.TreeOf(world.Read<AiAgent>(doer)));

        world.Dispose();
        system.Dispose();
    }

    [Fact]
    public void ADestroyedTreeAgentGivesItsSlotAndItsBlockBack() {
        var (world, system) = Build();
        var entity = world.Create(AiAgent.Thinking(0));

        system.Step(world, Frame(0));

        var rented = system.Memory.RentedCount;

        world.Destroy(entity);
        system.Step(world, Frame(1));

        Assert.Equal(0, system.Population);

        world.Create(AiAgent.Thinking(0));
        system.Step(world, Frame(2));

        Assert.Equal(1, system.Population);
        Assert.Equal(rented, system.Memory.RentedCount);

        world.Dispose();
        system.Dispose();
    }

    [Fact]
    public void TheDebugRecordSaysWhichTreeAndWhichNode() {
        var (world, system) = Build();

        world.Create(AiAgent.Thinking(0));
        system.Debug.Enabled = true;
        system.Step(world, Frame(0));

        var records = new AgentDebugRecord[16];
        var written = system.Debug.CopyTo(records);

        Assert.Contains(
            records[..written],
            record => record.Planner == AiPlanner.BehaviorTree && record.Reason == Symbol.Intern("guard")
        );

        world.Dispose();
        system.Dispose();
    }

    static (World World, AiSystem System) Build() {
        var actions = TreeHarness.Probes(new());
        var system = new AiSystem(actions, Layout) { Governor = new UnboundedGovernor() };

        system.Trees.Add(
            BehaviorTreeCompiler.Compile(
                BehaviorTree.Asset(
                    "guard",
                    BehaviorTree.Selector(
                        "root",
                        BehaviorTree.Task("respond", "running").With(BlackboardDecorator.Bool(Layout.Key("alert"))),
                        BehaviorTree.Task("patrol", "running")
                    )
                ),
                actions,
                Layout
            )
        );

        return (new World("system-tree"), system);
    }

    static GameTime Frame(int index) => new(
        TimeSpan.FromSeconds(index / 60.0),
        TimeSpan.FromSeconds(1 / 60.0),
        TimeSpan.FromSeconds(1 / 60.0),
        index,
        1f
    );
}
