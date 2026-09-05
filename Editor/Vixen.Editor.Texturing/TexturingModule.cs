// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Ui;
using Vixen.Ui;

namespace Vixen.Editor.Texturing;

/// <summary>Doc 48's texture graph, registering itself through the door a third party comes through.</summary>
/// <remarks>
///     <para>
///         <b>This is the claim doc 48 § D14 says the whole document exists to prove.</b> Four
///         batches built an evaluator, forty-five kernels and a compiler, and none of it was reachable
///         from the editor: nothing registered a document, a panel or a command. This type is that
///         spine — and it references <c>Vixen.Editor.App</c> not at all, which is the property that
///         makes "it is a plugin" a fact rather than a description.
///     </para>
///     <para>
///         ⚠ <b>What it takes from the host, it asks for.</b> The project and the contribution
///         registry, through <c>PluginServices.Require</c>. A host that has neither refuses this
///         module with a sentence naming what was missing, rather than throwing a null reference out
///         of <see cref="Activate" />.
///     </para>
///     <para>
///         ⚠ <b>Three things doc 48 predicted this plugin would need and could not have. Two are
///         closed, and closing them is what this module was for.</b>
///     </para>
///     <list type="number">
///         <item>
///             <description>
///                 <b>A graphics device — closed.</b> <c>EditorApplication.PluginPoints</c> now
///                 publishes <see cref="IEditorGraphics" />, so the preview pane evaluates a plan on
///                 the editor's own device and shows the result.
///                 <a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>. ⚠ Its "smallest
///                 honest fix is one line" was wrong: the application builds its plugin host in its
///                 constructor and acquires a device afterwards, so what a plugin can be handed is a
///                 live view rather than the device.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>A double-click — closed.</b> <c>AssetEditorRegistry.Add</c> hands back the
///                 removal now, so <see cref="TextureGraphEditorFactory" /> claims
///                 <c>.vxtexgraph</c> inside this module's registration scope and gives it back on
///                 unload. The Create ▸ entry says <c>Opens: true</c> exactly when a host published
///                 a registry to claim it in.
///                 <a href="https://github.com/Rikarin/Vixen/issues/739">#739</a>.
///             </description>
///         </item>
///         <item>
///             <description>
///                 <b>The compiler — still open.</b> <c>TextureGraphCompiler</c> is
///                 <c>internal</c>, so this plugin can offer an author every node and cannot compile
///                 what they wire; the preview is therefore the graph's base layer and says so.
///                 <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a>.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>Every registration goes through <see cref="PluginContext" />, including the
///         panel.</b> <c>TerrainModule</c> — the worked example — registers its five panels straight
///         on <c>Shell.RegisterPanel</c> with no matching <c>UnregisterPanel</c>, which
///         <c>EditorShell.UnregisterPanel</c>'s own remarks describe as "a lambda over the plugin's
///         own state that keeps its assembly loaded for the session". It survives that because a
///         built-in is never collected; a plugin loaded from a folder would not —
///         <a href="https://github.com/Rikarin/Vixen/issues/740">#740</a>.
///     </para>
/// </remarks>
public sealed class TexturingModule : IEditorPlugin, IDisposable {
    /// <summary>What the host activates it under, and what a plugin depending on it names.</summary>
    public const string ModuleId = "vixen.texturing";

    /// <summary>What a plugin-management panel calls it.</summary>
    public const string ModuleName = "Texturing";

    /// <summary>The verb that opens the selected <c>.vxtexgraph</c>.</summary>
    public const string OpenCommand = "texturing.open-graph";

    /// <summary>The panel a graph is edited in.</summary>
    public const string GraphPanel = "texturing.graph";

    EditorProject project = null!;
    EditorShell shell = null!;

    /// <summary>The host's graphics, or null in a host that publishes none.</summary>
    /// <remarks>
    ///     ⚠ <b>Optional, unlike the project and the contribution registry.</b> A module that
    ///     <c>Require</c>d this would refuse to start in a headless host — which is every test of
    ///     everything else it does — and doc 36's own rule for an extension point a plugin can do
    ///     without is <c>TryGet</c>. The pane says which of the two states it is in.
    /// </remarks>
    IEditorGraphics? graphics;

    /// <summary>What turns the open graph into pixels, once there is anything to turn it with.</summary>
    TextureGraphPreview? preview;

    /// <summary>The view, once the panel has been opened at least once.</summary>
    /// <remarks>
    ///     ⚠ <b>Null until then, and replaced every time the panel is reopened.</b> A dock panel's
    ///     factory runs again on reopen — <c>AssetEditorRegistry</c> states the same rule for a
    ///     document's view — so nothing durable may live here, and the document it shows is held by
    ///     the module rather than by the view.
    /// </remarks>
    TextureGraphView? view;

    /// <summary>The graph on the canvas, which outlives the panel showing it.</summary>
    TextureGraphDocument? document;

    /// <inheritdoc />
    public void Activate(PluginContext context) {
        ArgumentNullException.ThrowIfNull(context);

        project = context.Services.Require<EditorProject>();
        shell = context.Shell;

        // ⚠ Asked here and *read* on every show, and the difference is the finding. This used to
        // resolve to a `TexturePreviewBlocker` once, on the grounds that a host does not start
        // publishing a device halfway through a session — and the editor does exactly that: it
        // builds its `PluginHost` in its constructor and acquires a device when the window can
        // present. What is stored is the service; whether it has a device is a question with a
        // different answer at different moments and is asked each time.
        graphics = context.Services.TryGet<IEditorGraphics>(out var published) ? published : null;

        if (graphics is not null) {
            preview = new TextureGraphPreview(graphics);

            // ⚠ Through the scope rather than in `Deactivate`, because it holds device resources: an
            // evaluator's pipelines and one uploaded image. `Deactivate` runs first and this runs
            // whatever happens to it, which is the difference that matters for a throw.
            context.OnUnload(Release);
        }

        var registry = context.Services.Require<IEditorRegistry>();

        // ⚠ Registered inside the scope, which is what #739 made possible: `AssetEditorRegistry.Add`
        // hands back the removal, so the factory and every document it opened go when this module
        // does. Optional, because a host may publish no registry — and then the Create ▸ entry below
        // says `Opens: false` rather than promising a double-click nothing answers.
        var editors = context.Services.TryGet<AssetEditorRegistry>(out var found) ? found : null;

        if (editors is not null) {
            context.Owns(editors.Add(new TextureGraphEditorFactory()));
        }

        // ⚠ `Opens` is derived rather than declared. A kind that opens needs an editor claiming the
        // extension; a constant `true` here would put "No editor claims that file" on screen every
        // time somebody made one in a host with no registry, and a constant `false` would be a lie in
        // the host that has one.
        context.Owns(
            registry.Add(
                new NewAssetKind(
                    "texturing.create-texture-graph",
                    "Texture Graph",
                    TextureGraphDocument.Extension,
                    "New Texture Graph",
                    TextureGraphDocument.NewContents,
                    editors is not null
                )
            )
        );

        context.AddPanel(
            GraphPanel,
            new StringId("editor.panel.texture-graph", "Texture Graph"),
            panel => {
                view = new TextureGraphView(panel);
                Refresh();
            }
        );

        context.AddCommand(OpenCommand, new StringId("editor.command." + OpenCommand, "Open Texture Graph"), Open);

        // Where the verb belongs rather than a menu of its own — doc 36, and `PluginContext.FindMenu`
        // says why. A host with no Tools menu gets the command in the palette and the keymap, which
        // is the whole of what a menu entry adds.
        if (context.FindMenu(EditorStrings.MenuTools.Id) is { } tools) {
            context.AddMenuItem(tools, OpenCommand);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>The document goes, and it has to go here rather than in a registration.</b> It is this
    ///     module's own field and nothing the context recorded points at it — so a module that left it
    ///     would leave a live <c>EditorDocument</c> registered with the project, whose type is in the
    ///     assembly the host has just been asked to drop.
    /// </remarks>
    public void Deactivate() {
        view = null;

        if (document is { IsOpen: true }) {
            document.Close();
        }

        document = null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Not the path that runs, and it is here because the analyzer is right for the wrong
    ///     reason.</b> A module is torn down by <see cref="Deactivate" /> and by its registration
    ///     scope, and nothing in the plugin host disposes an <see cref="IEditorPlugin" /> — but this
    ///     type does own device resources, so a reader who reaches for <c>using</c> should get the
    ///     right behaviour rather than a silent leak. <see cref="Release" /> is idempotent, so the
    ///     two paths cannot free anything twice.
    /// </remarks>
    public void Dispose() => Release();

    /// <summary>Gives back everything of the host's that this module is holding.</summary>
    /// <remarks>
    ///     ⚠ <b>The uploaded image and the evaluator, and both are the editor's memory rather than
    ///     this module's.</b> A picture left behind is a texture and a descriptor set the renderer
    ///     holds for the rest of the session; an evaluator left behind is a pipeline and a shader
    ///     module per kernel it compiled. Neither shows up as a leaked registration, which is why it
    ///     is said here rather than assumed.
    /// </remarks>
    void Release() {
        preview?.Dispose();
        preview = null;

        graphics = null;
    }

    /// <summary>Re-evaluates the open graph and puts the result in the pane.</summary>
    /// <remarks>
    ///     ⚠ <b>Outside the host's own frame.</b> Every route here is a command handler or a panel
    ///     build, which run from the application's update — <c>TexturePlanEvaluator</c> drives
    ///     <c>BeginFrame</c> and <c>EndFrame</c> on the device itself, so a call from inside the
    ///     editor's frame would reset a command pool with work still executing in it.
    /// </remarks>
    void Refresh() {
        if (view is null) {
            return;
        }

        var blocker = TexturePreview.Blocking(graphics);

        view.Show(
            document,
            blocker,
            blocker == TexturePreviewBlocker.None && document is not null ? preview?.Evaluate(document) : null
        );
    }

    /// <summary>Opens the selected <c>.vxtexgraph</c> on the canvas.</summary>
    /// <remarks>
    ///     ⚠ <b>Reuses the document the project already has for that asset.</b> Two documents over one
    ///     file are two undo histories and two dirty flags, and the second save silently discards the
    ///     first — which is <c>AssetEditorRegistry.TryOpen</c>'s rule, restated because this module
    ///     cannot use that registry.
    /// </remarks>
    void Open() {
        // ⚠ `Primary` on an empty selection is `AssetId.Empty` rather than null — it is a struct — so
        // the emptiness is asked about by name. A pattern match here compiles to "always true" and
        // would send `Empty` to the database, which answers no and produces the right message for the
        // wrong reason.
        var asset = project.Selection.Primary;

        if (asset.IsEmpty
            || !project.Assets.TryGetByGuid(asset, out var entry)
            || !entry.Path.EndsWith(TextureGraphDocument.Extension, StringComparison.OrdinalIgnoreCase)) {
            shell.Notifications.Show(
                "Select a .vxtexgraph first",
                NotificationSeverity.Warning,
                "Open Texture Graph opens whatever is selected in the Project panel."
            );

            return;
        }

        if (project.TryGetDocument(asset, out var existing) && existing is TextureGraphDocument opened) {
            document = opened;
        } else {
            document = new TextureGraphDocument(project, asset, project.Paths.Absolute(entry.Path));
        }

        project.Activate(document);

        // ⚠ Opened rather than toggled. The command means "show me this graph"; a toggle would close
        // the panel for anybody who ran it while it was already open, which is every second use.
        shell.Workspace.Open(GraphPanel);
        Refresh();
    }
}
