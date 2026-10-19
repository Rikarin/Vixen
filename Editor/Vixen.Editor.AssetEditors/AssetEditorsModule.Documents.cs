// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Content;
using Vixen.Editor.AssetEditors.Importing;
using Vixen.Editor.AssetEditors.Materials;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.AssetEditors.Shading;
using Vixen.Editor.Assets.Content;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.SceneView;
using Vixen.Editor.ShaderGraph;
using Vixen.Editor.Ui;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors;

/// <summary>The asset editors as the module's contribution: every kind, every view, and what joins them to the host.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P3's exit, through doc 50 § D4's two registries</b> (#1297). The application
///         built this assembly's registry itself (<c>StandardEditors.CreateDefault</c>), installed its
///         stylesheet, wired the addressable analyser and the sequence's scene into two factories it
///         found by name, pointed the viewport at a frame and handed a shader graph its previews by
///         testing the opened document's type, fed the texture editor's preview by testing the view's,
///         and opened the shader graph a material links to by subscribing to the material view — ten
///         names in seven files, two of them invisible to the compiler. Each is a contribution this
///         module makes now, through <see cref="IDocumentHost" />, <see cref="IWorkspace" /> and
///         <see cref="IEditorGraphics" />, and the application references none of this assembly.
///     </para>
///     <para>
///         ⚠ <b>Everything here leaves with the module.</b> Each kind and view is handed to
///         <see cref="PluginContext.Owns{T}" /> and each subscription is undone by
///         <see cref="PluginContext.OnUnload" />, so switching the module off in the plugin manager
///         takes the twenty-six editors with it — a double-click then says no editor claims the file,
///         which is true — and leaves nothing of this assembly reachable from the editor.
///     </para>
/// </remarks>
public sealed partial class AssetEditorsModule {
    /// <summary>The Addressables panel's id, which the application's Assets menu names.</summary>
    public const string AddressablesPanel = "addressables";

    /// <summary>Registers every kind and view, and joins what the host hands out to what they need.</summary>
    /// <param name="context">The module's scope.</param>
    /// <param name="host">Where kinds go.</param>
    /// <param name="workspace">Where views go.</param>
    /// <param name="project">The project the documents open in.</param>
    void Documents(PluginContext context, IDocumentHost host, IWorkspace workspace, EditorProject project) {
        // ⚠ One world for every scene this editor opens and a fresh one per prefab — the
        // application's decision, read from the scene it publishes rather than decided here. Sharing
        // the editor's world between scenes is what makes an entity handle mean one thing across the
        // editor; a prefab must not share it, because "isolated" is the claim that its entities are
        // not in the level.
        var world = context.Services.Require<SceneDocument>().World;

        foreach (var kind in StandardEditors.Kinds(_ => world, _ => new World("Prefab"))) {
            context.Owns(host.Add(kind));

            // ⚠ A sequence drives the scene being edited, and which scene that is is the scene
            // service's answer — a kind that reached for one would be a kind that knows what the
            // editor has open.
            if (kind is SequenceEditorFactory sequences && context.Services.TryGet<ISceneEditing>(out var editing)) {
                sequences.Scene = () => editing.Active;
            }
        }

        foreach (var view in StandardEditors.Views()) {
            context.Owns(workspace.Add(view));

            // The real planner, against a workspace of its own — see `Analyse`.
            if (view is AddressableGroupEditorView groups) {
                groups.Analyser = () => Analyse(project);
            }
        }

        // ⚠ A graph opened after the renderer exists is handed it here, on the documents' own path;
        // the ones already open when it appears are handed it by `ShaderGraphPreviews`, which owns
        // the renderer (#1299) — one field, one owner, and the host hands nothing in.
        void Preview(EditorDocument opened) {
            if (opened is ShaderGraphDocument shader) {
                shader.PreviewSource = previews;
            }
        }

        host.Opened += Preview;
        context.OnUnload(() => host.Opened -= Preview);

        Joins(context, workspace);
        Addressables(context, workspace, project);
    }

    /// <summary>Joins each freshly built view to what only the host or this module can answer.</summary>
    /// <remarks>
    ///     ⚠ <b>On <see cref="IWorkspace.Shown" />, which fires on every build</b> — a panel's factory
    ///     runs again when it is reopened, so this subscribes the new view rather than a dead one. It
    ///     was <c>EditorApplication.Joined</c>, which named the material view, the texture view and
    ///     this module to do the same three things.
    /// </remarks>
    void Joins(PluginContext context, IWorkspace workspace) {
        var feed = context.Services.TryGet<IEditorGraphics>(out var graphics) ? new TexturePreviewFeed(graphics) : null;

        void Joined(UiElement view, IEditableDocument document) {
            // A material's "Open shader graph" carries an asset id and stops: opening it in a tab is
            // the workspace's, which is what the button asks.
            if (view is MaterialView material) {
                LinkGraph(material, workspace);
            }

            // ⚠ The texture editor's preview, which had no picture in it at all — `Image.Texture` is a
            // number only the host hands out, so this asks the graphics the contract lends.
            if (feed is not null && view is TextureImportView texture && document is TextureImportDocument imported) {
                feed.Follow(texture, imported);
            }

            // And the three AI editors, joined to the agent debugger's model.
            Follow(view);
        }

        workspace.Shown += Joined;

        if (graphics is not null && feed is not null) {
            graphics.SurfaceChanged += feed.Restate;
        }

        context.OnUnload(() => {
            workspace.Shown -= Joined;

            if (graphics is not null && feed is not null) {
                graphics.SurfaceChanged -= feed.Restate;
            }

            feed?.Release();
        });
    }

    /// <summary>Opens the shader graph a material names when its button asks.</summary>
    /// <remarks>
    ///     ⚠ <b>Static, and that is the whole point of it being a method.</b> A view outlives the module
    ///     — the tab stays open when the module is switched off — so a handler on it must not reach the
    ///     module. A lambda written inside <see cref="Joins" /> would share that method's closure, which
    ///     holds <c>this</c> because the join also calls <see cref="Follow" />; the weak-reference probe
    ///     in <c>AssetEditorsModuleReachTests</c> found exactly that.
    /// </remarks>
    static void LinkGraph(MaterialView material, IWorkspace workspace) =>
        material.OpenGraphRequested += (_, graph) => workspace.Open(graph);

    /// <summary>The Addressables panel: the group list, the per-group policy and doc 08's analysis.</summary>
    /// <remarks>
    ///     ⚠ <b>A panel of its own, and the view behind it already existed.</b> An asset editor is the
    ///     right home for editing <i>one</i> group's file and the wrong front door for the feature,
    ///     because a project that has never made a group has nothing to double-click. It was the
    ///     application's panel naming this assembly's view (#1297); the application's Assets menu
    ///     still names the panel by <see cref="AddressablesPanel" />.
    /// </remarks>
    static void Addressables(PluginContext context, IWorkspace workspace, EditorProject project) =>
        context.AddPanel(
            AddressablesPanel,
            EditorStrings.PanelAddressables,
            panel => {
                var view = panel.Add<AddressableGroupsView>();

                // ⚠ The same analyser the `.vxgroup` editor is given. Two panels asking two different
                // planners would be two answers to "what would a build do".
                view.Show(project, () => Analyse(project));

                // Opening a group goes through the ordinary path, so the panel that appears is the same
                // document a double-click in the browser opens.
                view.GroupOpened += (_, document) => {
                    if (document.Asset != AssetId.Empty) {
                        workspace.Open(document.Asset);
                    }
                };
            }
        );

    /// <summary>Runs the real content planner, for whoever wants to know what a build would say.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The planner, not a reimplementation of its rules.</b> A panel that worked out for
    ///         itself which assets land in which group would be a second set of rules.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Against a workspace of its own.</b> <see cref="ProjectWorkspace" /> opens the four
    ///         stores that have to agree about which directory they are looking at, and it must not
    ///         share the editor's database — <c>Scan</c> clears and repopulates its dictionaries.
    ///     </para>
    /// </remarks>
    static BuildPlan Analyse(EditorProject project) {
        var workspace = new ProjectWorkspace(project.Paths);

        workspace.Database.Scan();
        return ContentPipeline.Analyse(workspace, _ => { });
    }
}
