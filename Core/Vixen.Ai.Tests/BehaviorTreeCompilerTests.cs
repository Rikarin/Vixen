// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

public class BehaviorTreeCompilerTests {
    static readonly BlackboardLayout Layout = new BlackboardLayoutBuilder()
        .Add("alert", BlackboardValueType.Bool)
        .Add("target", BlackboardValueType.Entity)
        .Add("distance", BlackboardValueType.Float)
        .Build();

    /// <summary>
    ///     The layout rule the whole subsystem rests on: pre-order, so index is priority, and
    ///     <c>LastDescendant</c>, so a subtree is a contiguous range.
    /// </summary>
    [Fact]
    public void NodesArePreOrderAndSubtreesAreContiguous() {
        var template = Compile(
            BehaviorTree.Selector(
                "root",
                BehaviorTree.Sequence("left", Probe("a"), Probe("b")),
                BehaviorTree.Sequence("right", Probe("c"))
            )
        );

        Assert.Equal(6, template.Count);
        Assert.Equal(["root", "left", "a", "b", "right", "c"], Names(template));

        // root spans everything; `left` spans itself and its two children and stops before `right`.
        Assert.Equal(5, template[0].LastDescendant);
        Assert.Equal(3, template[1].LastDescendant);
        Assert.Equal(2, template[2].LastDescendant);
        Assert.Equal(5, template[4].LastDescendant);

        Assert.True(template.Contains(1, 3));
        Assert.False(template.Contains(1, 4));
        Assert.True(template.Contains(0, 5));
    }

    [Fact]
    public void ChildOrderIsWhatWasAuthoredAndNothingElse() {
        var template = Compile(
            BehaviorTree.Sequence("root", Probe("third"), Probe("first"), Probe("second"))
        );

        Assert.Equal(["root", "third", "first", "second"], Names(template));
    }

    [Fact]
    public void EveryNodeDecoratorAndServiceGetsAByteRangeOfItsOwn() {
        var template = Compile(
            BehaviorTree.Sequence("root", Probe("a").With(new CooldownDecorator(1f)), Probe("b"))
        );

        var ranges = new List<(int Start, int End)>();

        foreach (var node in template.Nodes) {
            if (node.MemorySize > 0) {
                ranges.Add((node.MemoryOffset, node.MemoryOffset + node.MemorySize));
            }
        }

        foreach (var slot in template.Decorators) {
            if (slot.MemorySize > 0) {
                ranges.Add((slot.MemoryOffset, slot.MemoryOffset + slot.MemorySize));
            }
        }

        ranges.Sort();

        for (var index = 1; index < ranges.Count; index++) {
            Assert.True(ranges[index - 1].End <= ranges[index].Start, "two nodes share bytes.");
        }

        Assert.True(ranges[^1].End <= template.MemorySize);
    }

    /// <summary>
    ///     ⚠ A static subtree is spliced rather than referenced, which is what keeps pre-order equal
    ///     to priority across the boundary.
    /// </summary>
    [Fact]
    public void AStaticSubtreeIsSplicedIntoThePreOrder() {
        var inner = BehaviorTree.Asset("inner", BehaviorTree.Sequence("inner-root", Probe("x"), Probe("y")));
        var template = Compile(
            BehaviorTree.Selector("root", Probe("before"), BehaviorTree.Subtree("call", inner), Probe("after"))
        );

        Assert.Equal(["root", "before", "inner-root", "x", "y", "after"], Names(template));

        // And the boundary is invisible to the range test, which is the whole point of splicing.
        Assert.True(template.Contains(0, 4));
        Assert.Equal(5, template[0].LastDescendant);
    }

    [Fact]
    public void ASubtreeThatContainsItselfIsRefusedByName() {
        var actions = TreeHarness.Probes(new());
        var root = BehaviorTree.Selector("root", Probe("a"));
        var asset = BehaviorTree.Asset("looping", root);

        root.Add(BehaviorTree.Subtree("again", asset));

        Assert.False(BehaviorTreeCompiler.TryCompile(asset, actions, Layout, out var diagnostics, out var template));
        Assert.Null(template);
        Assert.Contains(diagnostics, problem => problem.Message.Contains("itself", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ An observer with nothing to observe can never fire, which is the failure that reads as
    ///     "the AI sometimes gets stuck" rather than as a mistake.
    /// </summary>
    [Fact]
    public void ADecoratorThatAbortsWithoutReadingAKeyIsRefused() {
        var actions = TreeHarness.Probes(new());
        var asset = BehaviorTree.Asset(
            "silent",
            BehaviorTree.Selector("root", Probe("a").With(new AbortsNothingDecorator()))
        );

        Assert.False(BehaviorTreeCompiler.TryCompile(asset, actions, Layout, out var diagnostics, out _));
        Assert.Contains(diagnostics, problem => problem.Message.Contains("nothing can ever wake it", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnknownActionIsRefusedByName() {
        var actions = TreeHarness.Probes(new());
        var asset = BehaviorTree.Asset("missing", BehaviorTree.Selector("root", BehaviorTree.Task("nope")));

        Assert.False(BehaviorTreeCompiler.TryCompile(asset, actions, Layout, out var diagnostics, out _));
        Assert.Contains(diagnostics, problem => problem.Message.Contains("'nope'", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyCompositeAndAServiceOnATaskAreRefused() {
        var actions = TreeHarness.Probes(new());
        var empty = BehaviorTree.Asset("empty", BehaviorTree.Selector("root"));

        Assert.False(BehaviorTreeCompiler.TryCompile(empty, actions, Layout, out var first, out _));
        Assert.Contains(first, problem => problem.Message.Contains("no children", StringComparison.Ordinal));

        var onTask = BehaviorTree.Asset(
            "on-task",
            BehaviorTree.Selector("root", Probe("a").With(new NoopService(), 1f))
        );

        Assert.False(BehaviorTreeCompiler.TryCompile(onTask, actions, Layout, out var second, out _));
        Assert.Contains(second, problem => problem.Message.Contains("composite, not to a task", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ Unreal's restriction on parallels, kept: two branches whose decorators can abort each
    ///     other make the abort scope undefinable.
    /// </summary>
    [Fact]
    public void AParallelWithoutATaskAsItsFirstChildIsRefused() {
        var actions = TreeHarness.Probes(new());
        var asset = BehaviorTree.Asset(
            "bad-parallel",
            BehaviorTree.Parallel(
                "root",
                ParallelFinishMode.Immediate,
                BehaviorTree.Sequence("not-a-task", Probe("a")),
                Probe("b")
            )
        );

        Assert.False(BehaviorTreeCompiler.TryCompile(asset, actions, Layout, out var diagnostics, out _));
        Assert.Contains(diagnostics, problem => problem.Message.Contains("must be a task", StringComparison.Ordinal));
    }

    [Fact]
    public void AServiceWithNoIntervalIsRefused() {
        var actions = TreeHarness.Probes(new());
        var asset = BehaviorTree.Asset(
            "bad-service",
            BehaviorTree.Selector("root", Probe("a")).With(new NoopService(), 0f)
        );

        Assert.False(BehaviorTreeCompiler.TryCompile(asset, actions, Layout, out var diagnostics, out _));
        Assert.Contains(diagnostics, problem => problem.Message.Contains("interval must be positive", StringComparison.Ordinal));
    }

    [Fact]
    public void TheObservedKeyTableIsFlattenedAndDeduplicated() {
        var alert = Layout.Key("alert");
        var target = Layout.Key("target");
        var template = Compile(
            BehaviorTree.Selector(
                "root",
                Probe("a").With(BlackboardDecorator.Bool(alert, true, ObserverAborts.Self)),
                Probe("b").With(BlackboardDecorator.Set(target, true, ObserverAborts.LowerPriority)),
                Probe("c").With(BlackboardDecorator.Bool(alert, false, ObserverAborts.Both))
            )
        );

        Assert.Equal(2, template.ObservedKeys.Length);
        Assert.Equal(1, template.KeysOf(template.Decorators[0]).Length);
        Assert.Equal(alert, template.KeysOf(template.Decorators[0])[0]);
    }

    [Fact]
    public void TheDumpIsStableAndNamesEveryNode() {
        var template = Compile(BehaviorTree.Sequence("root", Probe("a"), Probe("b")));
        var dump = template.Dump();

        Assert.Equal(template.Dump(), dump);
        Assert.Contains("Sequence root", dump, StringComparison.Ordinal);
        Assert.Contains("Task a", dump, StringComparison.Ordinal);
    }

    [Fact]
    public void CompileThrowsWithEveryProblemInTheMessage() {
        var actions = TreeHarness.Probes(new());
        var asset = BehaviorTree.Asset("bad", BehaviorTree.Selector("root"));

        var error = Assert.Throws<InvalidOperationException>(
            () => BehaviorTreeCompiler.Compile(asset, actions, Layout)
        );

        Assert.Contains("no children", error.Message, StringComparison.Ordinal);
    }

    static BehaviorTreeTemplate Compile(BehaviorNodeDefinition root) =>
        BehaviorTreeCompiler.Compile(BehaviorTree.Asset("test", root), TreeHarness.Probes(new()), Layout);

    static BehaviorNodeDefinition Probe(string name) => BehaviorTree.Task(name, "running");

    static string[] Names(BehaviorTreeTemplate template) {
        var names = new string[template.Count];

        for (var index = 0; index < template.Count; index++) {
            names[index] = template[index].Name.ToString();
        }

        return names;
    }

    sealed class AbortsNothingDecorator : BehaviorDecorator {
        public override ObserverAborts Aborts => ObserverAborts.Both;

        public override bool Evaluate(in BehaviorContext context, ReadOnlySpan<byte> state) => true;
    }

    sealed class NoopService : BehaviorService {
        public override void Tick(in BehaviorContext context, Span<byte> state, float delta) { }
    }
}

/// <summary>
///     ⚠ A resolver outlives a compile, and P4 found what that meant. A game compiles every
///     <c>.vxbt</c> it ships against one resolver, and two trees that both contain <c>Wait(1)</c> name
///     the same action — which is the sharing the action key exists for. Registering it a second time
///     threw, so the second tree in a project was a crash at start-up rather than a shared action.
/// </summary>
public class BehaviorTreeResolverReuseTests {
    [Fact]
    public void TwoTreesWithTheSameTaskShareOneRegisteredAction() {
        var resolver = new BehaviorTreeResolver();

        Assert.True(BehaviorTreeContentCompiler.TryCompile(Tree("first"), resolver, out _, out var one));
        Assert.True(BehaviorTreeContentCompiler.TryCompile(Tree("second"), resolver, out _, out var two));

        Assert.NotNull(one);
        Assert.NotNull(two);

        // One action for the two trees, plus the placeholder every resolver registers up front.
        Assert.Equal(2, resolver.Actions.Count);
    }

    [Fact]
    public void TwoTreesWithTheSameTaskAtDifferentSettingsGetTwoActions() {
        var resolver = new BehaviorTreeResolver();

        BehaviorTreeContentCompiler.TryCompile(Tree("first"), resolver, out _, out _);
        BehaviorTreeContentCompiler.TryCompile(Tree("second", seconds: "2"), resolver, out _, out _);

        Assert.Equal(3, resolver.Actions.Count);
    }

    static BehaviorTreeContent Tree(string name, string seconds = "1") => new() {
        Name = name,
        Root = new() {
            Name = "Root",
            Type = "Selector",
            Children = { new() { Name = "Wait", Type = "Wait", Fields = { ["Seconds"] = seconds } } }
        }
    };
}
