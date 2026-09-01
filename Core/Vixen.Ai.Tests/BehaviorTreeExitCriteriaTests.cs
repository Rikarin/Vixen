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
///     P1's first exit criterion, measured both ways in one test: a thousand idle agents on a
///     ten-node tree cost less than a thousand agents on a one-node tree cost under an implementation
///     that traverses from the root every frame.
/// </summary>
/// <remarks>
///     ⚠ <b>The comparison is against a traversal, not against a number.</b> A wall-clock threshold
///     would be a different number on every machine and would fail in CI for reasons that are not the
///     code's. What is asserted instead is the shape of the claim in doc 37 § D7: the event-driven
///     tree does no work when nothing has changed, and a traversing one does work proportional to its
///     size whether or not anything has.
/// </remarks>
public class BehaviorTreeCostTests {
    const int Agents = 1_000;

    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("alert", BlackboardValueType.Bool)
        .Build();

    [Fact]
    public void AThousandIdleAgentsOnATenNodeTreeCostLessThanATraversalOfAOneNodeOne() {
        using var deep = Build(TenNodes(), Agents);
        using var flat = Build(OneNode(), Agents);

        // Settle both: the first step is where every agent walks down to its leaf.
        deep.Step(0);
        flat.Step(0);

        var evented = 0;
        var traversed = 0;

        for (var frame = 1; frame <= 60; frame++) {
            evented += deep.Step(frame);
            traversed += flat.Traversal();
        }

        Assert.True(deep.Template.Count >= 10, $"the deep tree has {deep.Template.Count} nodes.");
        Assert.Equal(0, evented);
        Assert.Equal(Agents * 60, traversed);

        Assert.True(
            evented < traversed,
            $"the event-driven tree visited {evented} nodes over sixty frames of a thousand agents, "
            + $"and a traversal of a one-node tree visited {traversed}."
        );
    }

    /// <summary>
    ///     And the same claim as an allocation: a settled tree allocates nothing, at a thousand
    ///     agents, for a whole frame.
    /// </summary>
    [Fact]
    public void AThousandSettledAgentsAllocateNothingInAFrame() {
        using var fleet = Build(TenNodes(), Agents);

        fleet.Step(0);

        var frame = 1;
        Measured.NothingAllocated(() => fleet.Step(frame++), warmUp: 20, passes: 100);
    }

    /// <summary>
    ///     ⚠ The template/instance test, at the tree level: a hundred agents on one template, each
    ///     driven to a different node, all asserted independently. This is the one that fails if any
    ///     node keeps state on itself.
    /// </summary>
    [Fact]
    public void AHundredAgentsOnOneTemplateHoldAHundredIndependentPositions() {
        var actions = TreeHarness.Probes(new());
        var alert = Layout.Key("alert");
        var template = BehaviorTreeCompiler.Compile(
            BehaviorTree.Asset(
                "guard",
                BehaviorTree.Selector(
                    "root",
                    BehaviorTree.Task("respond", "running").With(BlackboardDecorator.Bool(alert)),
                    BehaviorTree.Task("patrol", "running")
                )
            ),
            actions,
            Layout
        );

        using var world = new World("independent");
        var pool = new AgentMemoryPool();
        var instances = new List<(BehaviorTreeInstance Tree, Blackboard Board, Entity Entity)>();

        for (var index = 0; index < 100; index++) {
            var entity = world.Create();
            var board = new Blackboard(Layout);

            // Every other agent takes the other branch.
            board.SetBool(alert, index % 2 == 0);
            instances.Add((new(template, pool), board, entity));
        }

        foreach (var (tree, board, entity) in instances) {
            var context = Context(world, entity, board, actions, 0);

            tree.Step(in context, 1f / 60f);
        }

        for (var index = 0; index < instances.Count; index++) {
            var (tree, _, _) = instances[index];

            Assert.Equal(index % 2 == 0 ? 1 : 2, tree.ActiveNode);
            Assert.Equal(1, MemoryMarshal.Read<ProbeTask.ProbeState>(tree.StateOf(tree.ActiveNode)).Ticks);
        }

        // …and each one's node memory really is a different range of the pool.
        Assert.Equal(100, instances.Select(pair => pair.Tree.Handle.Index).Distinct().Count());
    }

    /// <summary>
    ///     The same tree, the same inputs, on two worlds built in a different order, chooses the same
    ///     nodes in the same order.
    /// </summary>
    [Fact]
    public void TheSameTreeOnTwoWorldsProducesTheIdenticalSequenceOfActiveNodes() {
        var first = Run(decoys: 0);
        var second = Run(decoys: 37);

        Assert.Equal(first, second);
        Assert.NotEmpty(first);

        static List<int> Run(int decoys) {
            var actions = TreeHarness.Probes(new());
            var template = BehaviorTreeCompiler.Compile(
                BehaviorTree.Asset(
                    "roll",
                    BehaviorTree.RandomSelector(
                        "root",
                        BehaviorTree.Task("a", "fail"),
                        BehaviorTree.Task("b", "fail"),
                        BehaviorTree.Task("c", "succeed-after-2")
                    )
                ),
                actions,
                Layout
            );

            using var world = new World($"determinism-{decoys}");

            // Entities created before the agent, so its id and therefore its whole random stream is
            // the same on both — which is the property `AgentRandom` exists to give and the reason a
            // seed is keyed on identity rather than on a slot.
            for (var index = 0; index < decoys; index++) {
                world.Destroy(world.Create());
            }

            var entity = world.Create();
            var board = new Blackboard(Layout);
            var tree = new BehaviorTreeInstance(template, new());
            var visited = new List<int>();

            for (var frame = 0; frame < 20; frame++) {
                var context = Context(world, entity, board, actions, frame);

                tree.Step(in context, 1f / 60f);
                visited.Add(tree.ActiveNode);
            }

            return visited;
        }
    }

    static BehaviorNodeDefinition TenNodes() =>
        BehaviorTree.Selector(
            "root",
            BehaviorTree.Sequence(
                "left",
                BehaviorTree.Task("a", "fail"),
                BehaviorTree.Task("b", "running")
            ),
            BehaviorTree.Sequence(
                "right",
                BehaviorTree.Sequence(
                    "deeper",
                    BehaviorTree.Task("c", "succeed"),
                    BehaviorTree.Task("d", "running")
                ),
                BehaviorTree.Task("e", "running")
            ),
            BehaviorTree.Task("f", "running")
        );

    static BehaviorNodeDefinition OneNode() => BehaviorTree.Selector("root", BehaviorTree.Task("only", "running"));

    static Fleet Build(BehaviorNodeDefinition root, int count) => new(root, count, Layout);

    static AgentContext Context(World world, Entity entity, Blackboard board, AgentActionRegistry actions, int frame) =>
        new(world, entity, board, null, Frame(frame), AgentRandom.SeedOf(entity), actions);

    static GameTime Frame(int index) => new(
        TimeSpan.FromSeconds(index / 60.0),
        TimeSpan.FromSeconds(1 / 60.0),
        TimeSpan.FromSeconds(1 / 60.0),
        index,
        1f
    );

    /// <summary>A world full of agents on one tree, and the two ways of counting what a frame cost.</summary>
    sealed class Fleet : IDisposable {
        readonly AiSystem system;
        readonly World world;
        readonly QueryDescription query = new QueryDescription().WithAll<AiAgent>();

        public Fleet(BehaviorNodeDefinition root, int count, BlackboardLayout layout) {
            var actions = TreeHarness.Probes(new());

            system = new(actions, layout) { Governor = new UnboundedGovernor() };
            world = new("cost");
            Template = BehaviorTreeCompiler.Compile(BehaviorTree.Asset("tree", root), actions, layout);
            system.Trees.Add(Template);

            for (var index = 0; index < count; index++) {
                world.Create(AiAgent.Thinking(0));
            }
        }

        public BehaviorTreeTemplate Template { get; }

        /// <summary>Steps every agent, and totals the nodes their trees actually visited.</summary>
        public int Step(int frame) {
            system.Step(world, Frame(frame));

            var visited = 0;

            foreach (var chunk in world.Chunks(query)) {
                var values = chunk.ReadValues<AiAgent>();

                for (var index = 0; index < chunk.Count; index++) {
                    visited += system.TreeOf(values[index])?.LastTransitions ?? 0;
                }
            }

            return visited;
        }

        /// <summary>
        ///     What the same frame would cost if the tree were walked from the root every time, which
        ///     is what a classic behaviour tree does.
        /// </summary>
        public int Traversal() {
            var visited = 0;

            foreach (var chunk in world.Chunks(query)) {
                var values = chunk.ReadValues<AiAgent>();

                for (var index = 0; index < chunk.Count; index++) {
                    if (system.TreeOf(values[index]) is { } tree) {
                        // A traversal visits the path from the root to the leaf every frame, whether
                        // or not anything changed. One node is the floor, and it is what a one-node
                        // tree gives.
                        visited += Math.Max(tree.Template.Count - 1, 1);
                    }
                }
            }

            return visited;
        }

        public void Dispose() {
            system.Dispose();
            world.Dispose();
        }
    }
}
