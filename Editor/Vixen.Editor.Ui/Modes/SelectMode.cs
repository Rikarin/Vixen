// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Ui;

/// <summary>The mode the editor is in when it is not in another one: pick and transform entities.</summary>
/// <remarks>
///     <para>
///         <b>The mode that has to exist for the others to be leaveable, and it does nothing.</b> That
///         is the point. Doc 20's A1 asks for <c>IEditorMode</c> to ship with one mode so that the
///         seam is proven and nothing depends on the mode set being final; this is that mode, and
///         every member of it is the neutral answer — no context, no panel, no toolbar, and no claim
///         on any input at all.
///     </para>
///     <para>
///         ⚠ <b>Which is why entering it is the same as the editor having no modes.</b> A viewport in
///         Select mode behaves exactly as the viewport did before modes existed: <c>1..9</c> are the
///         view bookmarks, a click picks an entity, and the gizmo has the drag. Any other arrangement
///         would mean the shipped editor's behaviour depended on a feature it does not use yet.
///     </para>
///     <para>
///         It lives here rather than in an application because it is the definition of "no mode" and
///         every host of this shell that has a viewport wants the same one.
///     </para>
/// </remarks>
public sealed class SelectMode : IEditorMode {
    /// <summary>What the mode is called, everywhere an id is wanted.</summary>
    public const string ModeId = "select";

    /// <inheritdoc />
    public string Id => ModeId;

    /// <inheritdoc />
    public StringId Title { get; } = new("editor.mode.select", "Select");

    /// <inheritdoc />
    public PathBuilder? Icon => null;

    /// <inheritdoc />
    public IconArt? Art => ModeArt.Select;

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Null, so that the viewport keeps whatever context it already had.</b> A Select mode
    ///     that claimed a context of its own would shadow the outliner's scoped verbs the moment the
    ///     pointer moved into the pane — and the editor's entity commands are scoped to the scene,
    ///     which is the context the pane already reports.
    /// </remarks>
    public string? Context => null;

    /// <inheritdoc />
    public string? Panel => null;

    /// <inheritdoc />
    public IReadOnlyList<ToolbarEntry> Toolbar => [];

    /// <inheritdoc />
    public void Register(EditorShell shell) => ArgumentNullException.ThrowIfNull(shell);

    /// <inheritdoc />
    public void Unregister(EditorShell shell) => ArgumentNullException.ThrowIfNull(shell);

    /// <inheritdoc />
    public void Activated() {
    }

    /// <inheritdoc />
    public void Deactivated() {
    }

    /// <inheritdoc />
    public bool Pointer(PointerEvent args) => false;

    /// <inheritdoc />
    public bool Key(KeyEvent args) => false;
}
