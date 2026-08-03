// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Rendering;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The variation matrix as a panel: the grid, the three states, and the drill-down.</summary>
public class HarnessViewTests {
    /// <summary>Rows are configurations and columns are goals, with a header over them.</summary>
    [Fact]
    public void TheGridIsConfigurationsByGoals() {
        using var harness = new ViewHarness();
        var (plan, report) = Run();

        var view = harness.Ui.Document.Root.Add<VariationHarnessView>();
        view.Show(plan, report);

        harness.Ui.Frame();

        // Three bodies plus the header row.
        Assert.Equal(report.Cases.Count + 1, view.Matrix.Children.Count);

        // A label column plus one cell per goal.
        Assert.Equal(report.Goals.Count + 1, view.Matrix.Children[1].Children.Count);
    }

    /// <summary>
    ///     ⚠ <b>Three states, not two.</b> "Never resolved" is a different kind of fact from "off by
    ///     four centimetres", and an author who reads it as a number spends the afternoon tuning a
    ///     goal that is not running.
    /// </summary>
    [Fact]
    public void AGoalThatNeverResolvedIsMarkedDifferentlyFromOneThatFailed() {
        using var harness = new ViewHarness();
        var (plan, report) = Run(shape: "no-such-shape");

        var view = harness.Ui.Document.Root.Add<VariationHarnessView>();
        view.Show(plan, report);

        harness.Ui.Frame();

        var cell = view.Matrix.Children[1].Children[1];

        Assert.True(cell.HasClass("missing"), "a goal that never resolved is marked as missing");
        Assert.False(cell.HasClass("failed"), "and not as a failure, which is a different fix");
        Assert.Equal("—", cell.Text);
    }

    /// <summary>
    ///     The drill-down: choosing a cell hands back the configuration rebuilt, not an index somebody
    ///     else has to turn back into a body.
    /// </summary>
    [Fact]
    public void SelectingACellHandsBackTheConfigurationRebuilt() {
        using var harness = new ViewHarness();
        var (plan, report) = Run();

        var view = harness.Ui.Document.Root.Add<VariationHarnessView>();
        HarnessSelection? chosen = null;

        view.CellSelected += (_, selection) => chosen = selection;
        view.Show(plan, report);

        harness.Ui.Frame();

        Click(harness, view.Matrix.Children[1].Children[1]);

        Assert.NotNull(chosen);

        var selection = chosen;

        Assert.Equal(0, selection.Case.Index);
        Assert.Contains("×0.7", selection.Case.Label, StringComparison.Ordinal);
        Assert.NotNull(selection.Subject.Shapes);
        Assert.Equal(selection.Cell, view.Selected);
    }

    /// <summary>The button that jumps to the cell worth looking at, without hunting across the grid.</summary>
    [Fact]
    public void TheWorstCellButtonSelectsTheWorstCell() {
        using var harness = new ViewHarness();
        var (plan, report) = Run(target: new Vector3(0.2f, 1.3f, 0.25f));

        var view = harness.Ui.Document.Root.Add<VariationHarnessView>();
        view.Show(plan, report);

        harness.Ui.Frame();
        Click(harness, view.Worst);

        var selected = Assert.NotNull(view.Selected);

        Assert.Equal(report.Worst(plan.Thresholds), selected);
        Assert.Contains("Failed", view.Verdict.Text, StringComparison.Ordinal);
    }

    static void Click(ViewHarness harness, UiElement element) {
        var x = element.AbsoluteLeft + (element.Width / 2f);
        var y = element.AbsoluteTop + (element.Height / 2f);

        harness.Ui.Document.Dispatch(
            new PointerEvent { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary }
        );

        harness.Ui.Document.Dispatch(
            new PointerEvent { X = x, Y = y, Action = PointerAction.Released, Button = PointerButton.Primary }
        );

        harness.Ui.Frame();
    }

    static (HarnessPlan Plan, VariationReport Report) Run(string shape = "belly", Vector3? target = null) {
        var skeleton = Rig(1f);
        var shapes = Shapes(skeleton, 1f);

        var tag = target is { } at
            ? new ConstraintTagRecord {
                Name = "right hand",
                Effector = "Wrist",
                Chain = "Shoulder",
                Goal = new() { Kind = ConstraintFrameKind.World, Position = at }
            }
            : new ConstraintTagRecord {
                Name = "right hand",
                Effector = "Wrist",
                Chain = "Shoulder",
                Goal = new() { Kind = ConstraintFrameKind.Surface, Shape = shape, Face = -1, U = 0.5f, V = 0.5f }
            };

        var plan = new HarnessPlan {
            Clip = new() { Name = "Reach", Data = new() { Name = "Reach", Duration = 1f }, Constraints = [tag] },
            Skeleton = skeleton,
            Shapes = shapes,
            Samples = 6,
            Thresholds = new() { Residual = 0.03f },
            Variations = [new BodyVariation(skeleton, 0.7f, 1f, 1.4f)]
        };

        return (plan, VariationHarness.Run(plan));
    }

    static Skeleton Rig(float scale) =>
        Skeleton.Create(Build("Body", scale));

    static ProxyShapeSet Shapes(Skeleton skeleton, float scale) =>
        ProxyShapeSet.Of(
            "Body",
            null,
            new ProxyShape {
                Name = Symbol.Intern("belly"),
                Kind = ShapeKind.Sphere,
                Joint = skeleton.IndexOf("Spine"),
                Offset = new(new Vector3(0f, 0.1f, 0.05f) * scale, Quaternion.Identity, Vector3.One),
                Dimensions = ShapeParams.Sphere(0.22f).Scaled(new Vector3(scale))
            }
        );

    /// <summary>An arm on a spine, built the way an importer would.</summary>
    static SkeletonData Build(string name, float scale) {
        (string Name, int Parent, Vector3 Offset)[] joints = [
            ("Root", -1, Vector3.Zero),
            ("Spine", 0, new Vector3(0f, 0.9f, 0f) * scale),
            ("Shoulder", 1, new Vector3(0.2f, 0.35f, 0f) * scale),
            ("Elbow", 2, new Vector3(0f, -0.32f, 0f) * scale),
            ("Wrist", 3, new Vector3(0f, -0.3f, 0f) * scale)
        ];

        var model = new Matrix4x4[joints.Length];
        var built = new SkeletonJoint[joints.Length];

        for (var index = 0; index < joints.Length; index++) {
            var local = Matrix4x4.FromTranslation(joints[index].Offset);

            model[index] = joints[index].Parent >= 0 ? local * model[joints[index].Parent] : local;

            Matrix4x4.Invert(model[index], out var inverse);

            built[index] = new() {
                Name = joints[index].Name,
                Parent = joints[index].Parent,
                InverseBindPose = inverse
            };
        }

        return new() { Name = name, Joints = built };
    }
}
