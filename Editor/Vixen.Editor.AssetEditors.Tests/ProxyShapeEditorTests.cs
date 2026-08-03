// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Rendering;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The proxy shape editor: the list, the mirror, the coarse set and the check.</summary>
public class ProxyShapeEditorTests {
    [Fact]
    public void EveryEditIsOneUndoEntry() {
        using var project = new EditorFixture();
        var document = Open(project);

        var shape = document.Add(new() { Name = "belly", Kind = ShapeKind.Sphere, Joint = "Spine", Extents = new(0.2f) });

        var resized = document.Edit(shape, "Resize Shape", entry => entry with { Extents = new(0.3f) });

        Assert.Equal(0.3f, Assert.Single(document.Set.Shapes).Extents.X, 3);

        document.Stack.Undo();
        Assert.Equal(0.2f, Assert.Single(document.Set.Shapes).Extents.X, 3);

        document.Stack.Undo();
        Assert.Empty(document.Set.Shapes);

        Assert.NotSame(shape, resized);
    }

    /// <summary>
    ///     ⚠ <b>A mirror that kept the same joint would put the left palm on the right wrist</b> — in
    ///     the right place in the bind pose and in the wrong place the moment either arm moved.
    /// </summary>
    [Fact]
    public void MirroringSwapsTheSideOfTheJointAsWellAsThePosition() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Rig = Rig();

        var left = document.Add(
            new() {
                Name = "palm_l",
                Kind = ShapeKind.Box,
                Joint = "Hand_l",
                Position = new(0.05f, -0.02f, 0f),
                Extents = new(0.04f, 0.02f, 0.08f)
            }
        );

        var right = document.Mirror(left);

        Assert.NotNull(right);
        Assert.Equal("palm_r", right.Name);
        Assert.Equal("Hand_r", right.Joint);
        Assert.Equal(-0.05f, right.Position.X, 4);
        Assert.Equal(-0.02f, right.Position.Y, 4);
    }

    /// <summary>A shape with no side has no other side, and inventing one is a duplicate.</summary>
    [Fact]
    public void AShapeWithNoSideIsNotMirrored() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Rig = Rig();

        var belly = document.Add(new() { Name = "belly", Kind = ShapeKind.Sphere, Joint = "Spine", Extents = new(0.2f) });

        Assert.Null(document.Mirror(belly));
        Assert.Single(document.Set.Shapes);

        // And a mirror onto a joint this rig does not have is refused rather than created dead.
        var stray = document.Add(new() { Name = "fin_l", Kind = ShapeKind.Box, Joint = "Fin_l", Extents = new(0.1f) });

        Assert.Null(document.Mirror(stray));
    }

    /// <summary>
    ///     ⚠ <b>The coarse set is a flag on the one list, not a second file.</b> A generated second
    ///     file would be regenerated over the top of somebody's override the next time anybody pressed
    ///     the button.
    /// </summary>
    [Fact]
    public void TheCoarseSetIsGeneratedAndThenOverridablePerShape() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Rig = Rig();

        document.Add(Tagged("belly", "Spine", "torso"));
        document.Add(Tagged("chest", "Spine", "torso"));
        document.Add(Tagged("palm_l", "Hand_l", "hand"));

        var chosen = document.GenerateCoarse();

        Assert.True(chosen > 0 && chosen < 3, $"a coarse set is a subset, and this one has {chosen} of 3");

        // One shape's flag, changed by hand, survives as an ordinary edit.
        var first = document.Set.Shapes[0];
        var flipped = document.Edit(first, "Override Coarse Flag", shape => shape with { Coarse = !shape.Coarse });

        Assert.NotEqual(first.Coarse, flipped.Coarse);
    }

    /// <summary>
    ///     ⚠ <b>The check nobody can make by reading either file.</b> A clip naming <c>left-palm</c>
    ///     works on the body that has one and silently does nothing on the body that calls it
    ///     <c>palm-l</c>.
    /// </summary>
    [Fact]
    public void TheCheckReportsANameOneBodyHasAndAnotherDoesNot() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        document.Rig = Rig();
        document.Add(Tagged("belly", "Spine", "torso"));
        document.Add(Tagged("palm_l", "Hand_l", "hand"));

        var view = harness.Ui.Document.Root.Add<ProxyShapeView>();

        view.Against = new() {
            Name = "Other",
            Shapes = [
                new("belly", ShapeKind.Sphere, "Spine", Vector3.Zero, Quaternion.Identity, new(0.2f), new(0.2f), [], false)
            ]
        };

        view.Show(document);
        harness.Ui.Frame();

        Click(harness, view.Check);

        var notes = view.Report.Children.Select(static child => child.Text ?? string.Empty).ToArray();

        Assert.Contains(notes, note => note.Contains("palm_l", StringComparison.Ordinal) && note.Contains("silently does nothing", StringComparison.Ordinal));
    }

    /// <summary>With no rig, the panel says what it cannot check rather than checking nothing.</summary>
    [Fact]
    public void WithNoRigThePanelSaysWhatItCannotDo() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Add(Tagged("belly", "Spine", "torso"));

        var found = Assert.Single(document.Audit());

        Assert.Contains("No rig is bound", found.Message, StringComparison.Ordinal);
        Assert.Equal(0, document.GenerateCoarse());
    }

    /// <summary>A shape on a joint this rig does not have never poses, so the row says so.</summary>
    [Fact]
    public void AShapeOnAMissingJointIsMarkedInTheList() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        document.Rig = Rig();
        document.Add(Tagged("belly", "Spine", "torso"));
        document.Add(Tagged("fin", "Dorsal", "torso"));

        var view = harness.Ui.Document.Root.Add<ProxyShapeView>();

        view.Show(document);
        harness.Ui.Frame();

        Assert.False(view.List.Children[1].HasClass("missing"));
        Assert.True(view.List.Children[2].HasClass("missing"));
    }

    // ── Handles ──────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>One undo entry per drag, not one per frame.</b> The gizmo recomputes from mouse-down
    ///     and writes absolute values every frame it moves, so a target that recorded from its setters
    ///     would put sixty entries on the stack for one gesture.
    /// </summary>
    [Fact]
    public void ADragIsOneEditHoweverManyFramesItTook() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Rig = Rig();

        var shape = document.Add(Tagged("belly", "Spine", "torso"));
        var before = document.Stack.History.Count;

        var target = new ProxyShapeGizmoTarget(
            document,
            shape,
            ProxyShapeGizmoTarget.JointOf(document.Rig, [], shape, BoneTransform.Identity)
        );

        // Three frames of a drag.
        target.Position = new(0.1f, 1.0f, 0f);
        target.Position = new(0.2f, 1.0f, 0f);
        target.Position = new(0.3f, 1.0f, 0f);

        Assert.Equal(before, document.Stack.History.Count);
        Assert.True(target.IsDirty);

        var committed = target.Commit();

        Assert.Equal(before + 1, document.Stack.History.Count);
        Assert.Equal(0.3f, committed.Position.X, 3);

        document.Stack.Undo();
        Assert.Equal(0f, Assert.Single(document.Set.Shapes).Position.X, 3);
    }

    /// <summary>A drag that moved nothing records nothing.</summary>
    [Fact]
    public void ADragThatChangedNothingIsNotAnEdit() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Rig = Rig();

        var shape = document.Add(Tagged("belly", "Spine", "torso"));
        var before = document.Stack.History.Count;

        var target = new ProxyShapeGizmoTarget(document, shape, BoneTransform.Identity);

        Assert.False(target.IsDirty);
        Assert.Same(shape, target.Commit());
        Assert.Equal(before, document.Stack.History.Count);
    }

    /// <summary>
    ///     ⚠ <b>The scale handle writes the extents, because a proxy shape has no scale of its own.</b>
    ///     A separate scale field would be a second size to reconcile with the first, and a sphere of
    ///     radius 0.2 at scale 3 is a sphere whose size nobody can read off the panel.
    /// </summary>
    [Fact]
    public void TheScaleHandleResizesTheShapeRelativeToWhereTheDragStarted() {
        using var project = new EditorFixture();
        var document = Open(project);

        document.Rig = Rig();

        var shape = document.Add(Tagged("belly", "Spine", "torso"));
        var target = new ProxyShapeGizmoTarget(document, shape, BoneTransform.Identity);

        Assert.Equal(Vector3.One, target.Scale, Near);

        target.Scale = new(2f);
        Assert.Equal(shape.Extents * 2f, target.Current.Extents, Near);

        // ⚠ Relative to the *start* and not to the current size: reading the extents back as the
        // factor would make the second frame scale the already-scaled shape and it would run away
        // under the pointer.
        target.Scale = new(3f);
        Assert.Equal(shape.Extents * 3f, target.Current.Extents, Near);
    }

    /// <summary>A handle is placed where the joint is, so the shape and its gizmo agree.</summary>
    [Fact]
    public void AHandleFollowsTheJointItHangsOff() {
        using var project = new EditorFixture();
        var document = Open(project);

        var rig = Rig();
        document.Rig = rig;

        var shape = document.Add(Tagged("palm_l", "Hand_l", "hand"));
        var world = new BoneTransform(new Vector3(10f, 0f, 0f), Quaternion.Identity, Vector3.One);

        var target = new ProxyShapeGizmoTarget(document, shape, ProxyShapeGizmoTarget.JointOf(rig, [], shape, world));

        // Hand_l sits at (−0.5, 1.2, 0) on a character standing at x = 10.
        Assert.Equal(9.5f, target.Position.X, 3);
        Assert.Equal(1.2f, target.Position.Y, 3);

        // ⚠ And a shape on a joint the rig does not have gets its handle where the character is,
        // rather than at the origin. A gizmo at the origin is one nobody can find.
        var stray = document.Add(Tagged("fin", "Dorsal", "torso"));

        Assert.Equal(world.Translation, ProxyShapeGizmoTarget.JointOf(rig, [], stray, world).Translation, Near);
    }

    static Vectors Near { get; } = new();

    sealed class Vectors : IEqualityComparer<Vector3> {
        public bool Equals(Vector3 left, Vector3 right) => (left - right).Length() < 1e-3f;

        public int GetHashCode(Vector3 value) => 0;
    }

    static ProxyShapeRecord Tagged(string name, string joint, string region) =>
        new(name, ShapeKind.Sphere, joint, Vector3.Zero, Quaternion.Identity, new(0.15f), new(0.15f), [$"region={region}"], false);

    static void Click(ViewHarness harness, UiElement element) {
        var x = element.AbsoluteLeft + (element.Width / 2f);
        var y = element.AbsoluteTop + (element.Height / 2f);

        harness.Ui.Document.Dispatch(new PointerEvent { X = x, Y = y, Action = PointerAction.Pressed, Button = PointerButton.Primary });
        harness.Ui.Document.Dispatch(new PointerEvent { X = x, Y = y, Action = PointerAction.Released, Button = PointerButton.Primary });

        harness.Ui.Frame();
    }

    static ProxyShapeDocument Open(EditorFixture project) {
        var path = project.WriteAsset("Assets/hero.vxproxyshapes", string.Empty);

        return new(project.Project, AssetId.New(), path);
    }

    static Skeleton Rig() {
        (string Name, int Parent, Vector3 Offset)[] joints = [
            ("Root", -1, Vector3.Zero),
            ("Spine", 0, new(0f, 0.9f, 0f)),
            ("Hand_l", 1, new(-0.5f, 0.3f, 0f)),
            ("Hand_r", 1, new(0.5f, 0.3f, 0f))
        ];

        var model = new Matrix4x4[joints.Length];
        var built = new SkeletonJoint[joints.Length];

        for (var index = 0; index < joints.Length; index++) {
            var local = Matrix4x4.FromTranslation(joints[index].Offset);

            model[index] = joints[index].Parent >= 0 ? local * model[joints[index].Parent] : local;

            Matrix4x4.Invert(model[index], out var inverse);

            built[index] = new() { Name = joints[index].Name, Parent = joints[index].Parent, InverseBindPose = inverse };
        }

        return Skeleton.Create(new() { Name = "Body", Joints = built });
    }
}
