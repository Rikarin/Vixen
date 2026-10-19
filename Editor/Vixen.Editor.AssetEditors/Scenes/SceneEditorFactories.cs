// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Prefabs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Scenes;

/// <summary>Writes a prefab back, refusing one that is not a single subtree.</summary>
/// <remarks>
///     ⚠ <b>Refused at the save rather than at the build.</b> <c>SceneCompiler</c> will not compile a
///     prefab with two roots, so a save that wrote one would produce a file that opens, looks right,
///     and fails in somebody else's build. It throws rather than dropping a root, because dropping
///     one is losing work.
/// </remarks>
/// <param name="path">Where to write it.</param>
public sealed class PrefabFileWriter(string path) : ISceneWriter {
    /// <summary>Where the prefab is written.</summary>
    public string Path { get; } = !string.IsNullOrEmpty(path)
        ? path
        : throw new ArgumentException("A prefab writer needs a path to write to.", nameof(path));

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The document does not have exactly one root.</exception>
    public void Write(SceneDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        var roots = document.Roots.Count;

        if (roots != 1) {
            throw new InvalidOperationException(
                $"A prefab has one root and this document has {roots}. Give the loose entities a root to hang "
                + "from, or save it as a scene — the build refuses the same file for the same reason."
            );
        }

        SceneSerializer.Save(document, Path);
    }
}

/// <summary>Opens a scene.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A world comes from the host, and that is the one thing this cannot decide.</b> A
///         scene <i>is</i> an ECS world, and whether two open scenes share one world or get one each
///         is an application's decision with consequences for play mode, for entity handles and for
///         how much memory an editor with ten scenes open uses. The factory takes a supplier and
///         does not guess.
///     </para>
///     <para>
///         <b>Loading clears the stack.</b> <c>SceneSerializer.Load</c> does it, and it is what stops
///         a scene opening with fifty undo entries nobody made.
///     </para>
/// </remarks>
/// <param name="worlds">Where an opened scene's world comes from.</param>
public sealed class SceneEditorFactory(Func<DocumentRequest, World> worlds) : DocumentKind {
    readonly Func<DocumentRequest, World> supply =
        worlds ?? throw new ArgumentNullException(nameof(worlds));

    /// <inheritdoc />
    public override string Name => "Scene";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } = [SceneFile.Extension];

    /// <inheritdoc />
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(SceneDocument)];

    /// <inheritdoc />
    public override EditorDocument Open(DocumentRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var document = new SceneDocument(
            request.Project,
            supply(request),
            request.Asset,
            System.IO.Path.GetFileNameWithoutExtension(request.Path)
        ) {
            Writer = new SceneFileWriter(request.Path)
        };

        // ⚠ `Open` and not `Load`, and the difference is doc 47 § 5. A level whose prefab moved while
        // it was closed is stale in exactly the members its instances did *not* claim as their own,
        // and this is the one moment anything can put that right: an importer cannot resolve an asset
        // id to a path, so neither the build nor the runtime can ever open the prefab a scene names.
        SceneSerializer.Open(document, request.Path);
        return document;
    }
}

/// <summary>What <see cref="SceneEditorFactory" />'s documents are shown in: the Workspace half of the pair (#1294).</summary>
/// <remarks>
///     ⚠ <b>One of two views over a <see cref="SceneDocument" /></b>, which is sealed: what makes a
///     scene a prefab is the writer it was opened with, so <see cref="Owns" /> asks that. Every
///     scene that is not a prefab is this view's — the editor's own scenes, whose writer is the
///     application's, included.
/// </remarks>
public sealed class SceneEditorView : DocumentView<SceneDocument> {
    /// <inheritdoc />
    protected override bool Owns(SceneDocument document) => document.Writer is not PrefabFileWriter;

    /// <inheritdoc />
    protected override UiElement Build(SceneDocument document, UiElement panel) {
        var tabs = panel.Add<Tabs>();

        // ⚠ **The class is load-bearing and not decoration.** `tabs` carries no `flex-grow` in
        // ControlTheme — a tab set in a form is as tall as its content — so a bare one dropped into a
        // document panel collapses, and the tree inside it resolves to no height at all. The rows are
        // still there and still clickable in the tree's own terms, which is what makes the failure
        // read as "selection is broken" rather than as a layout fault.
        tabs.AddClass("document-tabs");

        // ⚠ An element in the tab rather than a binding constructed for its effect on one. The
        // difference is what happens when the tab goes: the control hears `OnUnmounted` — the
        // component hook, not an `OnRemoved` override, which the generator writes — and stops
        // listening to the document, where the old form exposed a `Detach()` this line had no handle
        // to call — and a hierarchy left subscribed does not merely rebuild a tree nobody can see,
        // it throws out of the next `SceneDocument.Add` when `Refresh` asks a removed panel for a
        // row height.
        tabs.AddTab("Hierarchy").Panel.Add<SceneHierarchyView>().Show(document);

        tabs.AddTab("Compiled").Panel.Add<CompiledSceneView>().Show(document);

        return tabs;
    }
}

/// <summary>Opens a prefab, in isolation.</summary>
/// <remarks>
///     <para>
///         <b>Isolation is a world of its own, and that is the whole of it.</b> Doc 11 asks for an
///         "isolated prefab-editing mode": a prefab opened into the scene's world would be a subtree
///         sitting in the level, editable by the level's gizmo, saved by the level's Ctrl+S. Its own
///         world means the entities cannot be reached from the scene, the hierarchy shows the prefab
///         and nothing else, and the undo stack is the document's — which is what "isolated" has to
///         mean for it to be worth having.
///     </para>
///     <para>
///         The supplier is separate from the scene factory's on purpose: a host that shares one world
///         between scenes must not share it with prefabs, and making that one function would make the
///         mistake invisible.
///     </para>
/// </remarks>
/// <param name="worlds">Where an opened prefab's own world comes from.</param>
public sealed class PrefabEditorFactory(Func<DocumentRequest, World> worlds) : DocumentKind {
    readonly Func<DocumentRequest, World> supply =
        worlds ?? throw new ArgumentNullException(nameof(worlds));

    /// <inheritdoc />
    public override string Name => "Prefab";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } = [Prefab.Extension];

    /// <inheritdoc />
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(SceneDocument)];

    /// <inheritdoc />
    public override EditorDocument Open(DocumentRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var document = new SceneDocument(
            request.Project,
            supply(request),
            request.Asset,
            System.IO.Path.GetFileNameWithoutExtension(request.Path)
        ) {
            Writer = new PrefabFileWriter(request.Path)
        };

        // ⚠ A prefab is reconciled on open too, for the nested case. A `.vxprefab` may itself hold an
        // instance of another one — R7's single level — and the outer file carries the inner links
        // verbatim, so opening the outer one is where the inner one propagates.
        SceneSerializer.Open(document, request.Path);
        return document;
    }
}

/// <summary>What <see cref="PrefabEditorFactory" />'s documents are shown in: the Workspace half of the pair (#1294).</summary>
/// <remarks>The other view over a <see cref="SceneDocument" /> — see <see cref="SceneEditorView" />.</remarks>
public sealed class PrefabEditorView : DocumentView<SceneDocument> {
    /// <inheritdoc />
    protected override bool Owns(SceneDocument document) => document.Writer is PrefabFileWriter;

    /// <inheritdoc />
    protected override UiElement Build(SceneDocument document, UiElement panel) {
        var view = panel.Add<PrefabView>();

        view.Show(document);

        return view;
    }
}
