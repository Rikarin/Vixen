// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Engine.Transforms;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>Doc 50 § P5 (#1291): a gizmo drag recorded through <see cref="EditProperty" />.</summary>
/// <remarks>
///     ⚠ <b>The shape is the exit criterion.</b> One entry named <c>Move (3)</c> for a drag of three
///     entities — the gizmo's own N-target command, not a composite of a command per entity per
///     member, which is what recording each target through the pipeline's write path would make.
/// </remarks>
public sealed class GizmoRecordTests : IDisposable {
    readonly World world = new("Test");
    readonly TransformSystem transforms = new();
    readonly SceneDocument document;

    public GizmoRecordTests() {
        var project = new EditorProject(new ProjectPaths(Path.Combine(Path.GetTempPath(), "vixen-gizmo-record")));
        document = new SceneDocument(project, world, AssetId.Empty, "Test");
    }

    public void Dispose() => world.Dispose();

    /// <summary>What a frame does between two drags: resolve world transforms, then move the version on.</summary>
    void Resolve() {
        transforms.Resolve(world);
        world.AdvanceVersion();
    }

    [Fact]
    public void A_drag_of_three_entities_is_one_entry_that_puts_all_three_back() {
        EntityGizmoTarget[] targets = [
            new(world, Hierarchy.CreateTransform(world, LocalTransform.Identity)),
            new(world, Hierarchy.CreateTransform(world, LocalTransform.Identity)),
            new(world, Hierarchy.CreateTransform(world, LocalTransform.Identity))
        ];

        for (var index = 0; index < targets.Length; index++) {
            targets[index].Position = new Vector3(index, 0f, 0f);
        }

        // A world position is read from `WorldTransform`, which the editor resolves every frame.
        Resolve();

        var captured = targets.Select(GizmoTransformMember.State).ToArray();

        // The drag, as the gizmo does it: written straight to the entities, frame by frame.
        foreach (var target in targets) {
            target.Position += new Vector3(0f, 5f, 0f);
        }

        Resolve();

        // The fixture's own check: the drag is visible to a reader, which is what the gizmo reads.
        Assert.Equal(new Vector3(2f, 5f, 0f), targets[2].Position);

        var drag = new GizmoDrag(targets, GizmoMode.Translate, GizmoHandle.AxisY, captured, document);
        var edit = targets[0].Record(drag);

        Assert.NotNull(edit);
        Assert.Same(document.Stack, edit.Value.Stack);
        Assert.IsType<TransformTargetsCommand>(edit.Value.Command);
        Assert.Equal("Move (3)", edit.Value.Command.Name);

        document.Stack.Execute(edit.Value.Command);

        Assert.Equal(1, document.Stack.Depth.Value);

        document.Stack.Undo();
        Resolve();

        for (var index = 0; index < targets.Length; index++) {
            Assert.Equal(new Vector3(index, 0f, 0f), targets[index].Position);
        }

        document.Stack.Redo();
        Resolve();

        for (var index = 0; index < targets.Length; index++) {
            Assert.Equal(new Vector3(index, 5f, 0f), targets[index].Position);
        }
    }

    [Fact]
    public void A_drag_that_moved_nothing_records_nothing() {
        EntityGizmoTarget target = new(world, Hierarchy.CreateTransform(world, LocalTransform.Identity));

        Resolve();

        var drag = new GizmoDrag([target], GizmoMode.Translate, GizmoHandle.AxisX, [GizmoTransformMember.State(target)], document);

        Assert.Null(target.Record(drag));
    }
}
