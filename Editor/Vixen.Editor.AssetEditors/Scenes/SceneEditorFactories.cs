// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Prefabs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
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
public sealed class SceneEditorFactory(Func<AssetEditorRequest, World> worlds) : IAssetEditorFactory {
    readonly Func<AssetEditorRequest, World> supply =
        worlds ?? throw new ArgumentNullException(nameof(worlds));

    /// <inheritdoc />
    public string Name => "Scene";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [SceneFile.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var document = new SceneDocument(
            request.Project,
            supply(request),
            request.Asset,
            System.IO.Path.GetFileNameWithoutExtension(request.Path)
        ) {
            Writer = new SceneFileWriter(request.Path)
        };

        SceneSerializer.Load(document, request.Path);
        return document;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Two tabs over one document, not two documents.</b> The compiled pane reads the same
    ///     entities the tree does and writes nothing, so it shares this document's undo stack and its
    ///     dirty flag by not having any of its own — the rule <c>AssetEditorRegistry</c> states, that
    ///     two documents over one file are two undo histories over one set of bytes.
    /// </remarks>
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var scene = (SceneDocument) document;
        var tabs = panel.Add<Tabs>();

        // ⚠ **The class is load-bearing and not decoration.** `tabs` carries no `flex-grow` in
        // ControlTheme — a tab set in a form is as tall as its content — so a bare one dropped into a
        // document panel collapses, and the tree inside it resolves to no height at all. The rows are
        // still there and still clickable in the tree's own terms, which is what makes the failure
        // read as "selection is broken" rather than as a layout fault.
        tabs.AddClass("document-tabs");

        // Constructed for its effect on the panel it is given, which is what the single-tab form did
        // too — the tree it builds is the tab's content and nothing here needs a handle on it.
        _ = new SceneHierarchyView(scene, tabs.AddTab("Hierarchy").Panel);

        tabs.AddTab("Compiled").Panel.Add<CompiledSceneView>().Show(scene);

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
public sealed class PrefabEditorFactory(Func<AssetEditorRequest, World> worlds) : IAssetEditorFactory {
    readonly Func<AssetEditorRequest, World> supply =
        worlds ?? throw new ArgumentNullException(nameof(worlds));

    /// <inheritdoc />
    public string Name => "Prefab";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [Prefab.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var document = new SceneDocument(
            request.Project,
            supply(request),
            request.Asset,
            System.IO.Path.GetFileNameWithoutExtension(request.Path)
        ) {
            Writer = new PrefabFileWriter(request.Path)
        };

        SceneSerializer.Load(document, request.Path);
        return document;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The banner stays outside the tabs.</b> It exists to say "this is a prefab and not a
    ///     level" at every moment, and a banner that disappeared when the author looked at the
    ///     compiled tab would be absent from exactly the pane where a prefab's own rule — one root —
    ///     is being checked.
    /// </remarks>
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var prefab = (SceneDocument) document;
        var view = panel.Add<PrefabView>();

        view.Show(prefab);

        return view;
    }
}
