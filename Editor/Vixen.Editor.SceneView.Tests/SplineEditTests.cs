// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Editing a spline in the viewport — [docs/plan/31 § T8].</summary>
public sealed class SplineEditTests {
    static SplineAsset Road =>
        SplineAsset.Through("Road", [new(0f, 0f, 0f), new(20f, 0f, 0f), new(40f, 0f, 0f)]);

    static SplineEdit Editing(SplineAsset? asset = null) => new() { Asset = asset ?? Road, PickRadius = 2f };

    [Fact]
    public void PickingSelectsTheNearestHandle() {
        var edit = Editing();

        var picked = edit.Pick(new(20.5f, 0f, 0f));

        Assert.Equal(new SplineHandle(1, SplineElement.Point), picked);
        Assert.Single(edit.Selection);
    }

    [Fact]
    public void PickingNothingClearsTheSelection() {
        var edit = Editing();

        edit.Pick(new(20f, 0f, 0f));

        Assert.True(edit.HasSelection);
        Assert.Null(edit.Pick(new(200f, 0f, 0f)));
        Assert.False(edit.HasSelection);
    }

    /// <summary>A tangent handle is reachable even when it sits on its own point.</summary>
    /// <remarks>
    ///     ⚠ <b>A tangent of zero length is exactly where the corner is.</b> A pick that preferred
    ///     the point would make the handle unreachable precisely once it mattered — the author has
    ///     collapsed the tangent to make a corner and now cannot pull it back out.
    /// </remarks>
    [Fact]
    public void ACollapsedTangentHandleIsStillReachable() {
        var asset = Road;

        asset.SetTangentOut(1, Vector3.Zero, mirror: true);

        var edit = Editing(asset);
        var picked = edit.Pick(asset[1].Position);

        Assert.NotNull(picked);
        Assert.NotEqual(SplineElement.Point, picked.Value.Element);
    }

    [Fact]
    public void MovingAPointCarriesItsTangentsAndMovingATangentDoesNotMoveThePoint() {
        var asset = Road;
        var edit = Editing(asset);

        edit.Pick(asset[1].Position);
        edit.Move(new(0f, 0f, 5f));

        Assert.Equal(new Vector3(20f, 0f, 5f), asset[1].Position);

        var carried = asset[1].TangentOut;

        edit.Pick(asset[1].Position + asset[1].TangentOut);
        edit.Move(new(0f, 3f, 0f));

        Assert.Equal(new Vector3(20f, 0f, 5f), asset[1].Position);
        Assert.NotEqual(carried, asset[1].TangentOut);
    }

    [Fact]
    public void ADragIsOneUndoEntry() {
        var asset = Road;
        var edit = Editing(asset);
        // The command touches the asset and nothing else — no document, no project — so the context
        // it is handed is genuinely unused. Constructing a project to prove that would test the
        // project.
        var context = (EditorContext)null!;

        edit.Pick(asset[1].Position);
        edit.Begin();

        for (var step = 0; step < 10; step++) {
            edit.Move(new(0f, 0f, 1f));
        }

        var command = edit.Commit();

        Assert.NotNull(command);
        Assert.Equal(new Vector3(20f, 0f, 10f), asset[1].Position);

        command.Undo(context);
        Assert.Equal(new Vector3(20f, 0f, 0f), asset[1].Position);

        command.Do(context);
        Assert.Equal(new Vector3(20f, 0f, 10f), asset[1].Position);
    }

    [Fact]
    public void ADragThatChangedNothingIsNotAnEntry() {
        var edit = Editing();

        edit.Begin();

        Assert.Null(edit.Commit());
    }

    [Fact]
    public void CancellingPutsThePointsBack() {
        var asset = Road;
        var edit = Editing(asset);

        edit.Pick(asset[1].Position);
        edit.Begin();
        edit.Move(new(0f, 0f, 40f));
        edit.Cancel();

        Assert.Equal(new Vector3(20f, 0f, 0f), asset[1].Position);
    }

    /// <summary>Two nudges of the same asset become one entry; an insertion never merges.</summary>
    /// <remarks>
    ///     ⚠ <b>Inserting a point and then moving it are two things an author did</b>, and undoing
    ///     them together loses the insertion — which reads as undo skipping a step.
    /// </remarks>
    [Fact]
    public void MovesMergeAndStructuralEditsDoNot() {
        var asset = Road;
        var edit = Editing(asset);

        edit.Pick(asset[1].Position);

        edit.Begin();
        edit.Move(new(0f, 0f, 1f));

        var first = edit.Commit();

        edit.Begin();
        edit.Move(new(0f, 0f, 1f));

        var second = edit.Commit();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.True(second.TryMergeWith(first, out _));

        edit.Begin();
        edit.InsertOn(new(30f, 0f, 0f));

        var structural = edit.Commit();

        Assert.NotNull(structural);
        Assert.False(structural.TryMergeWith(second, out _));
    }

    /// <summary>Inserting on the curve puts the point where the curve is.</summary>
    [Fact]
    public void InsertingLandsOnTheCurve() {
        var asset = Road;
        var edit = Editing(asset);

        var index = edit.InsertOn(new(30f, 0f, 4f));

        Assert.True(index > 0);
        Assert.Equal(4, asset.Count);
        Assert.Equal(30f, asset[index].Position.X, 0);
        Assert.Equal(0f, asset[index].Position.Z, 1);
    }

    /// <summary>Deleting several points does not delete the wrong ones.</summary>
    /// <remarks>
    ///     ⚠ <b>Descending, so the indices below a removal do not shift under it.</b> The selection is
    ///     a set of indices, so a caller cannot even hand them over in a safe order by accident — the
    ///     same trap <c>FoliageVolume.Remove</c> guards against, one subsystem over.
    /// </remarks>
    [Fact]
    public void DeletingSeveralPointsRemovesExactlyThose() {
        // ⚠ Irregularly spaced on purpose. On an evenly spaced path a Catmull-Rom tangent handle
        // lands exactly on the *next* control point — the tip of point 1's outgoing tangent is point
        // 2 — so a pick there selects the tangent, which is the documented tie rule doing its job and
        // not what this test is about.
        var asset = SplineAsset.Through(
            "Long",
            [new(0f, 0f, 0f), new(10f, 0f, 0f), new(25f, 0f, 0f), new(45f, 0f, 0f), new(70f, 0f, 0f)]
        );

        var edit = Editing(asset);

        Assert.Equal(new SplineHandle(1, SplineElement.Point), edit.Pick(new(10f, 0f, 0f)));
        Assert.Equal(new SplineHandle(3, SplineElement.Point), edit.Pick(new(45f, 0f, 0f), add: true));

        Assert.Equal(2, edit.Delete());
        Assert.Equal(3, asset.Count);
        Assert.Equal([0f, 25f, 70f], asset.Points.Select(point => point.Position.X));
    }

    [Fact]
    public void AppendingSmoothsThePathAsItIsDrawn() {
        var asset = new SplineAsset { Name = "Drawn" };
        var edit = Editing(asset);

        edit.Append(new(0f, 0f, 0f));
        edit.Append(new(10f, 0f, 10f));
        edit.Append(new(20f, 0f, 0f));

        Assert.Equal(3, asset.Count);
        Assert.NotEqual(Vector3.Zero, asset[1].TangentOut);
        Assert.True(edit.HasSelection);
    }

    [Fact]
    public void SnappingRoundsAnAppendedPoint() {
        var asset = new SplineAsset { Name = "Snapped" };
        var edit = Editing(asset);

        edit.Snapping = new() { GridStep = 5f, SnapPosition = true };
        edit.Append(new(11.4f, 0f, 2.4f));

        Assert.Equal(new Vector3(10f, 0f, 0f), asset[0].Position);
    }

    [Fact]
    public void AnEditWithNoAssetDoesNothingRatherThanThrowing() {
        var edit = new SplineEdit();

        Assert.Null(edit.Pick(Vector3.Zero));
        Assert.Equal(-1, edit.Append(Vector3.Zero));
        Assert.Equal(-1, edit.InsertOn(Vector3.Zero));
        Assert.Equal(0, edit.Delete());
        Assert.Null(edit.Commit());
    }
}
