// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Rendering;

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

    // ── Doc 20 § Part D's selection and transform verbs (#1290's second slice) ──────────────────

    /// <summary>Raised after a verb changed what is hidden, so the outliner restates its eyes.</summary>
    event Action<ISceneEditing>? MarksChanged;

    /// <summary>Whether <see cref="Isolate" /> is hiding everything outside a selection.</summary>
    bool IsIsolating { get; }

    /// <summary>Hides everything the selection is not part of, or — while isolating — puts it all back.</summary>
    /// <remarks>
    ///     ⚠ <b>Not undoable, on purpose</b>: what is hidden is where somebody is looking, not what they
    ///     changed. See <c>entity.toggle-hidden</c>.
    /// </remarks>
    void Isolate();

    /// <summary>Selects every entity whose name contains some text, ignoring case.</summary>
    /// <param name="text">What to look for.</param>
    /// <returns>What was found and selected; empty, with the selection untouched, for nothing.</returns>
    IReadOnlyList<Entity> SelectByName(string text);

    /// <summary>The components something in the active scene carries, by name, with how many carry each.</summary>
    /// <returns>The components, ordered by name.</returns>
    IReadOnlyList<(string Component, int Count)> ComponentsInScene();

    /// <summary>Selects every entity carrying a component.</summary>
    /// <param name="component">The component's registered name.</param>
    /// <returns>What was found and selected; empty, with the selection untouched, for nothing.</returns>
    IReadOnlyList<Entity> SelectByType(string component);

    /// <summary>The named selections, as saved.</summary>
    IReadOnlyDictionary<string, IReadOnlyList<Entity>> SelectionSets { get; }

    /// <summary>Names the current selection so it can be brought back.</summary>
    /// <param name="name">What to call it.</param>
    /// <returns>Whether there was a selection to save.</returns>
    bool SaveSelectionSet(string name);

    /// <summary>Brings a named selection back, less whatever has since been deleted.</summary>
    /// <param name="name">Which.</param>
    /// <returns>What was selected; empty, with the selection untouched, for nothing alive.</returns>
    IReadOnlyList<Entity> RecallSelectionSet(string name);

    /// <summary>Puts the selection back at its parent's origin, unrotated and unscaled, as one undo entry.</summary>
    void ResetTransform();

    /// <summary>Whether the primary selection has a transform to copy.</summary>
    bool CanCopyTransform { get; }

    /// <summary>Whether something has been copied for <see cref="PasteTransform" />.</summary>
    bool HasCopiedTransform { get; }

    /// <summary>Remembers the primary selection's local transform.</summary>
    /// <returns>Whether there was one.</returns>
    bool CopyTransform();

    /// <summary>Writes the copied transform onto everything selected, as one undo entry.</summary>
    void PasteTransform();

    /// <summary>Puts everything selected on the primary's line along one axis, as one undo entry.</summary>
    /// <param name="axis">0, 1 or 2 for X, Y or Z.</param>
    void Align(int axis);

    /// <summary>Spaces everything selected evenly between the two ends along one axis, as one undo entry.</summary>
    /// <param name="axis">0, 1 or 2 for X, Y or Z.</param>
    void Distribute(int axis);

    // ── The create verbs (#1290's third slice) ─────────────────────────────────────────────────

    /// <summary>Creates an empty entity under the first selected one, or at the root, and selects it.</summary>
    /// <returns>The new entity. One undo entry.</returns>
    Entity CreateEntity();

    /// <summary>Creates one of the built-in shapes under the first selected entity, and selects it.</summary>
    /// <param name="kind">Which shape.</param>
    /// <returns>The new entity. One undo entry.</returns>
    Entity CreateShape(PrimitiveKind kind);

    /// <summary>Creates a light under the first selected entity, aimed down and forward, and selects it.</summary>
    /// <param name="kind">Which kind.</param>
    /// <returns>The new entity. One undo entry.</returns>
    Entity CreateLight(LightKind kind);

    /// <summary>Creates a camera under the first selected entity, and selects it.</summary>
    /// <returns>The new entity. One undo entry.</returns>
    Entity CreateCamera();
}
