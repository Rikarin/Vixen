// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Editor.Ai;
using Xunit;

namespace Vixen.Editor.Ai.Tests;

public class BehaviorTreeModelTests {
    [Fact]
    public void TheWalkIsPreOrderAndIsTheOrderTheBadgesShow() {
        var model = Tree();

        Assert.Equal(
            ["Root", "Left", "A", "B", "Right", "C"],
            model.Walk().Select(node => node.Name).ToArray()
        );

        Assert.Equal(0, model.IndexOf(model.Content.Root!));
        Assert.Equal(4, model.IndexOf(model.At(4)!));
    }

    [Fact]
    public void AChildIsInsertedWhereItWasAskedFor() {
        var model = Tree();
        var root = model.Content.Root!;

        model.Insert(root, Node("Middle"), 1);

        Assert.Equal(["Left", "Middle", "Right"], root.Children.Select(child => child.Name).ToArray());
    }

    [Fact]
    public void ReorderMovesAChildAmongItsSiblings() {
        var model = Tree();
        var root = model.Content.Root!;

        Assert.True(model.Reorder(root.Children[1], -1));
        Assert.Equal(["Right", "Left"], root.Children.Select(child => child.Name).ToArray());

        Assert.False(model.Reorder(root.Children[0], -1));
        Assert.False(model.Reorder(root.Children[1], 1));
    }

    /// <summary>
    ///     ⚠ A node dragged two places to the right lands two places to the right. The obvious
    ///     implementation lands it one short, every time, because the removal shifts the list under
    ///     the index the drop was measured against.
    /// </summary>
    [Fact]
    public void ReparentingWithinOneParentAccountsForTheRemoval() {
        var model = new BehaviorTreeModel(new BehaviorTreeContent { Root = Node("Root") });
        var root = model.Content.Root!;

        foreach (var name in new[] { "a", "b", "c", "d" }) {
            model.Insert(root, Node(name));
        }

        Assert.True(model.Reparent(root.Children[0], root, 3));
        Assert.Equal(["b", "c", "a", "d"], root.Children.Select(child => child.Name).ToArray());
    }

    [Fact]
    public void ANodeCannotBeMovedInsideItsOwnSubtree() {
        var model = Tree();
        var left = model.Content.Root!.Children[0];

        Assert.False(model.Reparent(left, left.Children[0]));
        Assert.False(model.Reparent(model.Content.Root!, left));
        Assert.Equal(6, model.Count);
    }

    [Fact]
    public void RemovingANodeTakesItsSubtreeWithIt() {
        var model = Tree();

        Assert.True(model.Remove(model.Content.Root!.Children[0]));
        Assert.Equal(["Root", "Right", "C"], model.Walk().Select(node => node.Name).ToArray());
    }

    [Fact]
    public void AnAttachmentGoesOnAndComesOffInOrder() {
        var model = Tree();
        var node = model.At(2)!;
        var first = Attach("Inverter");
        var second = Attach("ForceSuccess");

        model.Attach(node, BehaviorAttachmentSlot.Decorator, first);
        model.Attach(node, BehaviorAttachmentSlot.Decorator, second, 0);

        Assert.Equal(["ForceSuccess", "Inverter"], node.Decorators.Select(row => row.Type).ToArray());

        Assert.True(model.MoveAttachment(node, BehaviorAttachmentSlot.Decorator, second, 1));
        Assert.Equal(["Inverter", "ForceSuccess"], node.Decorators.Select(row => row.Type).ToArray());

        Assert.True(model.Detach(node, BehaviorAttachmentSlot.Decorator, second));
        Assert.Single(node.Decorators);
    }

    /// <summary>
    ///     ⚠ The rewrite is the whole point: a file references a key by name, so a rename that only
    ///     changed the declaration would leave every decorator pointing at a key that is gone.
    /// </summary>
    [Fact]
    public void RenamingAKeyRewritesEveryReferenceToIt() {
        var model = Tree();
        var key = model.AddKey("target", BlackboardValueType.Entity)!;
        var node = model.At(2)!;
        var decorator = Attach("Blackboard");

        decorator.Fields["Key"] = "target";
        model.Attach(node, BehaviorAttachmentSlot.Decorator, decorator);

        var service = Attach("UpdateBlackboard");

        service.Fields["Key"] = "target";
        model.Attach(model.Content.Root!, BehaviorAttachmentSlot.Service, service);

        Assert.Equal(2, model.RenameKey(key, "quarry"));
        Assert.Equal("quarry", decorator.Fields["Key"]);
        Assert.Equal("quarry", service.Fields["Key"]);
    }

    [Fact]
    public void ADuplicateKeyNameIsRefusedOnAddAndOnRename() {
        var model = Tree();

        Assert.NotNull(model.AddKey("target", BlackboardValueType.Entity));
        Assert.Null(model.AddKey("target", BlackboardValueType.Float));
        Assert.Null(model.AddKey("  ", BlackboardValueType.Float));

        var other = model.AddKey("alert", BlackboardValueType.Bool)!;

        Assert.Equal(-1, model.RenameKey(other, "target"));
        Assert.Equal("alert", other.Name);
    }

    /// <summary>
    ///     ⚠ The references are left dangling and counted. Clearing them would throw away which key
    ///     forty decorators used to read, which is what somebody undoing a mistaken delete wants.
    /// </summary>
    [Fact]
    public void DeletingAKeyCountsWhatItLeftDangling() {
        var model = Tree();
        var key = model.AddKey("target", BlackboardValueType.Entity)!;
        var decorator = Attach("Blackboard");

        decorator.Fields["Key"] = "target";
        model.Attach(model.At(2)!, BehaviorAttachmentSlot.Decorator, decorator);

        Assert.Equal(1, model.RemoveKey(key));
        Assert.Equal("target", decorator.Fields["Key"]);
        Assert.Empty(model.Content.Keys);
    }

    /// <summary>
    ///     The abort-scope region against a hand-computed one: an observer reaches the siblings under
    ///     its own parent composite, and no further.
    /// </summary>
    [Fact]
    public void TheAbortScopeIsTheParentCompositesSubtree() {
        var model = Tree();
        var gated = model.At(2)!;
        var decorator = Attach("Blackboard");

        model.AddKey("alert", BlackboardValueType.Bool);
        decorator.Fields["Key"] = "alert";
        decorator.Fields["Aborts"] = nameof(ObserverAborts.Both);
        model.Attach(gated, BehaviorAttachmentSlot.Decorator, decorator);

        // `A` sits under `Left`, so the region is `Left` and everything below it — and emphatically
        // not `Right`, which is what Unreal's wider rule would have reached.
        Assert.Equal(
            ["Left", "A", "B"],
            model.AbortScope(gated, decorator).Select(node => node.Name).ToArray()
        );
    }

    [Fact]
    public void ADecoratorThatObservesNothingShadesNothing() {
        var model = Tree();
        var gated = model.At(2)!;
        var decorator = Attach("Blackboard");

        model.Attach(gated, BehaviorAttachmentSlot.Decorator, decorator);

        Assert.Empty(model.AbortScope(gated, decorator));
    }

    [Fact]
    public void MakeFillsInTheDeclaredDefaults() {
        var schema = BehaviorNodeSchema.Default;

        Assert.True(schema.TryGet("Cooldown", out var cooldown));

        var attachment = BehaviorTreeModel.MakeAttachment(cooldown!);

        Assert.Equal("1", attachment.Fields["Seconds"]);

        Assert.True(schema.TryGet("Wait", out var wait));
        Assert.Equal("Wait", BehaviorTreeModel.Make(wait!).Name);
    }

    [Fact]
    public void ASnapshotIsADeepCopy() {
        var model = Tree();
        var before = model.Snapshot();

        model.Rename(model.At(2)!, "renamed");
        model.Insert(model.Content.Root!, Node("extra"));

        Assert.Equal("A", before.Root!.Children[0].Children[0].Name);
        Assert.Equal(2, before.Root.Children.Count);

        model.Replace(before);
        Assert.Equal(6, model.Count);
    }

    [Fact]
    public void TheLayoutPutsParentsOverTheirChildren() {
        var model = Tree();

        BehaviorTreeLayout.Apply(model);

        var root = model.Content.Root!;
        var left = root.Children[0];

        Assert.Equal(0f, root.Y);
        Assert.Equal(130f, left.Y);
        Assert.Equal(260f, left.Children[0].Y);

        // A parent sits at the midpoint of its outermost children, which is where the branch it owns
        // actually is.
        Assert.Equal((left.Children[0].X + left.Children[1].X) * 0.5f, left.X, 3);
        Assert.True(root.X > left.X, "the root did not centre over both of its branches.");
    }

    static BehaviorTreeModel Tree() {
        var root = Node("Root");
        var left = Node("Left");
        var right = Node("Right");

        left.Children.Add(Node("A", "Wait"));
        left.Children.Add(Node("B", "Wait"));
        right.Children.Add(Node("C", "Wait"));
        root.Children.Add(left);
        root.Children.Add(right);

        return new(new BehaviorTreeContent { Name = "test", Root = root });
    }

    static BehaviorNodeContent Node(string name, string type = "Selector") => new() { Name = name, Type = type };

    static BehaviorAttachmentContent Attach(string type) => new() { Type = type };
}
