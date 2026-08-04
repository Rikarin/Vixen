// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ai;
using Vixen.Core;
using Vixen.Editor.Ai;
using Vixen.Editor.AssetEditors.Ai;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The authoring half of doc 37 § P5: a table of actions, a table of considerations, a curve.</summary>
public class UtilitySetDocumentTests {
    [Fact]
    public void ANewSetOpensCompilingRatherThanComplainingAboutItself() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Villager.vxutility", string.Empty);
        var document = new UtilitySetDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(document.LoadError);
        Assert.Equal("Villager", document.Model.Content.Name);
        Assert.Single(document.Model.Content.Actions);
    }

    /// <summary>Authored by gestures, saved, reopened identically — the rule every asset here follows.</summary>
    [Fact]
    public void ASetIsAuthoredSavedAndReopenedIdentically() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Guard.vxutility", string.Empty);
        var document = new UtilitySetDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        Assert.Equal(4, document.Model.Count);

        var written = document.ToYaml();

        document.Save();

        var reopened = new UtilitySetDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(reopened.LoadError);
        Assert.Equal(4, reopened.Model.Count);
        Assert.Equal(written, reopened.ToYaml());

        // And the same thing said as a walk rather than as text, so a reader that lost a field to a
        // get-only collection could not pass by writing the same YAML twice.
        foreach (var (before, after) in document.Model.Content.Actions.Zip(reopened.Model.Content.Actions)) {
            Assert.Equal(before.Name, after.Name);
            Assert.Equal(before.Task, after.Task);
            Assert.Equal(before.Weight, after.Weight);
            Assert.Equal(before.Bucket, after.Bucket);
            Assert.Equal(before.Considerations.Count, after.Considerations.Count);

            foreach (var (one, two) in before.Considerations.Zip(after.Considerations)) {
                Assert.Equal(one.Name, two.Name);
                Assert.Equal(one.Key, two.Key);
                Assert.Equal(one.Curve, two.Curve);
                Assert.Equal(one.Centre, two.Centre, 4);
                Assert.Equal(one.Keys.Count, two.Keys.Count);
            }
        }
    }

    [Fact]
    public void EveryGestureIsUndoableAndTheDraggedOnesMerge() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Undo.vxutility", string.Empty);
        var document = new UtilitySetDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var authored = document.ToYaml();
        var settled = document.Stack.History.Count;

        for (var step = 0; step < 40; step++) {
            var value = step / 40f;

            document.Edit(
                "Tune",
                model => model.SetShape(model.Content.Actions[0].Considerations[0], centre: value),
                "curve-centre"
            );
        }

        // Forty gestures, one entry: what stops a dragged number being forty steps.
        Assert.Equal(settled + 1, document.Stack.History.Count);
        Assert.True(document.Stack.Undo());

        // ⚠ One step back to where the drag started, not one step back to halfway.
        Assert.Equal(authored, document.ToYaml());
    }

    /// <summary>
    ///     ⚠ A key rename rewrites every consideration that reads it. A rename that only changed the
    ///     declaration would leave them reading a key that is gone — which under the zero rule is an
    ///     action that silently never runs.
    /// </summary>
    [Fact]
    public void RenamingAKeyRewritesEveryConsiderationThatReadsIt() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Keys.vxutility", string.Empty);
        var document = new UtilitySetDocument(fixture.Project, AssetId.Empty, path);

        Author(document);

        var key = document.Model.Content.Keys.First(row => row.Name == "hunger");
        var rewritten = 0;

        document.Edit("Rename", model => rewritten = model.RenameKey(key, "appetite"));

        Assert.Equal(2, rewritten);
        Assert.DoesNotContain(
            document.Model.Content.Actions.SelectMany(action => action.Considerations),
            consideration => consideration.Key == "hunger"
        );
    }

    /// <summary>
    ///     ⚠ The failure a designer cannot see by looking at the set: a typo in a key name is an action
    ///     that never runs, because the consideration scores zero and a zero is a veto.
    /// </summary>
    [Fact]
    public void AConsiderationReadingAKeyThatIsNotThereIsADiagnostic() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Typo.vxutility", string.Empty);
        var document = new UtilitySetDocument(fixture.Project, AssetId.Empty, path);

        Author(document);
        document.Edit("Typo", model => model.SetInput(model.Content.Actions[0].Considerations[0], UtilityInputKind.Blackboard, "hungr"));
        document.Compile();

        Assert.Contains(
            document.Diagnostics,
            problem => problem.Message.Contains("is not a key on this agent's blackboard", StringComparison.Ordinal)
        );
    }

    /// <summary>A villager: four things it might do, scored on two keys.</summary>
    static void Author(UtilitySetDocument document) {
        // The seeded action a new file opens with, so that the numbers below are the test's own.
        document.Edit("Clear", model => model.RemoveAction(model.Content.Actions[0]));

        document.Edit("Keys", model => {
            model.AddKey("hunger", BlackboardValueType.Float);
            model.AddKey("danger", BlackboardValueType.Float);
        });

        document.Edit("Eat", model => {
            var action = model.AddAction("Eat");
            var axis = model.AddConsideration(action, "hungry")!;

            model.SetInput(axis, UtilityInputKind.Blackboard, "hunger");
            model.SetCurve(axis, ResponseCurveKind.Logistic);
            model.SetShape(axis, exponent: 10f, centre: 0.5f);
        });

        document.Edit("Flee", model => {
            var action = model.AddAction("Flee");

            action.Weight = 5f;
            action.Bucket = 5;

            var axis = model.AddConsideration(action, "afraid")!;

            model.SetInput(axis, UtilityInputKind.Blackboard, "danger");
        });

        document.Edit("Rest", model => {
            var action = model.AddAction("Rest");
            var axis = model.AddConsideration(action, "tired")!;

            model.SetInput(axis, UtilityInputKind.Blackboard, "hunger");
            model.SetCurve(axis, ResponseCurveKind.Sampled);
        });

        document.Edit("Wander", model => model.AddAction("Wander"));
        document.Edit("Order", model => model.Move(model.Content.Actions[3], -1));
        document.Edit("Rules", model => {
            model.Content.Selector = UtilitySelectorKind.Bucketed;
            model.Content.CommitmentBonus = 0.2f;
        });
    }
}

/// <summary>The panel: two tables and a curve, and the numbers on them.</summary>
public class UtilitySetViewTests {
    [Fact]
    public void TheBarsAreTheCompensatedScoreAndAVetoIsCalledOut() {
        // Two axes at 0.6 compensate to 0.6, which is twelve of the bar's twenty cells.
        Assert.Equal(12, UtilitySetView.Cells(0.6f));
        Assert.Equal(0, UtilitySetView.Cells(0f));
        Assert.Equal(20, UtilitySetView.Cells(1f));

        // ⚠ A weight above one is clamped for the bar and not for the score: a bar that ran off the
        // end of its own track would say less than a full one.
        Assert.Equal(20, UtilitySetView.Cells(5f));
    }

    /// <summary>
    ///     ⚠ The preview is what makes "why is this scoring 0.2" answerable while the game is not
    ///     running, which is the whole reason the panel exists.
    /// </summary>
    [Fact]
    public void ThePreviewScoresFromATableOfReadings() {
        var action = new UtilityActionContent { Name = "Eat", Task = "Wait" };

        action.Considerations.Add(new() { Name = "hungry", Key = "hunger" });
        action.Considerations.Add(new() { Name = "safe", Key = "danger", Slope = -1f, Centre = 0f, Shift = 1f });

        var (score, detail) = UtilitySetModel.Preview(action, new Dictionary<string, float> { ["hunger"] = 0.9f, ["danger"] = 0.1f });

        Assert.Equal(0.9f, detail[0], 3);
        Assert.Equal(0.9f, detail[1], 3);
        Assert.Equal(0.9f, score, 3);
    }

    [Fact]
    public void AReadingNobodyGaveIsZeroWhichVetoesTheAction() {
        var action = new UtilityActionContent { Name = "Eat", Task = "Wait" };

        action.Considerations.Add(new() { Name = "hungry", Key = "hunger" });

        var (score, _) = UtilitySetModel.Preview(action, new Dictionary<string, float>());

        Assert.Equal(0f, score);
    }
}
