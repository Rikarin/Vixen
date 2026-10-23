// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>The view a module is handed is the focused pane's, and it moves with the focus.</summary>
/// <remarks>
///     <b>#1294: <see cref="IActiveView" /> is the scene panel's model, not an adapter over the
///     application.</b> What the contract promises is the eye the focused pane draws through, asked
///     each time — so the assertions are that it follows a click into another pane of a split, which
///     is also what proves each pane's focus handler captured its own pane, and that it is empty once
///     the scene panel is closed.
/// </remarks>
public class ActiveViewTests {
    [Fact]
    public void The_published_view_follows_the_focus_between_panes_and_goes_with_the_panel() {
        using var session = EditorSession.Start();

        session.Open("scene");
        session.Frame();
        session.Run("scene.panes-quad");
        session.Settle();

        var view = session.Plugins.Services.Require<IActiveView>();
        var panes = session.Application.Viewports;

        Assert.True(panes.Count > 2);
        Assert.Same(session.Application.Viewport!.View, view.Current);

        // A press in the third pane is what a person does, and the focus handler is per pane.
        Assert.True(session.Document.Focus(panes[2].Control));
        session.Settle();

        Assert.Same(panes[2], session.Application.Viewport);
        Assert.Same(panes[2].View, view.Current);
        Assert.NotSame(panes[0].View, view.Current);

        session.Close("scene");
        session.Settle();

        Assert.Null(view.Current);
    }
}
