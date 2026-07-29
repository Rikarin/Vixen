// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Prefabs;
using Vixen.Editor.Core;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Ui;

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
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var hierarchy = new SceneHierarchyView((SceneDocument) document, panel);
        return hierarchy.Tree;
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
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<PrefabView>();
        view.Show((SceneDocument) document);

        return view;
    }
}
