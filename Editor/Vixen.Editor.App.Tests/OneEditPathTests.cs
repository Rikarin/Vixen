// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 36 § P1's exit criterion: two surfaces, one history, in the order they happened.</summary>
/// <remarks>
///     ⚠ <b>The finding this is the answer to is that there was no single edit path.</b> Inspector
///     fields went through one command, gizmo drags through another, terrain strokes and graph edits
///     through two more, and a viewport hook through a fifth — five defensible designs that together
///     meant a new editing surface had to invent a sixth and a plugin could not join undo at all.
///     Driven through the shell rather than through the model, because what is being tested is that
///     the two paths meet, and each of them is already right on its own.
/// </remarks>
public class OneEditPathTests {
    [Fact]
    public void A_gizmo_drag_and_a_field_edit_land_on_one_stack_in_the_order_they_happened() {
        using var editor = EditorSession.Start();

        editor.Open("scene");
        editor.Open("hierarchy");
        editor.Open("inspector");
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var scene = editor.Scene;
        var viewport = editor.Viewport ?? throw new InvalidOperationException("The scene panel opened no pane.");

        // Whatever opening the scene recorded is not what this is about.
        scene.Stack.Clear();

        viewport.Gizmo.Mode = GizmoMode.Translate;
        editor.Settle();

        var target = Assert.IsType<EntityGizmoTarget>(Assert.Single(viewport.Gizmo.Targets));
        var was = target.Position;

        // The drag, through the viewport's real end-of-drag path.
        Assert.True(viewport.Gizmo.Begin(GizmoHandle.AxisX, viewport.Ray(new(400f, 300f)), viewport.Camera));
        target.Position = was + new Vector3(3f, 0f, 0f);

        // ⚠ A frame, because `Transform.Position` reads `WorldTransform` and only the transform
        // system writes that. A real drag spans frames; a test that set and read in one would be
        // measuring the entity where it was.
        editor.Settle();

        Assert.True(viewport.EndManipulate());

        // The field edit, through the inspector's real write path.
        var name = Row(editor, "Name");

        Assert.True(name.Write("Key Light"));

        Assert.Equal(["Move", "Set Name"], scene.Stack.History.Select(static entry => entry.Name));

        // And they come back off in reverse, which is the whole of what "one stack" buys. A frame
        // after each, for the reason above: an undo writes the local transform and the world one
        // catches up in the pass that follows.
        scene.Stack.Undo();
        editor.Settle();

        Assert.Equal("Directional Light", scene.NameOf(target.Entity));
        Assert.Equal(was.X + 3f, target.Position.X, 3);

        scene.Stack.Undo();
        editor.Settle();

        Assert.Equal(was.X, target.Position.X, 3);
    }

    /// <summary>
    ///     ⚠ <b>The inspector follows the drag because both wrote the same object, not because the
    ///     viewport told it to.</b> A gizmo that had to notify every panel is a gizmo that grows a
    ///     notification per panel.
    /// </summary>
    [Fact]
    public void The_inspector_reads_back_what_the_gizmo_wrote() {
        using var editor = EditorSession.Start();

        editor.Open("scene");
        editor.Open("hierarchy");
        editor.Open("inspector");
        editor.ClickRow(editor.Hierarchy, "Directional Light");
        editor.Settle();

        var viewport = editor.Viewport ?? throw new InvalidOperationException("The scene panel opened no pane.");

        viewport.Gizmo.Mode = GizmoMode.Translate;
        editor.Settle();

        var target = Assert.IsType<EntityGizmoTarget>(Assert.Single(viewport.Gizmo.Targets));
        var moved = target.Position + new Vector3(0f, 2.5f, 0f);

        Assert.True(viewport.Gizmo.Begin(GizmoHandle.AxisY, viewport.Ray(new(400f, 300f)), viewport.Camera));
        target.Position = moved;

        editor.Settle();

        Assert.True(viewport.EndManipulate());

        editor.Settle();

        var position = Row(editor, "Position").Read();

        Assert.False(position.IsMixed);
        Assert.Equal(moved.Y, position.Or(Vector3.Zero).Y, 3);
    }

    static Inspector.InspectorField Row(EditorSession editor, string member) =>
        editor.Inspector.Rows
            .Select(static row => row.Field)
            .FirstOrDefault(field => field.Member.Name == member)
        ?? throw new InvalidOperationException($"The inspector is showing no '{member}' row.");
}
