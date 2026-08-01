// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.SceneView;

/// <summary>Whatever gets first refusal on a pane's input before the pane itself reads it.</summary>
/// <remarks>
///     <para>
///         <b>This is the viewport's half of doc 20's <c>IEditorMode</c>, and it is deliberately not
///         that interface.</b> A mode is a shell concept — it has a title, an icon, a place on the
///         mode bar and a claim on the keymap — and this assembly has never heard of a shell:
///         <c>Vixen.Editor.SceneView</c> does not reference <c>Vixen.Editor.Ui</c>, for the reason
///         doc 02 gives, which is that a viewport that needed the editor's chrome to be constructible
///         would not be testable headless. So the pane declares the seam it needs and the application
///         is what joins the two — the same bargain <see cref="SceneViewport.TargetsFactory" />,
///         <see cref="SceneViewport.Surfaces" /> and <see cref="SceneViewport.Picker" /> already make.
///     </para>
///     <para>
///         ⚠ <b>Refusal is over what a gesture <i>starts</i>, not over one already running.</b> A
///         pointer event that arrives while the gizmo is being dragged or a rubber-band is open goes
///         to the pane whatever this says, because a mode that could take the release of a drag it did
///         not begin would leave the gizmo holding the object and the band on screen with nothing
///         updating it. Keys are the other way round and
///         <see cref="Key" /> says why.
///     </para>
/// </remarks>
public interface IViewportInput {
    /// <summary>Offers a pointer event to whatever owns the pane's input.</summary>
    /// <param name="pane">The pane it happened in.</param>
    /// <param name="args">The event.</param>
    /// <returns>Whether it was taken, in which case the pane does nothing else with it.</returns>
    bool Pointer(SceneViewport pane, PointerEvent args);

    /// <summary>Offers a key event to it.</summary>
    /// <param name="pane">The pane.</param>
    /// <param name="args">The event.</param>
    /// <returns>Whether it was taken.</returns>
    /// <remarks>
    ///     ⚠ <b>Offered during a drag, unlike a pointer event, and doc 24's P0 is the reason.</b>
    ///     Typing <c>X 5 ⏎</c> partway through a translate is Blender's numeric entry and it is only
    ///     meaningful while a drag is in flight — a hook that stood down for the duration of a drag
    ///     would be one the feature cannot be written against. What still comes first is Escape, which
    ///     is the drag's own way out and has to stay reachable from inside any mode.
    /// </remarks>
    bool Key(SceneViewport pane, KeyEvent args);
}
