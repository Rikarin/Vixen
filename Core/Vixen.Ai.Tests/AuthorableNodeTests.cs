// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>
///     The three nodes that had runtime classes and no way to write them down.
/// </summary>
/// <remarks>
///     ⚠ <b>A node in doc 37 § Part 3 that a <c>.vxbt</c> cannot name is a node nobody can use.</b>
///     <c>CompositeDecorator</c>, <c>ConditionalLoopDecorator</c> and <c>RunUtilitySetTask</c> were all
///     built and tested as objects in P1 and P5, and none of them had a <see cref="BehaviorNodeSchema" />
///     entry — so the compiler refused them, the search popup never offered them, and the table's ✅
///     was about a class rather than about a feature. Found by reading the plan against the schema.
/// </remarks>
public class AuthorableNodeTests {
    [Fact]
    public void EveryNodeInThePlansTablesIsInTheSchema() {
        var schema = BehaviorNodeSchema.Default;

        // Every type doc 37 § Part 3 files under Vixen.Ai. The ones in the other three assemblies
        // register themselves, and their own tests assert that.
        string[] expected = [
            "Selector", "Sequence", "Parallel", "RandomSelector", "Priority",
            "Blackboard", "CompareEntries", "Composite", "Cooldown", "TagCooldown", "SetTagCooldown",
            "TimeLimit", "Loop", "ConditionalLoop", "ForceSuccess", "ForceFailure", "Inverter",
            "RandomChance", "Cone", "IsAtLocation",
            "UpdateBlackboard",
            "Wait", "WaitBlackboardTime", "FinishWith", "SetBlackboardValue", "ClearBlackboardValue",
            "RunSubtree", "RunSubtreeDynamic", "RunUtilitySet", "Log"
        ];

        var missing = expected.Where(type => !schema.TryGet(type, out _)).ToList();

        Assert.True(missing.Count == 0, $"the schema has no entry for {string.Join(", ", missing)}.");
    }

    /// <summary>
    ///     ⚠ The whole point of the node: three conditions on one branch, joined, with one abort
    ///     scope — rather than three decorators whose failure semantics compose and whose abort
    ///     semantics do not.
    /// </summary>
    [Fact]
    public void ACompositeConditionIsAuthoredAsNestedRows() {
        var content = Tree(
            new BehaviorAttachmentContent {
                Type = "Composite",
                Fields = { ["Logic"] = nameof(DecoratorLogic.And), ["Aborts"] = nameof(ObserverAborts.Both) },
                Children = {
                    Blackboard("visible", BlackboardTest.IsSet),
                    Blackboard("armed", BlackboardTest.IsSet)
                }
            }
        );

        var resolver = Resolver();

        Assert.True(BehaviorTreeContentCompiler.TryCompile(content, resolver, out var problems, out var template));
        Assert.Empty(problems);

        var slot = template!.Decorators[0];

        Assert.IsType<CompositeDecorator>(slot.Decorator);
        Assert.Equal(ObserverAborts.Both, slot.Aborts);

        // The union of what the operands read, so a write to either wakes the whole expression.
        Assert.Equal(2, template.KeysOf(in slot).Length);
    }

    [Fact]
    public void ACompositeConditionWithNothingUnderItIsADiagnostic() {
        var content = Tree(new BehaviorAttachmentContent { Type = "Composite" });

        Assert.False(Refused(content, out var problems));
        Assert.Contains(problems, problem => problem.Message.Contains("joins nothing", StringComparison.Ordinal));
    }

    /// <summary>⚠ One level. An expression tree of arbitrary depth is a thing the inspector cannot draw.</summary>
    [Fact]
    public void ACompositeConditionMayNotContainAnother() {
        var content = Tree(
            new BehaviorAttachmentContent {
                Type = "Composite",
                Children = {
                    Blackboard("visible", BlackboardTest.IsSet),
                    new BehaviorAttachmentContent { Type = "Composite" }
                }
            }
        );

        Assert.False(Refused(content, out var problems));
        Assert.Contains(problems, problem => problem.Message.Contains("cannot be nested", StringComparison.Ordinal));
    }

    [Fact]
    public void AConditionalLoopTakesExactlyOneDecoratorToTest() {
        var content = Tree(
            new BehaviorAttachmentContent {
                Type = "ConditionalLoop",
                Children = { Blackboard("hungry", BlackboardTest.IsSet) }
            }
        );

        Assert.True(BehaviorTreeContentCompiler.TryCompile(content, Resolver(), out var problems, out var template));
        Assert.Empty(problems);
        Assert.IsType<ConditionalLoopDecorator>(template!.Decorators[0].Decorator);

        var none = Tree(new BehaviorAttachmentContent { Type = "ConditionalLoop" });

        Assert.False(Refused(none, out var refused));
        Assert.Contains(refused, problem => problem.Message.Contains("exactly one", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A set is a live object, not a description, so the file names one and the game supplies
    ///     it — the bargain <c>PlaySound</c> and <c>DoesPathExist</c> already make.
    /// </summary>
    [Fact]
    public void ARunUtilitySetTaskNamesASetTheGameRegistered() {
        var resolver = Resolver();
        var set = new UtilitySet(
            Symbol.Intern("mood"),
            new UtilityAction(
                Symbol.Intern("idle"),
                resolver.Actions.Register("idle", new FinishWithTask(ActionStatus.Succeeded)),
                new UtilityConsideration(Symbol.Intern("calm"), UtilityInputs.Constant(1f), ResponseCurve.Identity)
            )
        );

        resolver.AddSet(set);

        var content = new BehaviorTreeContent {
            Name = "brain",
            Root = new() { Name = "run", Type = "RunUtilitySet", Fields = { ["Set"] = "mood" } },
            Keys = { new() { Name = "spare", Type = BlackboardValueType.Float } }
        };

        Assert.True(BehaviorTreeContentCompiler.TryCompile(content, resolver, out var problems, out var template));
        Assert.Empty(problems);

        // ⚠ And its state size is the *set's*, not zero: the layout is the header, a cooldown stamp
        // per action, and the widest sub-action's block. A zero here would be a span into the next
        // agent's memory.
        Assert.True(template![0].MemorySize >= UtilitySet.HeaderSize, $"it reserved {template[0].MemorySize} bytes.");
    }

    [Fact]
    public void ATreeThatNamesASetNobodyWroteStillCompiles() {
        var content = new BehaviorTreeContent {
            Name = "brain",
            Root = new() { Name = "run", Type = "RunUtilitySet", Fields = { ["Set"] = "missing" } },
            Keys = { new() { Name = "spare", Type = BlackboardValueType.Float } }
        };

        Assert.False(BehaviorTreeContentCompiler.TryCompile(content, Resolver(), out var problems, out var template));
        Assert.NotNull(template);
        Assert.Contains(problems, problem => problem.Message.Contains("No utility set called", StringComparison.Ordinal));
    }

    static BehaviorAttachmentContent Blackboard(string key, BlackboardTest test) =>
        new() { Type = "Blackboard", Fields = { ["Key"] = key, ["Test"] = test.ToString() } };

    /// <summary>A one-task tree with one decorator on it, and three keys the operands can read.</summary>
    static BehaviorTreeContent Tree(BehaviorAttachmentContent decorator) => new() {
        Name = "brain",
        Root = new() {
            Name = "act",
            Type = "Wait",
            Fields = { ["Seconds"] = "1" },
            Decorators = { decorator }
        },
        Keys = {
            new() { Name = "visible", Type = BlackboardValueType.Bool },
            new() { Name = "armed", Type = BlackboardValueType.Bool },
            new() { Name = "hungry", Type = BlackboardValueType.Bool }
        }
    };

    static BehaviorTreeResolver Resolver() => new() { Schema = new BehaviorNodeSchema() };

    static bool Refused(BehaviorTreeContent content, out IReadOnlyList<BehaviorTreeDiagnostic> problems) =>
        BehaviorTreeContentCompiler.TryCompile(content, Resolver(), out problems, out _);
}
