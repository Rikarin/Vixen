// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.SceneView.Tests;

/// <summary>The last hop of a play session's overlay geometry: from the pane into the line list.</summary>
/// <remarks>
///     <para>
///         <b>The half that turns a wired overlay into a picture</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1247">#1247</a>). An overlay writes into
///         a <c>DebugDraw</c> the application owns; the application turns that into segments; this is
///         what puts them in front of a camera. Without it the whole wiring is a finished thing
///         nothing calls — the accumulator fills every frame and no pane reads it.
///     </para>
///     <para>
///         ⚠ <b>Asserted against <c>World</c> and not <c>Overlay</c>, which is the load-bearing
///         choice.</b> A collider behind a wall is behind the wall; drawing it in the overlay channel
///         would put every wireframe in the level in front of the geometry it describes, which reads
///         as the colliders being in the wrong place rather than as the wrong channel.
///     </para>
/// </remarks>
public class SceneDiagnosticLineTests : IDisposable {
    static readonly Color4 Red = new(1f, 0f, 0f, 1f);

    readonly string root = Path.Combine(Path.GetTempPath(), "vixen-diagnostics-" + Guid.NewGuid().ToString("N"));
    readonly EditorProject project;
    readonly World world = new("Test");
    readonly SceneDocument scene;

    public SceneDiagnosticLineTests() {
        Directory.CreateDirectory(root);
        project = new(new ProjectPaths(root));
        scene = new(project, world, AssetId.Empty, "Untitled");
    }

    public void Dispose() {
        world.Dispose();

        if (Directory.Exists(root)) {
            Directory.Delete(root, true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A pane pointed at a diagnostic list draws it, and one pointed at nothing does not.</summary>
    /// <remarks>
    ///     ⚠ <b>A differential over the same pane rather than a count</b>, because the baseline is
    ///     whatever the grid, the markers and the cage came to for this scene and pane — a number no
    ///     assertion should own.
    /// </remarks>
    [Fact]
    public void A_panes_diagnostic_lines_reach_the_depth_tested_list() {
        using var pane = new Pane();
        var lines = new SceneLines();

        lines.Build(scene, pane.Viewport, 600);

        var bare = lines.World.Count;
        var overlay = lines.Overlay.Count;

        pane.Viewport.Diagnostics = [
            new LineVertex(Vector3.Zero, Red),
            new LineVertex(Vector3.UnitY, Red),
            new LineVertex(Vector3.UnitY, Red),
            new LineVertex(Vector3.UnitX, Red)
        ];

        lines.Build(scene, pane.Viewport, 600);

        Assert.Equal(bare + 4, lines.World.Count);

        // ⚠ And nothing landed in the overlay channel, which is the assertion that says which of the
        // two lists this went into rather than merely that it went somewhere.
        Assert.Equal(overlay, lines.Overlay.Count);

        // The vertices themselves, in the order they were given: a drain that reversed a segment
        // draws the same picture and a drain that dropped one does not.
        Assert.Equal(Vector3.Zero, lines.World[bare].Position);
        Assert.Equal(Vector3.UnitX, lines.World[bare + 3].Position);
    }

    /// <summary>And a rebuild with the list emptied takes them away again.</summary>
    /// <remarks>
    ///     ⚠ <b>The state after Stop.</b> The application clears its accumulator every frame and
    ///     refills it from whatever the session drew, so the frame after a session ends hands the
    ///     pane an empty list — a <c>Build</c> that had adopted the list rather than copying out of
    ///     it would keep the last session's colliders standing where the bodies were.
    /// </remarks>
    [Fact]
    public void An_emptied_diagnostic_list_draws_nothing() {
        using var pane = new Pane();
        var lines = new SceneLines();

        List<LineVertex> live = [new(Vector3.Zero, Red), new(Vector3.UnitY, Red)];

        pane.Viewport.Diagnostics = live;
        lines.Build(scene, pane.Viewport, 600);

        var withLines = lines.World.Count;

        live.Clear();
        lines.Build(scene, pane.Viewport, 600);

        Assert.Equal(withLines - 2, lines.World.Count);
    }
}
