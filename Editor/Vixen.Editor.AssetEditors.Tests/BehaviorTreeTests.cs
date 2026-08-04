// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Editor.Ai;
using Vixen.Editor.AssetEditors.Ai;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>
///     P2's exit criterion, and the half of it that is this assembly's: a tree of thirty nodes
///     authored end to end with no text editing, saved, reopened identically.
/// </summary>
/// <remarks>
///     ⚠ <b>"Authored end to end with no text editing" is what the gestures below are.</b> Every node,
///     every decorator, every service, every key and every reorder goes through
///     <see cref="BehaviorTreeDocument.Edit" /> — which is what the canvas, the search popup and the
///     inspector all call — so a gesture the editor cannot express is a gesture this test cannot
///     write. What it does <i>not</i> prove is that a person can reach each one with a mouse; that is
///     what the view's own tests are for.
/// </remarks>
public class BehaviorTreeDocumentTests {
    [Fact]
    public void ANewTreeOpensCompilingRatherThanComplainingAboutItself() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Guard.vxbt", string.Empty);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(document.LoadError);
        Assert.NotNull(document.Compile());
        Assert.Empty(document.Diagnostics);
        Assert.Equal("Guard", document.Model.Content.Name);
    }

    /// <summary>
    ///     The exit criterion. Thirty nodes, every one of them put there by a gesture, and a
    ///     save/load/save round trip that is a no-op in the diff — <c>NodeGraphAsset</c>'s rule.
    /// </summary>
    [Fact]
    public void AThirtyNodeTreeIsAuthoredSavedAndReopenedIdentically() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Patrol.vxbt", string.Empty);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        Assert.Equal(30, document.Model.Count);

        var written = document.ToYaml();

        document.Save();

        var reopened = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(reopened.LoadError);
        Assert.Equal(30, reopened.Model.Count);

        // ⚠ Save → load → save is a no-op in the diff. A round trip that lost a field would look like
        // a working editor right up until somebody read the file, and a round trip that *reordered*
        // one would change what the agent does.
        Assert.Equal(written, reopened.ToYaml());

        // And the reopened tree is the same tree, not merely the same number of bytes.
        Assert.Equal(
            document.Model.Walk().Select(node => $"{node.Name}:{node.Type}").ToArray(),
            reopened.Model.Walk().Select(node => $"{node.Name}:{node.Type}").ToArray()
        );

        Assert.Equal(
            document.Model.Content.Keys.Select(key => $"{key.Name}:{key.Type}").ToArray(),
            reopened.Model.Content.Keys.Select(key => $"{key.Name}:{key.Type}").ToArray()
        );
    }

    [Fact]
    public void TheAuthoredTreeCompilesWithNothingToSayAboutIt() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Patrol.vxbt", string.Empty);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var template = document.Compile();

        Assert.NotNull(template);
        // Everything except the sensor, which is a name a game registers in code and the editor
        // cannot resolve — reported as a remark rather than an error, for AnimationGraphCompiler's
        // reason.
        Assert.DoesNotContain(
            document.Diagnostics,
            problem => !problem.Message.Contains("sensor", StringComparison.Ordinal)
        );

        // The compiled tree is the authored one: pre-order, and every attachment carried across.
        Assert.Equal(document.Model.Count, template!.Count);
        Assert.Equal(3, template.Decorators.Length);

        // ⚠ And the service is *not* there, because its sensor is a name a game registers in code.
        // Dropping it and saying so is the documented behaviour: the alternative is a compiler that
        // refuses, which would make every tree in a project unopenable until its code existed.
        Assert.Empty(template.Services.ToArray());
        Assert.Contains(document.Diagnostics, problem => problem.Message.Contains("sensor", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryGestureIsOneUndoStep() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Undo.vxbt", string.Empty);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);
        var before = document.Model.Count;

        document.Edit("Add Node", model => model.Insert(model.Content.Root, Node("Sequence", "extra")));
        Assert.Equal(before + 1, document.Model.Count);

        Assert.True(document.Stack.Undo());
        Assert.Equal(before, document.Model.Count);

        Assert.True(document.Stack.Redo());
        Assert.Equal(before + 1, document.Model.Count);
    }

    /// <summary>
    ///     ⚠ A key rename that rewrote forty references undoes as one step. That is the whole reason
    ///     the undo entry is a snapshot: an inverse written by hand would have to put back every
    ///     reference it touched, and the one it forgot would be silent.
    /// </summary>
    [Fact]
    public void AKeyRenameAndItsRewritesUndoTogether() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Keys.vxbt", string.Empty);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        document.Edit("Add Key", model => model.AddKey("target", BlackboardValueType.Entity));

        var gated = document.Model.Content.Root!.Children[0];

        document.Edit(
            "Add Decorator",
            model => {
                var decorator = new BehaviorAttachmentContent { Type = "Blackboard" };

                decorator.Fields["Key"] = "target";
                model.Attach(gated, BehaviorAttachmentSlot.Decorator, decorator);
            }
        );

        document.Edit("Rename Key", model => model.RenameKey(model.Content.Keys[0], "quarry"));

        Assert.Equal("quarry", Current(document).Children[0].Decorators[0].Fields["Key"]);

        Assert.True(document.Stack.Undo());
        Assert.Equal("target", Current(document).Children[0].Decorators[0].Fields["Key"]);
        Assert.Equal("target", document.Model.Content.Keys[0].Name);
    }

    [Fact]
    public void MergedEntriesUndoToWhereTheGestureStarted() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Drag.vxbt", string.Empty);
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);
        for (var step = 1; step <= 10; step++) {
            var to = step * 10f;

            document.Edit("Move Nodes", model => model.Move(model.Content.Root!, to, to), mergeKey: "move");
        }

        Assert.Equal(100f, Current(document).X);

        // One step back to where the drag started, not one step back to ninety.
        Assert.True(document.Stack.Undo());
        Assert.Equal(0f, Current(document).X);
    }

    [Fact]
    public void ABrokenFileOpensEmptyAndSaysWhy() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Bad.vxbt", "root: [ this is not a tree");
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        Assert.NotNull(document.LoadError);
        Assert.Equal("Bad", document.Model.Content.Name);
    }

    [Fact]
    public void ATreeFromTheFutureIsRefusedRatherThanMisread() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Ahead.vxbt", "version: 99\nname: Ahead\n");
        var document = new BehaviorTreeDocument(fixture.Project, AssetId.Empty, path);

        Assert.NotNull(document.LoadError);
        Assert.Contains("99", document.LoadError!, StringComparison.Ordinal);
    }

    /// <summary>Thirty nodes, put there the way the editor puts them there.</summary>
    static void Author(BehaviorTreeDocument document) {
        document.Edit("Add Keys", model => {
            model.AddKey("target", BlackboardValueType.Entity);
            model.AddKey("alert", BlackboardValueType.Bool);
            model.AddKey("home", BlackboardValueType.Vector3);
            model.AddKey("here", BlackboardValueType.Vector3);
            model.AddKey("wait", BlackboardValueType.Float);
        });

        var root = document.Model.Content.Root!;

        // The wait the empty document came with is the first leaf; the rest is authored.
        document.Edit("Name Root", model => model.Rename(root, "Brain"));

        var branches = new[] { "Respond", "Investigate", "Patrol" };

        foreach (var name in branches) {
            document.Edit("Add Branch", model => model.Insert(root, Node("Sequence", name), 0));
        }

        foreach (var branch in root.Children.Take(3)) {
            document.Edit(
                "Add Steps",
                model => {
                    model.Insert(branch, Node("Wait", "Pause"));
                    model.Insert(branch, Node("Log", "Say"));
                    var remember = Node("SetBlackboardValue", "Remember");

                    remember.Fields["Key"] = "alert";
                    model.Insert(branch, remember);
                    model.Insert(branch, Node("Selector", "Choose"));
                }
            );

            var choose = branch.Children[^1];

            document.Edit(
                "Add Choices",
                model => {
                    model.Insert(choose, Node("FinishWith", "Give up"));
                    model.Insert(choose, Node("Wait", "Hold"));
                }
            );

            document.Edit(
                "Gate Branch",
                model => {
                    var decorator = new BehaviorAttachmentContent { Type = "Blackboard" };

                    decorator.Fields["Key"] = "alert";
                    decorator.Fields["Test"] = nameof(BlackboardTest.IsSet);
                    decorator.Fields["Aborts"] = nameof(ObserverAborts.Both);
                    model.Attach(branch, BehaviorAttachmentSlot.Decorator, decorator);
                }
            );
        }

        document.Edit(
            "Watch",
            model => {
                var service = new BehaviorAttachmentContent { Type = "UpdateBlackboard", Interval = 0.4f, RandomDeviation = 0.1f };

                service.Fields["Sensor"] = "nearest";
                service.Fields["Key"] = "target";
                model.Attach(root, BehaviorAttachmentSlot.Service, service);
            }
        );

        // Two nodes short of thirty, so the last gesture is the one that gets there.
        while (document.Model.Count < 30) {
            document.Edit("Add Leaf", model => model.Insert(root, Node("Wait", $"Idle {model.Count}")));
        }

        document.Edit("Reorder", model => model.Reorder(root.Children[^1], -1));
        document.Layout();
    }

    static BehaviorNodeContent Node(string type, string name) {
        BehaviorNodeSchema.Default.TryGet(type, out var declared);

        return BehaviorTreeModel.Make(declared!, name);
    }

    static BehaviorNodeContent Current(BehaviorTreeDocument document) => document.Model.Content.Root!;
}
