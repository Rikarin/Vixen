// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     P1's second exit criterion, and doc 37's answer to its own first risk: randomly generated
///     trees with randomly placed observers, driven by random blackboard writes, asserting that the
///     node which ends up active is the lowest-index runnable one.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Generated rather than authored, because the bug this is looking for is a case nobody
///         thought of.</b> "Abort semantics are subtly wrong and nobody notices for a year" is A-R1,
///         and its symptom is <i>the AI sometimes gets stuck</i> — which no hand-written test
///         reproduces, because a hand-written test asserts the case its author had in mind.
///     </para>
///     <para>
///         ⚠ <b>"Lowest-index runnable" needs one qualification, and finding it is what this test was
///         worth.</b> Doc 37 states the criterion in that form and separately adopts Unity's
///         <i>scoped</i> abort rule over Unreal's wider one — and the two are in tension. A decorator
///         reaches the siblings under its own parent composite; if the agent is off in a different
///         branch entirely, that composite is not running and there is nothing listening. So a
///         condition that becomes true deep inside a branch the agent has already walked past does
///         <b>not</b> pull it back, and a from-scratch walk would say it should.
///     </para>
///     <para>
///         That is the documented cost of the drawable rule rather than a defect, so the oracle here
///         is exact about it: a second instance is stepped from scratch on the same blackboard, and
///         where the two disagree the test requires the disagreement to be <i>explained</i> — the
///         node the fresh walk chose must be deeper than any composite the driven agent had open. A
///         disagreement about a direct child of a shared composite is a bug, and this fails on it.
///     </para>
/// </remarks>
public class BehaviorTreeAbortOrderingTests {
    const int Trees = 200;
    const int StepsPerTree = 40;

    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("k0", BlackboardValueType.Bool)
        .Add("k1", BlackboardValueType.Bool)
        .Add("k2", BlackboardValueType.Bool)
        .Add("k3", BlackboardValueType.Bool)
        .Build();

    [Fact]
    public void TheActiveNodeIsAlwaysTheLowestIndexRunnableOneInScope() {
        var random = new Random(20260804);
        var checkedSteps = 0;
        var aborts = 0;
        var scoped = 0;

        for (var pass = 0; pass < Trees; pass++) {
            var root = Generate(random, depth: 0, counter: new());

            using var harness = TreeHarness.For(root, Layout, actions => TreeHarness.Probes(actions));

            var template = harness.Tree.Template;
            var fresh = new BehaviorTreeInstance(template, harness.Memory);

            for (var step = 0; step < StepsPerTree; step++) {
                harness.Board.SetBool(new((ushort)random.Next(Layout.Count)), random.Next(2) == 1);

                var before = harness.Tree.ActiveNode;

                // Twice: the abort is deferred by exactly one step, which is the latency doc 37 § D6
                // states and the reason a condition is checked on the step after the write.
                harness.Step();
                harness.Step();

                if (harness.Tree.ActiveNode != before) {
                    aborts++;
                }

                Assert.False(harness.Tree.Overran, $"pass {pass} step {step} ran away.");

                var driven = harness.Tree.ActiveNode;

                Assert.True(driven < 0 || template[driven].Kind == BehaviorNodeKind.Task, "a composite is active.");

                // Every gate on the way down to what is running still holds. A tree running something
                // its own conditions forbid is the failure this catches whatever else it misses.
                for (var walk = driven; walk >= 0; walk = template[walk].Parent) {
                    Assert.True(Passes(harness, walk), $"pass {pass} step {step}: node {walk} is gated off and running.");
                }

                // And what a walk from the root would pick, with no history at all.
                var context = harness.Context();

                fresh.Reset();
                fresh.Step(in context, 0f);

                if (fresh.ActiveNode != driven) {
                    scoped++;
                    AssertExplainedByScope(harness, fresh.ActiveNode, driven, pass, step);
                }

                checkedSteps++;
            }

            fresh.Release(harness.Context());
        }

        // The test is only worth something if the generated trees actually aborted, so it says so.
        Assert.Equal(Trees * StepsPerTree, checkedSteps);
        Assert.True(aborts > Trees, $"only {aborts} aborts happened across {Trees} trees.");
    }

    /// <summary>
    ///     A disagreement with the from-scratch walk is only allowed where the scoped rule says it
    ///     is: the node the fresh walk chose must sit <i>below</i> the composites the driven agent
    ///     had open, so no observer of the driven agent's was listening for it.
    /// </summary>
    static void AssertExplainedByScope(TreeHarness harness, int fresh, int driven, int pass, int step) {
        var template = harness.Tree.Template;
        var reason = string.Create(
            CultureInfo.InvariantCulture,
            $"pass {pass} step {step}: a walk from the root reaches node {fresh}, node {driven} is running, "
            + $"and the difference is not the scope rule.{Environment.NewLine}{template.Dump()}"
        );

        Assert.True(fresh >= 0, reason);
        Assert.True(driven >= 0, reason);

        var ancestor = LowestCommonAncestor(template, fresh, driven);

        Assert.True(ancestor >= 0, reason);

        // The child of that shared composite which leads to the fresh answer. If the fresh answer
        // *is* that child, then it was a sibling of what was running, the observer had it in scope,
        // and it should have taken over — so a disagreement there is a real abort bug.
        var child = fresh;

        while (template[child].Parent != ancestor) {
            child = template[child].Parent;
        }

        Assert.True(child != fresh, reason);
    }

    static int LowestCommonAncestor(BehaviorTreeTemplate template, int left, int right) {
        for (var walk = left; walk >= 0; walk = template[walk].Parent) {
            if (template.Contains(walk, right)) {
                return walk == left ? template[walk].Parent : walk;
            }
        }

        return -1;
    }

    static bool Passes(TreeHarness harness, int node) {
        var template = harness.Tree.Template;

        for (var slot = template[node].DecoratorStart;
            slot < template[node].DecoratorStart + template[node].DecoratorCount;
            slot++) {
            if (!template.Decorators[slot].Decorator.Evaluate(new(harness.Context(), harness.Tree, node), default)) {
                return false;
            }
        }

        return true;
    }

    /// <summary>A tree of selectors over leaves, with observing conditions scattered through it.</summary>
    /// <remarks>
    ///     Selectors only, and every leaf runs for ever. That is not a simplification of the problem —
    ///     it is what lets an oracle exist at all: with no node ever finishing, "which node should be
    ///     running" has one answer that does not depend on history, so a disagreement is about the
    ///     abort machinery rather than about resumption. Every decorator gets
    ///     <see cref="ObserverAborts.Both" />, so a condition changing in either direction is
    ///     something the tree is supposed to notice.
    /// </remarks>
    static BehaviorNodeDefinition Generate(Random random, int depth, Counter counter) {
        var children = new List<BehaviorNodeDefinition>();
        var count = random.Next(2, 4);

        for (var index = 0; index < count; index++) {
            var child = depth < 2 && random.Next(3) == 0
                ? Generate(random, depth + 1, counter)
                : BehaviorTree.Task($"leaf{counter.Next()}", "running");

            if (random.Next(2) == 0) {
                child.With(
                    BlackboardDecorator.Bool(
                        new((ushort)random.Next(Layout.Count)),
                        random.Next(2) == 1,
                        ObserverAborts.Both
                    )
                );
            }

            children.Add(child);
        }

        return BehaviorTree.Selector($"branch{counter.Next()}", [.. children]);
    }

    sealed class Counter {
        int next;

        public int Next() => next++;
    }
}
