// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Editor.AssetEditors.Scenes;

/// <summary>A scene's entities as a tree: what is in it, what is selected, and what things are called.</summary>
/// <remarks>
///     <para>
///         <b>The half of doc 11's scene editor that is not a viewport.</b> A scene editor is "scene
///         view + hierarchy + inspector"; the viewport is <c>Vixen.Editor.SceneView</c>'s and the
///         inspector is <c>Vixen.Editor.Inspector</c>'s, and this is the third — written once, so
///         that the scene editor and the prefab editor are one tree over two documents.
///     </para>
///     <para>
///         The panel is <c>SceneHierarchyView.vxml</c>; this file is the accessibility modifier and
///         the reasoning, the same arrangement as <c>CompiledSceneView</c> beside it.
///     </para>
///     <para>
///         ⚠ <b>A control rather than a model over a panel</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1322">#1322</a>). It was
///         <c>new SceneHierarchyView(scene, panel)</c> — a plain class that added a
///         <see cref="Vixen.Ui.Controls.Advanced.TreeView" /> to whatever container it was handed —
///         and the cost of that was a <c>Detach()</c> its own production caller could not call:
///         <c>SceneEditorFactory.CreateView</c> constructed one for its effect and kept no handle.
///         An element is told when its subtree leaves the document; a plain class has nowhere to
///         hear it.
///     </para>
///     <para>
///         ⚠ <b>Rebuilt from <c>StructureChanged</c>, not polled.</b> The document raises it when
///         entities appear, disappear or change parent, and deliberately does not raise it for a
///         transform edit or a rename — a tree rebuilt on every frame of a gizmo drag would lose its
///         expansion state forty times a second. A rename moves one row's text instead.
///     </para>
///     <para>
///         ⚠ <b>Selection travels out of this and not into it.</b> Clicking a row writes
///         <c>SceneDocument.Selection</c>; something else selecting an entity does not move the
///         highlight, because nothing here subscribes to the signal. That is the same gap the
///         application's own hierarchy has, and the fix is the same <c>Effect</c> that needs a
///         reactive scheduler the editor's loop does not flush.
///     </para>
/// </remarks>
public sealed partial class SceneHierarchyView;
