// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.SceneView;

/// <summary>Which corner of the pane an overlay sits in.</summary>
/// <remarks>
///     ⚠ <b>A corner rather than coordinates, because a pane is not a canvas.</b> Panes are split,
///     resized and rearranged, and an overlay placed at a pixel offset ends up under the toolbar in
///     one layout and off the edge in another. Four corners is what a docked thing can say about
///     where it wants to be and still be somewhere sensible in every layout.
/// </remarks>
public enum OverlayCorner : byte {
    /// <summary>Top left, under the toolbar.</summary>
    TopLeft,

    /// <summary>Top right.</summary>
    TopRight,

    /// <summary>Bottom left.</summary>
    BottomLeft,

    /// <summary>Bottom right, above the stats readout.</summary>
    BottomRight
}

/// <summary>A panel floating over a scene pane.</summary>
/// <param name="Id">What everything refers to it by. Prefix a plugin's with the plugin's own id.</param>
/// <param name="Title">What its header says.</param>
/// <param name="Build">Fills a host element for one pane. Called once per pane.</param>
/// <param name="Corner">Where it sits.</param>
/// <param name="Order">Where among the overlays sharing a corner, low first.</param>
/// <remarks>
///     <para>
///         <b>Doc 36 § D4's <c>AddOverlay</c>, and Unity's <c>[Overlay]</c>.</b> The scene pane
///         already had chrome — a toolbar, a stats readout, a rubber-band — built by
///         <c>ViewportChrome</c> as ordinary elements in <c>Viewport.Overlay</c>. What it did not have
///         is a way for anything but the application to put something there.
///     </para>
///     <para>
///         ⚠ <b>Built once per pane, not once.</b> Two panes side by side are two cameras, two view
///         modes and two active tools, so an overlay showing any of that has to be two elements
///         reading two <see cref="SceneViewport" />s. One element shared between panes would show the
///         first pane's numbers over the second pane's picture, which is the failure
///         <c>ViewportChrome</c>'s own remarks describe for the toolbar.
///     </para>
///     <para>
///         ⚠ <b>The pane is handed in rather than fetched.</b> An overlay that reached for
///         <c>ViewportLayout.Focused</c> would be right for the focused pane and lying over every
///         other one.
///     </para>
/// </remarks>
public sealed record SceneOverlay(
    string Id,
    string Title,
    Action<UiElement, SceneViewport> Build,
    OverlayCorner Corner = OverlayCorner.TopRight,
    int Order = 0
);

/// <summary>Marks a static method as a panel floating over the scene pane.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D3.</b> The method is what <see cref="SceneOverlay.Build" /> takes: it is
///         handed a host element and the pane, and fills the first for the second.
///     </para>
///     <code language="csharp">
///         [Overlay("Snapping", Corner = OverlayCorner.BottomLeft)]
///         public static void Build(UiElement host, SceneViewport pane) { … }
///     </code>
///     <para>
///         ⚠ <b>A static method rather than a class, unlike Unity's <c>[Overlay]</c>.</b> Unity's goes
///         on an <c>Overlay</c> subclass because the base carries the docking state, the collapsed
///         state and the display name. Ours has no base: an overlay <i>is</i> an
///         <c>Action&lt;UiElement, SceneViewport&gt;</c>, its title is on the attribute, and where it
///         sits is <see cref="SceneOverlay.Corner" />. Same reasoning as
///         <c>CustomInspectorAttribute</c>, and it matches the rest of the set.
///     </para>
///     <para>
///         ⚠ <b>Read by a scan of the assembly that declared it, and only for a plugin or a project
///         script</b> — see <c>CustomInspectorAttribute</c> for why that is bounded rather than the
///         assembly scanning ADR-002 refuses. In-tree code registers a <see cref="SceneOverlay" />
///         directly.
///     </para>
/// </remarks>
/// <param name="title">What its header says.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class OverlayAttribute(string title) : Attribute {
    /// <summary>What its header says.</summary>
    public string Title { get; } = title;

    /// <summary>Where it sits.</summary>
    public OverlayCorner Corner { get; init; } = OverlayCorner.TopRight;

    /// <summary>Its id, or empty to derive one from the title.</summary>
    /// <remarks>
    ///     ⚠ <b>Worth setting for anything whose visibility should be remembered.</b> A derived id
    ///     changes when the title does, so renaming the overlay forgets whether it was open.
    /// </remarks>
    public string Id { get; init; } = string.Empty;

    /// <summary>Where among the overlays sharing a corner, low first.</summary>
    public int Order { get; init; }
}
