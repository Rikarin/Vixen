// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using ViewportControl = Vixen.Ui.Controls.Advanced.Viewport;

namespace Vixen.Editor.SceneView;

/// <summary>What a scene-view tool or mode is handed of the pane whose input it was offered.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § P4 (#1303), and it is a list read off the consumers rather than designed.</b>
///         Doc 36 § Part 5 struck an <c>IToolContext</c> because <see cref="SceneViewport" /> was a
///         1,600-line concrete class and "narrowing it is worth doing when a consumer can say which
///         members matter". The consumers are the five <see cref="IViewportInput" />s that exist —
///         <c>BlockoutMode</c>, <c>TerrainMode</c>, <c>FoliageMode</c>, <c>WaterMode</c> and the
///         out-of-tree sample plugin's tool — and these are every member any of them touches.
///     </para>
///     <para>
///         ⚠ <b>Narrowing what a tool is handed, not what a pane is.</b> The pane still has its
///         bookmarks, its selection, its editing state and its gizmo; the application that owns the
///         pane reaches them as before. What changed is that a plugin's tool can no longer reach them,
///         so a new member here is a decision a reviewer sees rather than a public property it
///         happened to find.
///     </para>
/// </remarks>
public interface IViewportPane {
    /// <summary>The control the scene is drawn in — what turns an event's coordinates into render pixels.</summary>
    ViewportControl Control { get; }

    /// <summary>Where the camera is looking.</summary>
    EditorCamera Camera { get; }

    /// <summary>What a ray hits in the scene this pane shows, or <see langword="null" /> while nothing answers.</summary>
    ISceneProbe? Surfaces { get; }

    /// <summary>The tool's own cursor, drawn every frame over the scene, or <see langword="null" /> for none.</summary>
    /// <remarks>See <see cref="SceneViewport.Cursor" />: set it on hover, clear it on leave.</remarks>
    Action<GizmoDraw>? Cursor { get; set; }

    /// <summary>The ray under a point in this pane.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <returns>The ray, in world space.</returns>
    Ray Ray(Vector2 point);

    /// <summary>Records which gizmo handle the pointer is over.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <returns>The handle, or <see cref="GizmoHandle.None" />.</returns>
    GizmoHandle Hover(Vector2 point);

    /// <summary>Works out which mesh element the pointer is over, and remembers it.</summary>
    /// <param name="point">Where, in render pixels.</param>
    /// <returns>What is under it.</returns>
    SubObject HoverElement(Vector2 point);

    /// <summary>Rebuilds what the gizmo is holding, now rather than on the next frame.</summary>
    /// <returns>Whether there is anything to drag.</returns>
    bool RefreshTargets();
}
