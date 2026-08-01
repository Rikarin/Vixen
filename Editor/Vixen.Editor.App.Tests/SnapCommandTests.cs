// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 24's D4 and B5, from the editor: one snapping context, reachable from the strip.</summary>
public class SnapCommandTests {
    [Fact]
    public void Every_element_the_context_has_can_be_turned_on_from_the_editor() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var snap = fixture.Viewport!.Gizmo.Snap;

        // ⚠ This is doc 24's B5 in the form it survived in. `SnapToVertex` had been honoured by the
        // viewport for a while; what there was no way to do was turn it on — `scene.toggle-snap`
        // moves the increment, the angle and the scale and says nothing about the four that need
        // geometry under the pointer.
        Assert.False(snap.SnapsToGeometry);

        foreach (var element in (SnapElements[]) [
            SnapElements.Vertex, SnapElements.Edge, SnapElements.EdgeCentre, SnapElements.Face
        ]) {
            var id = EditorApplication.ViewportIds.SnapElement(element);

            Assert.True(fixture.CanRun(id));
            fixture.Run(id);

            Assert.True(snap.Has(element));
            Assert.True(fixture.Shell.Commands[id]!.IsChecked);

            fixture.Run(id);
            Assert.False(snap.Has(element));
        }
    }

    [Fact]
    public void The_base_is_a_choice_of_one_rather_than_four_ticks() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var snap = fixture.Viewport!.Gizmo.Snap;

        Assert.Equal(SnapBase.Origin, snap.Base);

        fixture.Run(EditorApplication.ViewportIds.SnapBase(SnapBase.Pointer));

        Assert.Equal(SnapBase.Pointer, snap.Base);
        Assert.True(fixture.Shell.Commands[EditorApplication.ViewportIds.SnapBase(SnapBase.Pointer)]!.IsChecked);
        Assert.False(fixture.Shell.Commands[EditorApplication.ViewportIds.SnapBase(SnapBase.Origin)]!.IsChecked);
    }

    [Fact]
    public void The_modifiers_are_toggles_and_start_where_the_context_says() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Frames(2);

        var snap = fixture.Viewport!.Gizmo.Snap;
        var id = EditorApplication.ViewportIds.SnapModifier(SnapModifiers.AlignToTarget);

        Assert.True(snap.Is(SnapModifiers.AlignToTarget));

        fixture.Run(id);
        Assert.False(snap.Is(SnapModifiers.AlignToTarget));
    }

    [Fact]
    public void Every_pane_and_every_drop_share_one_context() {
        using var fixture = EditorSession.Start();

        fixture.Open("scene");
        fixture.Run("scene.panes-quad");
        fixture.Frames(2);

        var panes = fixture.Viewports;

        Assert.Equal(4, panes.Count);

        // ⚠ Doc 24's D4. Snapping is a claim about how the user is working, not about which pane they
        // are looking through — four panes disagreeing about whether vertex snapping is on is the
        // same confusion as a vertex snap that works for a drag and not for an extrude.
        Assert.All(panes, pane => Assert.Same(panes[0].Gizmo.Snap, pane.Gizmo.Snap));
        Assert.All(panes, pane => Assert.Same(panes[0].Gizmo.Snap, pane.Placement.Snap));

        fixture.Run(EditorApplication.ViewportIds.SnapElement(SnapElements.Face));

        // Turned on once, and the drop that stands a crate up on a ramp agrees with the drag that
        // does — which is the disagreement one context was built to make impossible.
        Assert.All(panes, pane => Assert.True(pane.Placement.Snap.SnapToSurface));
    }
}
