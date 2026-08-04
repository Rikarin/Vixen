// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

public class BehaviorTreeAbortTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("alarm", BlackboardValueType.Bool)
        .Add("safe", BlackboardValueType.Bool)
        .Build();

    /// <summary>
    ///     <c>LowerPriority</c>: a higher-priority branch whose condition starts passing takes over
    ///     from whatever was running after it.
    /// </summary>
    [Fact]
    public void ALowerPriorityObserverTakesOverFromABranchAfterIt() {
        var alarm = Layout.Key("alarm");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("respond", "running").With(BlackboardDecorator.Bool(alarm, true, ObserverAborts.LowerPriority)),
                Task("patrol", "running")
            )
        );

        harness.Step();
        Assert.Equal("patrol", harness.Active);

        harness.Board.SetBool(alarm, true);
        harness.Step();

        Assert.Equal("respond", harness.Active);
        Assert.Equal(1, harness.Probe(2).Aborts);
    }

    /// <summary>
    ///     <c>Self</c>: a running branch whose condition stops holding is torn down, and the
    ///     composite goes on to the next child.
    /// </summary>
    [Fact]
    public void ASelfObserverTearsDownItsOwnBranch() {
        var alarm = Layout.Key("alarm");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("respond", "running").With(BlackboardDecorator.Bool(alarm, true, ObserverAborts.Self)),
                Task("patrol", "running")
            )
        );

        harness.Board.SetBool(alarm, true);
        harness.Step();
        Assert.Equal("respond", harness.Active);

        harness.Board.SetBool(alarm, false);
        harness.Step();

        Assert.Equal("patrol", harness.Active);
        Assert.Equal(1, harness.Probe(1).Aborts);
    }

    [Fact]
    public void ANoneObserverNeverInterruptsAnything() {
        var alarm = Layout.Key("alarm");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("respond", "running").With(BlackboardDecorator.Bool(alarm)),
                Task("patrol", "running")
            )
        );

        harness.Step();
        harness.Board.SetBool(alarm, true);
        harness.Steps(5);

        Assert.Equal("patrol", harness.Active);
        Assert.Equal(0, harness.Probe(1).Starts);
    }

    /// <summary>
    ///     ⚠ The abort is deferred by exactly one step, and that latency is a stated cost rather than
    ///     a bug: a task writes its own results during its tick, and tearing it down from inside that
    ///     write would destroy the state of the thing currently executing.
    /// </summary>
    [Fact]
    public void AConditionThatGoesFalseDuringATickTakesEffectOnTheNextOne() {
        var alarm = Layout.Key("alarm");
        var writer = new KeyWritingTask(alarm, false);

        using var harness = TreeHarness.For(
            BehaviorTree.Selector(
                "root",
                BehaviorTree
                    .Task("respond", "write")
                    .With(BlackboardDecorator.Bool(alarm, true, ObserverAborts.Self)),
                BehaviorTree.Task("patrol", "running")
            ),
            Layout,
            actions => {
                TreeHarness.Probes(actions);
                actions.Register("write", writer);
            }
        );

        harness.Board.SetBool(alarm, true);
        harness.Step();

        // It cleared its own condition mid-tick, and is still the active node at the end of the step.
        Assert.Equal("respond", harness.Active);
        Assert.Equal(1, writer.Writes);

        harness.Step();
        Assert.Equal("patrol", harness.Active);
    }

    /// <summary>
    ///     ⚠ Unity's scope rule, and the reason it was taken over Unreal's: an observer reaches its
    ///     own parent composite's children and no further.
    /// </summary>
    [Fact]
    public void AnObserverDoesNotReachOutsideItsOwnParentComposite() {
        var alarm = Layout.Key("alarm");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                BehaviorTree.Sequence(
                    "left",
                    Task("gated", "running").With(BlackboardDecorator.Bool(alarm, true, ObserverAborts.LowerPriority))
                ),
                BehaviorTree.Sequence("right", Task("busy", "running"))
            )
        );

        harness.Step();
        Assert.Equal("busy", harness.Active);

        // `gated`'s decorator sits under `left`, and the running node is under `right`. Unreal's
        // wider rule would abort here; the scoped rule does not, which is what makes what a decorator
        // can interrupt drawable.
        harness.Board.SetBool(alarm, true);
        harness.Steps(3);

        Assert.Equal("busy", harness.Active);
        Assert.Equal(0, harness.Probe(2).Starts);
    }

    [Fact]
    public void AnObserverInsideTheSameCompositeDoesReach() {
        var alarm = Layout.Key("alarm");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("gated", "running").With(BlackboardDecorator.Bool(alarm, true, ObserverAborts.LowerPriority)),
                BehaviorTree.Sequence("right", Task("busy", "running"))
            )
        );

        harness.Step();
        Assert.Equal("busy", harness.Active);

        harness.Board.SetBool(alarm, true);
        harness.Step();

        Assert.Equal("gated", harness.Active);
    }

    /// <summary>Ties break on the index, so the higher-priority composite is the one that restarts.</summary>
    [Fact]
    public void TwoObserversFiringAtOnceRestartTheLowerIndexedScope() {
        var alarm = Layout.Key("alarm");
        var safe = Layout.Key("safe");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("outer", "running").With(BlackboardDecorator.Bool(alarm, true, ObserverAborts.LowerPriority)),
                BehaviorTree.Selector(
                    "inner",
                    Task("nested", "running").With(BlackboardDecorator.Bool(safe, true, ObserverAborts.LowerPriority)),
                    Task("busy", "running")
                )
            )
        );

        harness.Step();
        Assert.Equal("busy", harness.Active);

        harness.Board.SetBool(alarm, true);
        harness.Board.SetBool(safe, true);
        harness.Step();

        Assert.Equal("outer", harness.Active);
    }

    /// <summary>
    ///     A decorator that flips and flips back between two steps has not changed, so nothing aborts.
    /// </summary>
    [Fact]
    public void AKeyThatChangesAndChangesBackDoesNotAbort() {
        var alarm = Layout.Key("alarm");

        using var harness = Build(
            BehaviorTree.Selector(
                "root",
                Task("respond", "running").With(BlackboardDecorator.Bool(alarm, true, ObserverAborts.Both)),
                Task("patrol", "running")
            )
        );

        harness.Step();

        harness.Board.SetBool(alarm, true);
        harness.Board.SetBool(alarm, false);
        harness.Step();

        Assert.Equal("patrol", harness.Active);
        Assert.Equal(0, harness.Probe(1).Starts);
    }

    [Fact]
    public void AbortingTheWholeTreeTellsEveryRunningNode() {
        using var harness = Build(
            BehaviorTree.Selector("root", BehaviorTree.Sequence("branch", Task("busy", "running")))
        );

        harness.Step();

        var context = harness.Context();

        harness.Tree.Abort(in context);

        Assert.Equal(1, harness.Probe(2).Aborts);
        Assert.Equal(-1, harness.Tree.ActiveNode);

        // And it starts again from the root next step, rather than being left dead.
        harness.Step();
        Assert.Equal("busy", harness.Active);
    }

    static TreeHarness Build(BehaviorNodeDefinition root) =>
        TreeHarness.For(root, Layout, actions => TreeHarness.Probes(actions));

    static BehaviorNodeDefinition Task(string name, string action) => BehaviorTree.Task(name, action);

    /// <summary>A task that writes a key during its own tick — the case D6's deferral exists for.</summary>
    sealed class KeyWritingTask(BlackboardKey key, bool value) : IAgentAction {
        public int Writes { get; private set; }

        public void Start(in AgentContext context, Span<byte> state) { }

        public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
            context.Blackboard.SetBool(key, value);
            Writes++;

            return ActionStatus.Running;
        }

        public void Abort(in AgentContext context, Span<byte> state) { }
    }
}
