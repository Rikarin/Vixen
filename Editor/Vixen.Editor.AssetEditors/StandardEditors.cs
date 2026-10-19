// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Ai;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Audio;
using Vixen.Editor.AssetEditors.Code;
using Vixen.Editor.AssetEditors.Compositor;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.AssetEditors.Fonts;
using Vixen.Editor.AssetEditors.Frame;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.AssetEditors.Input;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.AssetEditors.Scenes;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.AssetEditors.Vfx;
using Vixen.Editor.Core;
using Vixen.Editor.Ui.Documents;

namespace Vixen.Editor.AssetEditors;

/// <summary>The document kinds and views this assembly ships, as two lists a host can register.</summary>
/// <remarks>
///     <para>
///         Registration stays explicit — see <see cref="DocumentHost" /> for why nothing is
///         discovered — and these are the lists, in one place. Since #1294 they are two lists
///         rather than one, because a kind (<c>Vixen.Editor.Core</c>) and a view
///         (<c>Vixen.Editor.Ui</c>) are registered with two different registries; that each kind's
///         documents have a view and each view a kind is held by <c>DocumentPairingTests</c> rather
///         than by a type.
///     </para>
///     <para>
///         ⚠ <b>The scene and the prefab kinds need a world and this cannot decide where from.</b>
///         Whether two open scenes share one world is an application's decision, so the suppliers
///         are arguments — two of them, because a prefab is edited in isolation and sharing the
///         scene's world would silently take that away.
///     </para>
/// </remarks>
public static class StandardEditors {
    /// <summary>Every kind this assembly ships.</summary>
    /// <param name="scenes">Where an opened scene's world comes from.</param>
    /// <param name="prefabs">Where an opened prefab's own world comes from.</param>
    /// <returns>The kinds, fresh.</returns>
    public static IReadOnlyList<DocumentKind> Kinds(
        Func<DocumentRequest, World> scenes,
        Func<DocumentRequest, World> prefabs
    ) {
        ArgumentNullException.ThrowIfNull(scenes);
        ArgumentNullException.ThrowIfNull(prefabs);

        return [
            .. WorldlessKinds(),
            new SceneEditorFactory(scenes),
            new PrefabEditorFactory(prefabs)
        ];
    }

    /// <summary>The kinds that need nothing from the host, for a test or a headless tool.</summary>
    /// <returns>Everything except the scene and prefab kinds, which need a world.</returns>
    public static IReadOnlyList<DocumentKind> WorldlessKinds() => [
        new TextureEditorFactory(),
        new ModelEditorFactory(),
        new MaterialEditorFactory(),
        new ShaderEditorFactory(),
        new MarkupEditorFactory(),
        new AddressableGroupEditorFactory(),
        new CompositorEditorFactory(),
        new StandardFrameEditorFactory(),
        new ShaderGraphEditorFactory(),
        new VfxEditorFactory(),
        new AnimationClipEditorFactory(),
        new AnimationGraphEditorFactory(),
        new BehaviorTreeEditorFactory(),
        new UtilitySetEditorFactory(),
        new GoapDomainEditorFactory(),
        new QueryEditorFactory(),
        new MoveSetEditorFactory(),
        new ProxyShapeEditorFactory(),
        new HarnessEditorFactory(),
        new ShapeVocabularyEditorFactory(),
        new SequenceEditorFactory(),
        new AudioMixerEditorFactory(),
        new InputActionsEditorFactory(),
        new FontEditorFactory()
    ];

    /// <summary>Every view this assembly ships, one or more per kind.</summary>
    /// <returns>The views, fresh.</returns>
    public static IReadOnlyList<DocumentView> Views() => [
        new TextureEditorView(),
        new ModelEditorView(),
        new MaterialEditorView(),
        new ShaderEditorView(),
        new MarkupEditorView(),
        new StyleSheetEditorView(),
        new AddressableGroupEditorView(),
        new CompositorEditorView(),
        new StandardFrameEditorView(),
        new ShaderGraphEditorView(),
        new VfxEditorView(),
        new AnimationClipEditorView(),
        new AnimationGraphEditorView(),
        new BehaviorTreeEditorView(),
        new UtilitySetEditorView(),
        new GoapDomainEditorView(),
        new QueryEditorView(),
        new MoveSetEditorView(),
        new ProxyShapeEditorView(),
        new HarnessEditorView(),
        new ShapeVocabularyEditorView(),
        new SequenceEditorView(),
        new AudioMixerEditorView(),
        new InputActionsEditorView(),
        new FontEditorView(),
        new SceneEditorView(),
        new PrefabEditorView()
    ];

    /// <summary>A host over a project with every kind registered.</summary>
    /// <param name="project">The project.</param>
    /// <param name="scenes">Where an opened scene's world comes from.</param>
    /// <param name="prefabs">Where an opened prefab's own world comes from.</param>
    /// <returns>The host.</returns>
    public static DocumentHost CreateDefault(
        EditorProject project,
        Func<DocumentRequest, World> scenes,
        Func<DocumentRequest, World> prefabs
    ) =>
        Fill(new DocumentHost(project), Kinds(scenes, prefabs));

    /// <summary>A host over a project with every kind that needs no world.</summary>
    /// <param name="project">The project.</param>
    /// <returns>The host.</returns>
    public static DocumentHost CreateWorldless(EditorProject project) =>
        Fill(new DocumentHost(project), WorldlessKinds());

    /// <summary>A workspace with every view registered.</summary>
    /// <returns>The workspace.</returns>
    public static EditorWorkspace CreateWorkspace() {
        var workspace = new EditorWorkspace();

        foreach (var view in Views()) {
            workspace.Add(view);
        }

        return workspace;
    }

    /// <summary>Registers every kind and view into an editor's own registries, and hands back the removals.</summary>
    /// <param name="host">Where the kinds go.</param>
    /// <param name="workspace">Where the views go.</param>
    /// <param name="scenes">Where an opened scene's world comes from.</param>
    /// <param name="prefabs">Where an opened prefab's own world comes from.</param>
    /// <returns>One removal per registration.</returns>
    public static IReadOnlyList<IDisposable> Register(
        IDocumentHost host,
        IWorkspace workspace,
        Func<DocumentRequest, World> scenes,
        Func<DocumentRequest, World> prefabs
    ) {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(workspace);

        var removals = new List<IDisposable>();

        foreach (var kind in Kinds(scenes, prefabs)) {
            removals.Add(host.Add(kind));
        }

        foreach (var view in Views()) {
            removals.Add(workspace.Add(view));
        }

        return removals;
    }

    /// <remarks>
    ///     ⚠ <b>The removals are dropped here on purpose and only here</b> (#739): this is a set
    ///     registered once into a host built for it. The case that keeps them is
    ///     <see cref="Register" />'s.
    /// </remarks>
    static DocumentHost Fill(DocumentHost host, IReadOnlyList<DocumentKind> kinds) {
        foreach (var kind in kinds) {
            host.Add(kind);
        }

        return host;
    }
}
