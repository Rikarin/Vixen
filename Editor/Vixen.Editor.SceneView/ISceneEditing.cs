// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;

namespace Vixen.Editor.SceneView;

/// <summary>Scene authoring as a service: the entity and hierarchy verbs, callable without a window.</summary>
/// <remarks>
///     <para>
///         <b>Doc 50 § D2, the first slice of § P2b (#1290).</b> Group, Snap To Floor, Align With View
///         and their neighbours were private methods on the application, closed over its fields, so
///         a plugin that wanted "group the selection, undoably, the way the menu does" could not call
///         it. The menu lines are now names for these methods, registered by <c>SceneModule</c>
///         through the same <c>PluginContext</c> a third party uses.
///     </para>
///     <para>
///         ⚠ <b>Every verb acts on <see cref="Active" /> and its selection, and every edit is one undo
///         entry on that scene's stack.</b> A verb that touches several entities opens one
///         transaction or executes one command; none writes behind the stack's back.
///     </para>
///     <para>
///         ⚠ <b><see cref="Active" /> is the active <i>scene</i>, not <c>EditorProject.ActiveDocument</c>.</b>
///         Doc 50 proposed the second; it is a different answer whenever an asset editor's tab has
///         focus, because opening one activates its document — and a Group pressed then must still
///         group entities.
///     </para>
/// </remarks>
public interface ISceneEditing {
    /// <summary>The scene the verbs act on.</summary>
    SceneDocument Active { get; }

    /// <summary>The camera of the focused viewport, or <see langword="null" /> when no pane is showing.</summary>
    /// <remarks>What <see cref="AlignWithView" /> and <see cref="MoveToView" /> are relative to.</remarks>
    EditorCamera? View { get; }

    /// <summary>Raised after a verb changed the hierarchy in a way the outliner should redraw for.</summary>
    event Action<ISceneEditing>? HierarchyChanged;

    /// <summary>Selects every entity not selected, and nothing that was.</summary>
    void InvertSelection();

    /// <summary>Selects the children of everything selected.</summary>
    void SelectChildren();

    /// <summary>Selects the parents of everything selected.</summary>
    void SelectParent();

    /// <summary>Creates an entity under the first selected one, and selects it.</summary>
    /// <returns>The new entity, or <see cref="Entity.Null" /> when nothing was selected.</returns>
    Entity CreateChild();

    /// <summary>Puts the selection under a new empty parent, as one undo step, and selects the group.</summary>
    /// <returns>The group, or <see cref="Entity.Null" /> when nothing was selected.</returns>
    Entity Group();

    /// <summary>Puts two or more entities under a LOD group, one level each, as one undo step.</summary>
    /// <returns>The group, or <see cref="Entity.Null" /> when fewer than two were selected.</returns>
    Entity GroupAsLod();

    /// <summary>Duplicates the selection beside itself and selects the copies.</summary>
    /// <returns>How many entities were copied.</returns>
    int DuplicateSelection();

    /// <summary>Moves the selection to the scene's root.</summary>
    void ClearParent();

    /// <summary>Moves entities under a parent, undoably.</summary>
    /// <param name="entities">What to move — the selection when Set Parent was asked, ordinarily.</param>
    /// <param name="parent">The new parent, or <see cref="Entity.Null" /> for the root.</param>
    /// <returns>Whether anything moved.</returns>
    bool SetParent(IReadOnlyList<Entity> entities, Entity parent);

    /// <summary>Why entities cannot be hung from a candidate, or <see langword="null" /> if they can.</summary>
    /// <param name="entities">What would move.</param>
    /// <param name="candidate">The proposed parent.</param>
    /// <returns>The reason, in words a person reads.</returns>
    string? WhyNotParent(IReadOnlyList<Entity> entities, Entity candidate);

    /// <summary>Puts the selection where the view's camera is, looking where it looks.</summary>
    void AlignWithView();

    /// <summary>Moves the selection, keeping its shape, to the view's pivot.</summary>
    void MoveToView();

    /// <summary>Drops each selected entity straight down onto whatever is under it, or onto Y = 0.</summary>
    void SnapToFloor();
}
