// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.SceneView;
using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.App;

/// <summary>Gives the active editor mode first refusal on a scene pane's input.</summary>
/// <remarks>
///     <para>
///         <b>Four lines, and they are the join doc 20's A1 and doc 24's B2 both describe.</b>
///         <c>IEditorMode</c> lives in <c>Vixen.Editor.Ui</c> because a mode has a title, an icon, a
///         place on the mode bar and a claim on the keymap; <c>IViewportInput</c> lives in
///         <c>Vixen.Editor.SceneView</c> because a pane has to be constructible with no shell around
///         it. Neither assembly references the other, deliberately — see the solution's Editor note —
///         so the application is what puts them together, exactly as it does for the picker, the
///         surface probe and the gizmo's targets.
///     </para>
///     <para>
///         ⚠ <b>The registry rather than the mode, so switching modes does not have to walk the
///         panes.</b> A four-pane layout has four <c>Input</c> properties and one active mode; asking
///         the registry per event costs a property read and means a mode entered from the mode bar,
///         from the palette or from a key is in force in every pane on the same frame.
///     </para>
/// </remarks>
sealed class ModeInput(EditorModes modes) : IViewportInput {
    /// <inheritdoc />
    public bool Pointer(SceneViewport pane, PointerEvent args) =>
        modes.Active is IViewportInput input
            ? input.Pointer(pane, args)
            : modes.Active?.Pointer(args) == true;

    /// <inheritdoc />
    public bool Key(SceneViewport pane, KeyEvent args) =>
        modes.Active is IViewportInput input
            ? input.Key(pane, args)
            : modes.Active?.Key(args) == true;

    // ⚠ The pane-aware overload wins when the mode has one, and the two are not both offered. A mode
    // that implements `IViewportInput` has said which pane it wants to be asked about; offering it the
    // pane-less event as well would mean writing every gesture twice and having the two disagree — see
    // `BlockoutMode.Pointer`, whose pane-less overload declines precisely because it cannot answer.
}
