// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.TextureGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Editor.Ui;
using Vixen.Graphics;
using Vixen.Rendering.Ecs;
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
///         ⚠ <b>Three things doc 48 predicted this plugin would need and could not have. All three
///         are closed, and closing them is what this module was for.</b>
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
///                 <b>The compiler — closed, and this entry was stale twice.</b>
///                 <c>TextureGraphCompiler</c> is <c>public</c>
///                 (<a href="https://github.com/Rikarin/Vixen/issues/738">#738</a>), and
///                 <em>both</em> panes now compile through it:
///                 <see cref="LayerStackPreview" /> the open stack, and
///                 <see cref="TextureGraphPreview" /> the open graph. The second was the
///                 finished-thing-nothing-calls this workstream keeps producing — the compiler
///                 public, the document's <c>Compile</c> written, and a pane drawing a fixed
///                 checkerboard beside them for three batches
///                 (<a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>,
///                 <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>).
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

    /// <summary>The verb that opens the selected <c>.vxlayers</c>.</summary>
    public const string OpenStackCommand = "texturing.open-stack";

    /// <summary>The panel a layer stack is shown in.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own panel rather than the graph's, and the reason is the canvas.</b>
    ///     <c>NodeGraphView</c> pans and zooms in a space of its own; a panel that swapped a node
    ///     canvas for a list of rows and back would have to reset that transform on every swap, and
    ///     the one that forgets is a canvas an author cannot find their graph on.
    /// </remarks>
    public const string StackPanel = "texturing.layers";

    /// <summary>The verb that swaps the pointer between selecting and painting.</summary>
    /// <remarks>
    ///     ⚠ <b>A verb and not only a control, because a tool mode is a thing an artist wants on a
    ///     key.</b> Registering it through <c>PluginContext.AddCommand</c> is what puts it in the
    ///     palette and the keymap as well as on the Tools menu; the segmented control in the brush
    ///     inspector is a second writer of the same state, which is why
    ///     <see cref="PaintBrushInspector.Refresh" /> exists and is called from here.
    /// </remarks>
    public const string PaintCommand = "texturing.toggle-paint";

    /// <summary>The pane a stroke is made in: doc 48 § D13's 2D UV view.</summary>
    /// <remarks>
    ///     ⚠ <b>Its own panel rather than a mode of the layers pane, and the reason is that both are
    ///     wanted at once.</b> An artist paints while reading the stack — which layer is selected,
    ///     what is over it, what the map looks like — so a pane that replaced the rows with a canvas
    ///     would make the two halves of one task exclusive. It is also the seam between two slices'
    ///     files: <see cref="LayerStackView" /> owns the rows and this owns the pointer.
    /// </remarks>
    public const string PaintPanel = "texturing.paint";

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

    /// <summary>What turns the open stack into pixels.</summary>
    /// <remarks>
    ///     ⚠ <b>A second preview and not a second evaluator, and the difference is what doc 48 § D1
    ///     claims.</b> Both compile through the same public <c>TextureGraphCompiler</c>, run the same
    ///     kernels, and now dispatch through the same <see cref="evaluator" /> — which for a batch
    ///     they did not (<a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>): each built
    ///     one of its own, so a session with both panels open compiled the whole overlap twice and
    ///     held two pipeline caches for the rest of it.
    /// </remarks>
    LayerStackPreview? stackPreview;

    /// <summary>The one evaluator both panes dispatch through.</summary>
    /// <remarks>
    ///     ⚠ <b>Built on first use rather than at activation, and released through the registration
    ///     scope rather than in <see cref="Deactivate" />.</b> The editor acquires its device after it
    ///     builds its plugin host (<a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>), so
    ///     there is nothing to build one on at activation; and <c>Deactivate</c> runs first while the
    ///     scope runs whatever happens to it, which is the difference that matters for a throw.
    /// </remarks>
    TexturePlanEvaluator? evaluator;

    /// <summary>Which device <see cref="evaluator" /> was built on.</summary>
    /// <remarks>
    ///     ⚠ <b>An evaluator is bound to its device for the life of its pipeline cache, and
    ///     <c>IEditorGraphics.Device</c> is a <em>live view</em> that can answer with a different
    ///     one</b> — <a href="https://github.com/Rikarin/Vixen/issues/945">#945</a>. See
    ///     <see cref="Evaluator" /> for the route that does it and what is done about it.
    /// </remarks>
    IGraphicsDevice? evaluatorDevice;

    /// <summary>The view, once the panel has been opened at least once.</summary>
    /// <remarks>
    ///     ⚠ <b>Null until then, and replaced every time the panel is reopened.</b> A dock panel's
    ///     factory runs again on reopen — <c>AssetEditorRegistry</c> states the same rule for a
    ///     document's view — so nothing durable may live here, and the document it shows is held by
    ///     the module rather than by the view.
    /// </remarks>
    TextureGraphView? view;

    /// <summary>The stack's view, on the same terms.</summary>
    LayerStackView? stackView;

    /// <summary>The graph on the canvas, which outlives the panel showing it.</summary>
    TextureGraphDocument? document;

    /// <summary>The stack in the panel, on the same terms.</summary>
    LayerStackDocument? stack;

    /// <summary>The brush, and whether the pointer is holding it.</summary>
    /// <remarks>
    ///     ⚠ <b>The module's and not the view's, for the reason the document is the module's.</b> A
    ///     dock panel's factory runs again every time the panel is reopened, so a brush that lived
    ///     in <see cref="LayerStackView" /> would go back to a 32-texel default every time an artist
    ///     closed the panel — which is the state they are in exactly when they have just dialled a
    ///     brush in and gone looking for something else.
    /// </remarks>
    readonly PaintTool tool = new();

    /// <summary>The <c>.vxpaint</c> canvases this session has open.</summary>
    /// <remarks>
    ///     ⚠ <b>The module's, and that placement is the whole of
    ///     <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a> and
    ///     <a href="https://github.com/Rikarin/Vixen/issues/948">#948</a> rather than a convenience.</b>
    ///     Three things read a paint layer's canvas — <see cref="BeginStroke" /> at pointer-down,
    ///     <see cref="RefreshPaint" /> at pointer-up, and the layers pane on the way to the map — and
    ///     each of them opened the file. A cache owned by any one of them would serve the other two a
    ///     picture from before the stroke, because a session writes texels in memory and does not
    ///     touch the file until it saves. Here, all three hold the same object.
    /// </remarks>
    readonly PaintCanvasStore canvases = new();

    /// <summary>The 2D UV pane, once the panel has been opened at least once.</summary>
    PaintUvView? paintView;

    /// <summary>The paint layer the last drag opened, and the canvas behind it.</summary>
    /// <remarks>
    ///     ⚠ <b>Re-opened at every pointer-down rather than held across drags.</b> The layer, the
    ///     canvas on disk and the stack's resolution are all things that change with the pointer up
    ///     — an undo of a layer edit, a re-import, another panel — and a surface captured once would
    ///     paint into whichever state the first stroke found.
    /// </remarks>
    PaintSurface? surface;

    /// <summary>The geometry the open stack is painted on, resolved — or null with a reason.</summary>
    /// <remarks>
    ///     ⚠ <b>Held, because resolving it reads a model file off the disk and parses it.</b> The two
    ///     callers are a panel refresh and a pointer-down, and re-reading a 25 000-triangle OBJ on
    ///     every stroke would put the mesh's triangle count into the per-stamp path — which is the
    ///     one property doc 48's exit criterion 8 is about. <see cref="meshKey" /> is what decides
    ///     that the answer is still the answer.
    /// </remarks>
    LayerStackMesh? mesh;

    /// <summary>Where an imported mesh chunk is read from, or null in a host that publishes none.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is a real state and not a missing dependency</b>, exactly as the graphics beside
    ///     it is: a headless test host publishes no <see cref="IMeshSource" />, and a module that
    ///     <c>Require</c>d one would refuse to start there. What it costs is the fallback —
    ///     <c>LayerStackMesh.Open</c> reads the model file itself and says so.
    /// </remarks>
    IMeshSource? geometry;

    /// <summary>What <see cref="mesh" /> was resolved for: the stack, its model and the set's mesh.</summary>
    /// <remarks>
    ///     ⚠ <b>Null is "not asked yet" and it is not a spare value.</b> A key is a real string for
    ///     every state including the empty one — a stack that names no model has a key, and its
    ///     answer is a refusal worth keeping rather than re-deriving. A sentinel string would have to
    ///     be a string no path can produce, which is how a raw NUL gets into a source file and makes
    ///     it invisible to <c>grep</c>; nullable says the same thing in the type.
    /// </remarks>
    string? meshKey;

    /// <summary>Why there is no mesh, or empty.</summary>
    string meshRefusal = "";

    /// <summary>What the islands on the paint pane were drawn for, so they are not redrawn per stroke.</summary>
    /// <remarks>
    ///     ⚠ <b><c>PaintUvView.ShowIslands</c> rebuilds the whole overlay, three segments per
    ///     triangle.</b> <see cref="RefreshPaint" /> runs at every pointer-up, and re-adding 75 000
    ///     segments after each stroke is a cost that grows with the model and buys nothing: the
    ///     islands change when the binding or the atlas does, and at no other moment.
    /// </remarks>
    string? islandsKey;

    /// <summary>What the paint pane is showing, so it can be given back.</summary>
    IEditorImage? painted;

    /// <summary>One dirtied rectangle's own rows, for the partial upload.</summary>
    /// <remarks>
    ///     ⚠ <b>Grown and reused rather than allocated per redraw.</b> A redraw is every pointer move
    ///     of a drag, and the whole point of
    ///     <a href="https://github.com/Rikarin/Vixen/issues/912">#912</a> is that a move stops costing
    ///     the atlas — an array per move would put a megabyte of garbage back in its place.
    /// </remarks>
    byte[] patch = [];

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

        // ⚠ The host's mesh source, and it is what makes a stack's binding read what the *project*
        // has rather than what the file carries — #934. `EditorApplication` publishes its
        // `ProjectMeshSource` under this contract, which reads the chunks the last import wrote; a
        // host that publishes none leaves `LayerStackMesh.Open` on its source-file path, which is
        // where every one of these resolves used to be. Optional for that reason and asked for once,
        // unlike the graphics above: the answer is a store on disk and does not acquire itself
        // halfway through a session.
        geometry = context.Services.TryGet<IMeshSource>(out var meshes) ? meshes : null;

        if (graphics is not null) {
            preview = new TextureGraphPreview(graphics, Evaluator, canvases);
            stackPreview = new LayerStackPreview(graphics, Evaluator, canvases);

            // ⚠ Through the scope rather than in `Deactivate`, because it holds device resources: an
            // evaluator's pipelines and one uploaded image. `Deactivate` runs first and this runs
            // whatever happens to it, which is the difference that matters for a throw.
            context.OnUnload(Release);

            // ⚠ And the other half of the same promise, which #968 is what makes expressible. Unload
            // is not the only way this module stops owning device objects: the window can go and take
            // the device with it, and until the contract carried that this module could only *notice*
            // — see `Evaluator`, whose stale branch drops an evaluator it cannot legally dispose,
            // because by the time a live view starts answering differently the old device has already
            // been destroyed. This runs while it is still valid, so the pipelines go back.
            context.OnDeviceLost(ReleaseDevice);
        }

        var registry = context.Services.Require<IEditorRegistry>();

        // ⚠ Registered inside the scope, which is what #739 made possible: `AssetEditorRegistry.Add`
        // hands back the removal, so the factory and every document it opened go when this module
        // does. Optional, because a host may publish no registry — and then the Create ▸ entry below
        // says `Opens: false` rather than promising a double-click nothing answers.
        var editors = context.Services.TryGet<AssetEditorRegistry>(out var found) ? found : null;

        if (editors is not null) {
            context.Owns(editors.Add(new TextureGraphEditorFactory()));
            context.Owns(editors.Add(new LayerStackEditorFactory()));
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

        // ⚠ The second kind, and registering it is what turned `TexturingClaimTests`' `Assert.Single`
        // red — deliberately, and that tripwire firing is the good version of #806. A kind added
        // where nothing counted them would have been a kind nobody noticed; the assertion now names
        // both extensions, so a *third* still has to be argued for.
        context.Owns(
            registry.Add(
                new NewAssetKind(
                    "texturing.create-layer-stack",
                    "Layer Stack",
                    LayerStackDocument.Extension,
                    "New Layer Stack",
                    LayerStackDocument.NewContents,
                    editors is not null
                )
            )
        );

        context.AddPanel(
            GraphPanel,
            new StringId("editor.panel.texture-graph", "Texture Graph"),
            panel => {
                view = new TextureGraphView(panel);

                // ⚠ The graph pane's half of #819, which was worth nothing until #792. A canvas edit
                // now changes the map, and without this line it changed the map only the next time
                // the panel was built or `Open Texture Graph` was run.
                view.Edited = Refresh;

                Refresh();
            }
        );

        context.AddPanel(
            StackPanel,
            new StringId("editor.panel.layer-stack", "Layer Stack"),
            panel => {
                // ⚠ The previous one is ended first, and this factory really does re-run: opening
                // any other panel relays the workspace out. `LayerStackView` follows the open
                // document's `CommandStack.Depth` (#933), and that edge outlives the elements — so
                // a view that was not ended goes on refreshing from a stack it no longer draws.
                stackView?.Dispose();
                stackView = new LayerStackView(panel, tool);

                // ⚠ The one line that makes the panel's edits reach the picture. `LayerStackView`
                // holds no evaluator — two of them over one device would be two pipeline caches,
                // which is `stackPreview`'s own stated reason — so an edit made in a row can redraw
                // the rows and cannot redraw the map. #819.
                stackView.Edited = RefreshStack;

                // ⚠ And the paint pane, which is the other half of #910. A row click writes
                // `PaintTool.LayerId`, and the pane reads it once — at its own refresh — so without
                // this line the artist selects a layer and the pane goes on showing the pixels of
                // whichever one the brush found first, until something else happens to refresh it.
                stackView.SelectionChanged = RefreshPaint;

                RefreshStack();
            }
        );

        context.AddPanel(
            PaintPanel,
            new StringId("editor.panel.texture-paint", "Paint"),
            panel => {
                paintView = new PaintUvView(panel, tool) {
                    Target = BeginStroke,
                    Painted = Redraw,
                    Reverted = Persist,
                    Finished = Recorded
                };

                // ⚠ The overlay belongs to the view and the key that says what is on it belongs to
                // the module, so reopening the panel resets one and not the other. Without this the
                // reopened pane keeps its key, agrees that the islands are already drawn, and shows
                // an atlas with nothing on it — the exact state #920 is about, reintroduced by a
                // cache.
                islandsKey = null;

                RefreshPaint();
            }
        );

        context.AddCommand(OpenCommand, new StringId("editor.command." + OpenCommand, "Open Texture Graph"), Open);
        context.AddCommand(
            OpenStackCommand,
            new StringId("editor.command." + OpenStackCommand, "Open Layer Stack"),
            OpenStack
        );

        context.AddCommand(
            PaintCommand,
            new StringId("editor.command." + PaintCommand, "Paint on Layer"),
            TogglePaint
        );

        // Where the verb belongs rather than a menu of its own — doc 36, and `PluginContext.FindMenu`
        // says why. A host with no Tools menu gets the command in the palette and the keymap, which
        // is the whole of what a menu entry adds.
        if (context.FindMenu(EditorStrings.MenuTools.Id) is { } tools) {
            context.AddMenuItem(tools, OpenCommand);
            context.AddMenuItem(tools, OpenStackCommand);
            context.AddMenuItem(tools, PaintCommand);
        }
    }

    /// <summary>Swaps the pointer between selecting and painting.</summary>
    /// <remarks>
    ///     ⚠ <b>It opens the pane the brush works in, and until <see cref="PaintUvView" /> existed
    ///     there was none.</b> This verb used to say, in the notification, that no viewport drove the
    ///     brush and that a drag would paint nothing. The 2D UV view is doc 48 § D13's second front
    ///     end and is that viewport; the 3D projection path is still owed
    ///     (<a href="https://github.com/Rikarin/Vixen/issues/574">#574</a>), and a mode with no pane
    ///     open would still be a mode nothing can be done in — so switching it on shows the pane.
    /// </remarks>
    void TogglePaint() {
        var mode = tool.Toggle();

        // ⚠ Pulled rather than pushed. The segmented control and this verb are two writers of one
        // model; without this the control keeps showing what was last clicked, which is the state
        // the panel is in exactly when somebody used the shortcut instead.
        stackView?.Brush?.Refresh();

        if (mode == PaintToolMode.Paint) {
            // Opened rather than toggled, for `Open`'s reason: the verb means "let me paint", and a
            // toggle would close the pane for anybody who ran it while it was already open.
            shell.Workspace.Open(PaintPanel);
            RefreshPaint();
        }

        shell.Notifications.Show(
            mode == PaintToolMode.Paint ? "Painting" : "Not painting",
            NotificationSeverity.Info,
            // ⚠ It names the layer the brush is really aimed at rather than "the first paint layer",
            // which is what it said and is no longer true — a row's Select button writes
            // `PaintTool.LayerId` (#910). It also names the *set*, because there is still no way to
            // choose one and every path here takes `Sets[0]` — #927 asks for exactly this sentence
            // where a selector is not built.
            mode == PaintToolMode.Paint
                ? "The brush is " + tool.Describe() + ". Drag in the Paint pane to lay a stroke into "
                + Aimed()
                + ". ⚠ The 3D projection path is still doc 48 § D13 (#574), so a drag in the scene "
                + "paints nothing."
                : "A drag selects rows and pans the preview."
        );
    }

    /// <summary>Which set and which layer a drag would reach, as a person reads it.</summary>
    /// <remarks>
    ///     ⚠ <b>The set is named even though it cannot be chosen, and that is the point of naming
    ///     it.</b> <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>: every path in this
    ///     plugin takes <c>Sets[0]</c> and the messages read as though one had been picked, so a
    ///     multi-set stack paints into the first one and says nothing about it. Saying which is the
    ///     honest half of the fix; the selector is the other and is not built.
    /// </remarks>
    string Aimed() {
        var set = stack?.Document.Sets is { Count: > 0 } sets ? $"set '{sets[0].Name}'" : "this stack";

        return tool.LayerId.Length > 0
            ? $"the layer '{tool.LayerId}' of {set}"
            : $"the first paint layer of {set} — no layer is selected, so the brush takes the first one";
    }

    /// <summary>Pointer-down in the paint pane: what to paint into, or nothing with a reason.</summary>
    /// <remarks>
    ///     ⚠ <b>Every refusal is a sentence under the pane rather than an exception</b>, for
    ///     <c>LayerStackPreview.Evaluate</c>'s reason: this runs from a pointer event, and a throw
    ///     out of one takes the editor's frame with it.
    /// </remarks>
    PaintTarget? BeginStroke() {
        if (paintView is null) {
            return null;
        }

        if (stack is null) {
            paintView.Say("No stack is open. Select a .vxlayers and run Open Layer Stack.");

            return null;
        }

        surface = PaintSurface.Open(stack, tool.LayerId, canvases, out var refusal);

        if (surface is null) {
            paintView.Say(refusal);

            return null;
        }

        // ⚠ The coverage is passed *in* rather than rewritten on the record afterwards — #942. The
        // reason it is the module's at all is which type knows what: a surface holds a canvas and a
        // layer, and the mesh is this module's because resolving one reads a model file whose answer
        // is cached across strokes. For a batch this read `surface.Target(...) with { Coverage = … }`,
        // which left `Target`'s own remarks claiming a coverage of `Everywhere` that no stroke ever
        // got. #920's dilation is unexercisable until somebody hands a real raster in, and this is
        // the somebody.
        return surface.Target(tool.Channel, Mesh()?.Coverage(surface.Canvas.Width, surface.Canvas.Height));
    }

    /// <summary>The geometry the open stack is painted on, resolved once per binding.</summary>
    /// <remarks>
    ///     ⚠ <b>The set is <c>Sets[0]</c>, which is the same pin every other path here has and is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a> rather than a decision.</b>
    ///     <c>PaintSurface.Open</c> takes the first set, <c>LayerStackView</c> draws the first set and
    ///     <c>LayerStackPreview</c> compiles the first set; a mesh resolved for a different one would
    ///     be the only thing in the plugin that disagreed.
    /// </remarks>
    LayerStackMesh? Mesh() {
        if (stack is null) {
            mesh = null;
            meshKey = null;
            meshRefusal = "";

            return null;
        }

        var asset = stack.Document;
        var set = asset.Sets.Count > 0 ? asset.Sets[0] : null;
        var key = stack.AssetPath + "\n" + asset.Model + "\n" + (set?.Mesh ?? "");

        if (!string.Equals(key, meshKey, StringComparison.Ordinal)) {
            meshKey = key;
            mesh = LayerStackMesh.Open(project, asset, set, geometry, out meshRefusal);
        }

        return mesh;
    }

    /// <summary>A move, an undo or a redo dirtied a rectangle: put the composite back on the screen.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The rectangle is what is uploaded now, and the whole picture is the fallback</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/912">#912</a>. This used to hand the
    ///         atlas back on every pointer move because <c>IEditorGraphics</c> had no sub-rectangle
    ///         form: at 4K a stamp that touched a 96-texel disc moved 67 MB, made a texture and wrote
    ///         a descriptor set, per frame of the drag. That is exactly the cost
    ///         <c>PaintComposite.Resolve</c>'s rectangles were bought to avoid, and it was being paid
    ///         one level up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The fallback is not decoration.</b> <c>Update</c> refuses an image made before the
    ///         atlas changed size, and it refuses everything on a host with no surface — and a caller
    ///         that treated a refusal as done would leave the pane showing the picture from before the
    ///         stroke, which looks precisely like a brush that does not paint.
    ///     </para>
    /// </remarks>
    void Redraw(PaintRect rect) {
        if (paintView?.Live is not { } composite) {
            return;
        }

        var image = composite.Result;
        var clipped = rect.Clip(image.Width, image.Height);

        if (clipped.IsEmpty) {
            return;
        }

        if (Patch(image, clipped)) {
            // Only the sentence, because the picture is the same handle with new texels in it.
            paintView.Say("Painting: " + tool.Describe());

            return;
        }

        Show(image, "Painting: " + tool.Describe());
    }

    /// <summary>Copies one rectangle's rows out of the composite and into the live image.</summary>
    /// <returns>Whether the host took it.</returns>
    /// <remarks>
    ///     ⚠ <b>The extent is checked against the <em>image</em> rather than trusted from the
    ///     composite.</b> The two disagree for one redraw whenever the atlas resolution changes under
    ///     an open pane — a stack edited to a different base size — and a patch against the old handle
    ///     would be refused by the host anyway; checking here is what makes the fallback take over
    ///     rather than the pane going quietly stale.
    /// </remarks>
    bool Patch(PaintImage image, PaintRect rect) {
        if (graphics is null || painted is not { } live || live.Width != image.Width || live.Height != image.Height) {
            return false;
        }

        var stride = rect.Width * PaintImage.BytesPerTexel;
        var bytes = stride * rect.Height;

        if (patch.Length < bytes) {
            patch = new byte[bytes];
        }

        for (var row = 0; row < rect.Height; row++) {
            var from = (((rect.Y + row) * image.Width) + rect.X) * PaintImage.BytesPerTexel;

            Array.Copy(image.Texels, from, patch, row * stride, stride);
        }

        return graphics.Update(live, rect.X, rect.Y, rect.Width, rect.Height, patch.AsSpan(0, bytes));
    }

    /// <summary>An undo or a redo moved texels, so the canvas goes back to disk and the map redraws.</summary>
    /// <remarks>
    ///     ⚠ <b>The <c>RefreshStack</c> is what this is for, and the save beside it is no longer the
    ///     reason</b> — <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>. Undoing a
    ///     stroke mends the <c>PaintImage</c> in memory, and until <see cref="PaintCanvasStore" /> the
    ///     layers pane resolved a paint layer by opening the <c>.vxpaint</c> off the disk — so
    ///     without the write the pane went on showing the stroke the artist had just taken back. The
    ///     pane now reads the same canvas the undo mended, so the redraw alone would do it; the save
    ///     stays because an undone stroke that is only in memory is one a crash brings back.
    ///     <para>
    ///         The save is the same one <see cref="Recorded" /> does and is idempotent, so an undo
    ///         immediately after a stroke writes the same bytes twice rather than doing something
    ///         different the second time.
    ///     </para>
    /// </remarks>
    void Persist() {
        surface?.Save();

        RefreshStack();
    }

    /// <summary>Pointer-up: the canvas goes to disk, the drag goes on the undo stack, the map redraws.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The naming edit is executed <em>before</em> the stroke and that ordering is the
    ///         whole of it.</b> A paint layer that named no canvas gets one written down here, and it
    ///         is a change to the <c>.vxlayers</c> rather than to the pixels — so it is its own entry.
    ///         Pushed after the stroke, the artist's first undo would take the name away and leave a
    ///         layer pointing at nothing with the stroke still in it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The file is still written first, and the reason it had to be is gone</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/885">#885</a>. This said that
    ///         <c>LayerStackPreview</c> resolves a paint layer by opening the <c>.vxpaint</c> off the
    ///         disk, so a stroke only in memory was a stroke the map could not show. Both panes now
    ///         read the session's own <see cref="PaintCanvasStore" />, which is why the map redraws
    ///         mid-drag. The save is what makes the stroke outlast the session, which is why it is
    ///         still here and why its position no longer matters.
    ///     </para>
    /// </remarks>
    void Recorded(IEditorCommand command) {
        if (stack is null || surface is null) {
            return;
        }

        surface.Save();

        if (surface.NeedsNaming) {
            stack.Stack.Execute(
                new SetLayerCommand(
                    stack,
                    new LayerPath(surface.Set.Name, surface.Layer.Id),
                    surface.Layer,
                    surface.Named(),
                    "Name paint canvas"
                )
            );
        }

        stack.Stack.Execute(command);

        // ⚠ Both, because they answer to different things. `RefreshStack` rebuilds the map from the
        // stack, and it refreshes the paint pane only when the *binding* moved — the model, the
        // mesh, the layer or the channel — since it also runs once per frame of an opacity drag. A
        // stroke moves none of those and changes the pixels, so the pane is asked directly here.
        RefreshStack();
        RefreshPaint();
    }

    /// <summary>What the paint pane was last built for: the model, the mesh, the layer and the channel.</summary>
    /// <remarks>
    ///     ⚠ <b>A layer-stack edit refreshes that pane only when one of these four moved.</b>
    ///     <c>RefreshStack</c> runs from <c>LayerStackView.Edited</c>, which an opacity slider raises
    ///     once per frame — and rebuilding the pane uploads a whole channel, which at 4K is 67 MB per
    ///     frame of the drag. Nothing else an artist can change in those rows alters what that pane
    ///     shows. ⚠ The <em>read</em> half of that cost is gone —
    ///     <see cref="PaintCanvasStore" /> answers from memory — and the upload half is not, so this
    ///     comparison still earns its place.
    /// </remarks>
    (string Model, string Mesh, string Layer, string Channel) paintBinding;

    /// <summary>Puts the painted layer's own pixels in the paint pane.</summary>
    /// <remarks>
    ///     ⚠ <b>The layer and not the stack, and that is a smaller promise than doc 48 § D13's.</b>
    ///     What a live composite would show is the layer between the stack's two halves; those halves
    ///     have to come out of the plan, which is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/849">#849</a> and is not built. With
    ///     <see cref="PaintStackImages.Empty" /> under and over it, the composite of the layer <em>is
    ///     the layer</em> — so this pane and the drag agree exactly, and both of them differ from the
    ///     map in the layers pane by whatever the stack does. Saying which of the two an artist is
    ///     looking at is what the sentence under the pane is for.
    /// </remarks>
    void RefreshPaint() {
        if (paintView is null) {
            return;
        }

        if (stack is null) {
            paintView.Show(0, 1, 1, "No stack is open. Select a .vxlayers and run Open Layer Stack.");
            Islands(Mesh());

            return;
        }

        var opened = PaintSurface.Open(stack, tool.LayerId, canvases, out var refusal);

        if (opened is null) {
            paintView.Show(0, stack.Document.BaseWidth, stack.Document.BaseHeight, refusal);

            // ⚠ The islands still go up. A stack with no paint layer in it is one an artist is about
            // to add a paint layer to, and the outlines are what tells them the mesh binding worked —
            // there is nothing about a missing layer that makes the geometry unknown.
            Islands(Mesh());

            return;
        }

        var bound = Mesh();

        Show(
            opened.Canvas.Channel(tool.Channel),
            $"'{opened.Set.Name}' · '{opened.Layer.Name}' · {tool.Channel} · this layer's own pixels, not "
            + "the stack's composite (#849)."
            + " " + (bound is null ? meshRefusal : $"Painting on '{bound.Model}' — {bound.Triangles} triangles.")
        );

        // ⚠ After `Show`, because `ShowIslands` puts the outlines in texels of the extent `Show`
        // just set. Before it, every segment would be scaled by the atlas the pane was showing
        // last, which for the first refresh of a session is 1×1.
        Islands(bound);
    }

    /// <summary>Draws the bound mesh's UV islands under the brush, or takes the last ones away.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><c>PaintUvView.ShowIslands</c>' first production caller</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/928">#928</a> names it among five
    ///         members that had a declaration and no use. It could not have one before
    ///         <a href="https://github.com/Rikarin/Vixen/issues/920">#920</a>: the pane is handed an
    ///         atlas and the islands are a property of a mesh, and no <c>.vxlayers</c> named one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An unbound stack is drawn with <em>no</em> islands rather than left alone.</b>
    ///         Unbinding a model, or opening a second stack that names none, would otherwise leave
    ///         the previous mesh's outlines over an atlas they describe nothing about — which is a
    ///         worse picture than an empty one, because it looks like information.
    ///     </para>
    /// </remarks>
    void Islands(LayerStackMesh? bound) {
        if (paintView is null) {
            return;
        }

        var key = (bound?.Model ?? "") + "\n" + (bound?.Mesh ?? "") + "\n" + (bound?.Triangles ?? 0)
            + "\n" + paintView.Image.ImageWidth + "×" + paintView.Image.ImageHeight;

        if (string.Equals(key, islandsKey, StringComparison.Ordinal)) {
            return;
        }

        islandsKey = key;
        paintView.ShowIslands(bound?.Coordinates ?? []);
    }

    /// <summary>Uploads a paint image and hands it to the pane.</summary>
    void Show(PaintImage image, string status) {
        if (paintView is null) {
            return;
        }

        var uploaded = graphics?.Upload(image.Width, image.Height, image.Texels);

        // One live upload per redraw: a pane re-uploaded on every pointer move would otherwise hold
        // a texture and a descriptor set per frame of the drag. `LayerStackPreview` says the same.
        painted?.Dispose();
        painted = uploaded;

        paintView.Show(uploaded?.Image ?? 0ul, image.Width, image.Height, status);
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

        // ⚠ Ended and not only dropped. `LayerStackView` subscribes to the document's command stack
        // so it can redraw on an undo taken elsewhere, and that subscription outlives the elements —
        // so a view merely nulled here goes on refreshing from a stack it no longer draws. The panel
        // factory learned this; this path is the other way a view is let go.
        stackView?.Dispose();

        stackView = null;
        paintView = null;

        // ⚠ The surface and not only the pane. It holds a `PaintCanvas`, which at 4K is 67 MB a
        // channel — a module that let the pane go and kept the canvas would leave the largest thing
        // it ever allocated alive for the session.
        surface = null;

        // ⚠ And every *other* open canvas, for the same reason one size larger — #948. The store
        // holds the largest allocations this plugin makes and its budget is a ceiling rather than a
        // schedule, so nothing in it goes on its own; a module deactivated with a stack open would
        // otherwise keep that stack's paintings for the life of the process.
        canvases.Clear();

        // ⚠ And the resolved mesh, for the same reason one size larger: it holds three `Vector2`
        // per triangle of the model plus a `bool` per texel of the atlas, which for a hero asset at
        // 4K is tens of megabytes that outlive every panel that could have used them.
        mesh = null;
        meshKey = null;
        islandsKey = null;

        if (document is { IsOpen: true }) {
            document.Close();
        }

        if (stack is { IsOpen: true }) {
            stack.Close();
        }

        document = null;
        stack = null;
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

        stackPreview?.Dispose();
        stackPreview = null;

        // ⚠ Here and in neither preview — #820. The panes borrow it; freeing it from one of them
        // would destroy the pipelines the other is still dispatching through, which on a device is a
        // use-after-free rather than a slow first open. `EvaluatorsBuilt` is deliberately not reset:
        // it counts what this module built over its life.
        evaluator?.Dispose();
        evaluator = null;

        // ⚠ And the device it was built on, or the module holds the last one it saw for the rest of
        // the process. That is a reference to an `IGraphicsDevice` this module does not own, kept
        // past the point where it gave everything else back — and it would compare equal to a device
        // the host happened to hand over again, which is #945 with the fix in place.
        evaluatorDevice = null;

        // The paint pane's own upload: it is made here rather than by a preview, so nothing else
        // would ever give it back.
        painted?.Dispose();
        painted = null;

        graphics = null;
    }

    /// <summary>Gives back what was built on a device that is going, while it is still valid.</summary>
    /// <param name="device">The device the host is about to stop answering with.</param>
    /// <remarks>
    ///     <para>
    ///         <b>The release <see cref="Evaluator" />'s stale branch could not do</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/968">#968</a>. That branch meets a
    ///         device that has <em>already</em> been destroyed and can only drop the evaluator;
    ///         <c>PluginContext.OnDeviceLost</c> is raised before the host stops answering with it, so
    ///         here <c>WaitIdle</c> and <c>Destroy</c> are both legal and the pipelines, shader
    ///         modules and <c>EffectLoader</c> go back to the device that owns them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the evaluator, and not <see cref="Release" />'s whole list.</b> The module
    ///         survives a device loss — the window comes back, <c>EnsureDevice</c> builds another and
    ///         the panes ask again — so clearing <c>graphics</c> here would leave it holding no
    ///         service at all for the rest of the session. The uploaded pictures are the host's
    ///         thumbnail surface's, which the host has already taken down by this point.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Guarded on the identity, so a device this module never built on is a no-op.</b>
    ///         Disposing an evaluator through a device that is not the one its pipelines belong to is
    ///         the use-after-free the guard in <see cref="Evaluator" /> exists to avoid, reached from
    ///         the other side.
    ///     </para>
    /// </remarks>
    void ReleaseDevice(IGraphicsDevice device) {
        if (evaluator is null || !ReferenceEquals(evaluatorDevice, device)) {
            return;
        }

        evaluator.Dispose();
        evaluator = null;
        evaluatorDevice = null;
    }

    /// <summary>How many evaluators this module has built over its life.</summary>
    /// <remarks>
    ///     ⚠ <b>A count rather than a flag, because the defect it measures is a <em>second</em>
    ///     one</b> — <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>. Nothing reports
    ///     two: both panes draw correctly, and what it costs is a Raven parse, a shader module, a
    ///     compute pipeline and a duplicate descriptor-set-layout cache entry per kernel and format
    ///     the two panes share, held for the session.
    /// </remarks>
    internal int EvaluatorsBuilt { get; private set; }

    /// <summary>How many kernel variants this module's evaluator has compiled.</summary>
    /// <remarks>
    ///     ⚠ <b>The cost <see cref="EvaluatorsBuilt" /> only counts the cause of</b>, and the counter
    ///     <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a> names: a variant is a Raven
    ///     parse and bind, a shader module and a compute pipeline, cached per kernel and output
    ///     format. Two evaluators over one device each compile the kernels the two panes share; one
    ///     compiles each once, and this is the difference said as a number.
    /// </remarks>
    internal int KernelCompilations => evaluator?.Compilations ?? 0;

    /// <summary>How many <c>.vxpaint</c> files this session has read off the disk.</summary>
    /// <remarks>
    ///     ⚠ <b>The number <a href="https://github.com/Rikarin/Vixen/issues/948">#948</a> is, and a
    ///     counter rather than a clock for the reason <see cref="EvaluatorsBuilt" /> is one.</b> A
    ///     stroke that reads its canvas three times and a stroke that reads it once produce the same
    ///     picture, the same undo entry and the same file — so nothing about the result can tell them
    ///     apart, and at 4K the difference is 134 MB of read and of allocation per stroke.
    /// </remarks>
    internal int CanvasReads => canvases.Reads;

    /// <summary>How many times an already-open canvas answered instead of the disk.</summary>
    /// <remarks>
    ///     Beside <see cref="CanvasReads" /> because one read is also what a session that only ever
    ///     asked once reports, and the two together say which of the two happened.
    /// </remarks>
    internal int CanvasHits => canvases.Hits;

    /// <summary>How many times anything in this session asked the store for pixels at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The only one of the three that a call site going back to <c>File.OpenRead</c> moves
    ///     in a direction an assertion can catch</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/978">#978</a>. Un-wiring a reader lowers
    ///     <see cref="CanvasReads" />, which a suite wanting it at zero reads as success, and lowers
    ///     <see cref="CanvasHits" />, which a threshold reads as success as soon as the remaining
    ///     readers clear it. An exact expectation over a scripted drag goes red on any of them.
    /// </remarks>
    internal int CanvasOpens => canvases.Opens;

    /// <summary>Hands a pane the one evaluator for the device it found, building it on demand.</summary>
    /// <param name="device">The device the pane found on the host.</param>
    /// <returns>The evaluator, which the caller does not own.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One per <em>device</em> rather than one per module, and for a batch it was the
    ///         latter</b> — <a href="https://github.com/Rikarin/Vixen/issues/945">#945</a>. An
    ///         evaluator caches a compiled pipeline and a shader module per kernel and output format,
    ///         and an <c>EffectLoader</c>, all built on the device it was constructed with; there is
    ///         no route by which any of that is replayed onto another. So a module that returned its
    ///         first evaluator whatever it was asked handed a pane pipelines belonging to a device
    ///         that is gone, and what a dispatch does through those is a crash somewhere else
    ///         entirely.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That the device really can change inside one session was checked rather than
    ///         assumed, because the issue's own first question was whether it can.</b>
    ///         <c>EditorHost</c> answers <c>PlatformEventKind.Suspending</c> with <c>Release</c>,
    ///         which sets <c>EditorApplication.GraphicsDevice</c> to null and disposes the
    ///         <c>VulkanDevice</c>; the next <c>Present</c> calls <c>EnsureDevice</c>, which sees a
    ///         null device and a surface that can present and creates a <em>new</em> one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The plugin used to be told nothing at any point, and now it is</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/968">#968</a>. <c>OnDeviceLost</c> is
    ///         raised before the host stops answering with the old device, so
    ///         <see cref="ReleaseDevice" /> gives its pipelines back and this branch is no longer the
    ///         route a lost device normally takes. What it still is, is the backstop for a host that
    ///         raises nothing — a test that writes <c>GraphicsDevice</c> straight through, or a
    ///         future host with a different order — and for that reason it keeps its old behaviour.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A stale evaluator reaching <em>here</em> is dropped and <em>not</em> disposed,
    ///         which is the opposite of what this type does everywhere else.</b>
    ///         <c>TexturePlanEvaluator.Dispose</c> calls <c>WaitIdle</c> and <c>Destroy</c> on the
    ///         device it holds — and a device that got this far unannounced was disposed before the
    ///         replacement existed. Destroying a pipeline through a destroyed device is the crash this
    ///         is avoiding rather than a tidier version of it. Nothing outlives the device: a Vulkan
    ///         device's objects go when it does, and what stays behind is a managed wrapper holding
    ///         invalid handles that the next collection takes.
    ///     </para>
    /// </remarks>
    TexturePlanEvaluator Evaluator(IGraphicsDevice device) {
        if (evaluator is not null && ReferenceEquals(evaluatorDevice, device)) {
            return evaluator;
        }

        EvaluatorsBuilt++;
        evaluatorDevice = device;

        return evaluator = new TexturePlanEvaluator(device);
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

        if (document is null) {
            view.Show(null, TexturePreview.Blocking(graphics));

            return;
        }

        // ⚠ The blocker is no longer asked first, and that is the change #816 is. `Evaluate` compiles
        // before it looks for a device — a graph that does not compile does not compile on any host —
        // so a pane that gated the whole call on `Blocking` answered every mistake in an author's
        // graph with a message about the window not being up yet. `LayerStackPreview` was moved off
        // that order first, and the fallback below is the state where there is no preview at all,
        // which is a host publishing no graphics rather than the editor.
        // ⚠ Asked before the picture is made, and handed across. Producing the picture compiles,
        // and compiling republishes — so a `Show(document, Evaluate(document))` written the obvious
        // way consumes the stale flag in its own argument list and leaves the view's re-seat of an
        // author who is inside a republished compound unreachable.
        view.Republished = document.Republish();

        view.Show(
            document,
            preview?.Evaluate(document)
            ?? new TextureGraphPicture(null, TexturePreview.Describe(TexturePreview.Blocking(graphics)))
        );
    }

    /// <summary>Re-compiles the open stack and puts the map it produces in the pane.</summary>
    /// <remarks>
    ///     ⚠ <b>The one difference from <see cref="Refresh" />, and it is why the two are not one
    ///     method.</b> A graph pane is blocked or not, and <c>TexturePreview.Describe</c> answers
    ///     for the whole host. A stack has a third kind of answer — it compiled and the compilation
    ///     refused, it wants an imported picture, it writes no map of that usage — so the sentence
    ///     under the pane comes back from the evaluation rather than from a blocker enum.
    ///     <see cref="Refresh" />'s "outside the host's own frame" rule holds identically.
    /// </remarks>
    void RefreshStack() {
        if (stackView is null) {
            return;
        }

        if (stack is null) {
            stackView.Show(null);

            return;
        }

        // ⚠ The fallback matters: with no graphics there is no `LayerStackPreview` at all, and a null
        // picture would leave the pane blank with an empty line under it — which says nothing about
        // whether this host could have drawn one. `TexturePreview.Describe` is the sentence naming
        // which of the two host states it is in.
        //
        // ⚠ And it carries no diagnostics, which is a real difference and not an oversight. Nothing
        // compiles the stack on this path, because a host publishing no graphics at all is not the
        // editor — it is a test or a tool embedding the shell. The state the editor is really in at
        // start-up is graphics *with no device*, and `LayerStackPreview.Evaluate` compiles before it
        // asks for one precisely so that pane is not silent.
        stackView.Show(
            stack,
            stackPreview?.Evaluate(stack)
            ?? new LayerStackPicture(
                null,
                LayerStackPreview.DefaultUsage,
                stack.Document.BaseWidth,
                stack.Document.BaseHeight,
                TexturePreview.Describe(TexturePreview.Blocking(graphics))
            )
        );

        // ⚠ And the paint pane — but only when the binding moved, which is the whole correction.
        // This runs from `stackView.Edited`, and an opacity slider raises that once per frame of a
        // drag; `RefreshPaint` resolves the layer's canvas and uploads a channel, so calling it
        // unconditionally put a 64 MB read and a 64 MB upload on the slider's per-frame path. The
        // mesh and the islands are cached on their keys, which is what the first version of this
        // comment relied on — but `PaintSurface.Open` and `Show` are not, and they are the expensive
        // half. ⚠ The read is now the store's and costs nothing (#948); the upload is unchanged, so
        // the comparison is still worth making. What an edit to these rows can change for that pane
        // is which model, which mesh and which layer, so that triple is what is compared.
        var binding = (stack.Document.Model, stack.Document.Sets[0].Mesh, tool.LayerId, tool.Channel);

        if (binding != paintBinding) {
            paintBinding = binding;

            RefreshPaint();
        }
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

    /// <summary>Opens the selected <c>.vxlayers</c> in the layers panel.</summary>
    /// <remarks>
    ///     <b><see cref="Open" />'s six decisions, unchanged and for its reasons:</b> the project's
    ///     own document is reused so that one file is not two undo histories, <c>Primary</c>'s
    ///     emptiness is asked about by name because <c>AssetId</c> is a struct and a pattern match
    ///     compiles to "always true", and the panel is opened rather than toggled.
    /// </remarks>
    void OpenStack() {
        var asset = project.Selection.Primary;

        if (asset.IsEmpty
            || !project.Assets.TryGetByGuid(asset, out var entry)
            || !entry.Path.EndsWith(LayerStackDocument.Extension, StringComparison.OrdinalIgnoreCase)) {
            shell.Notifications.Show(
                "Select a .vxlayers first",
                NotificationSeverity.Warning,
                "Open Layer Stack opens whatever is selected in the Project panel."
            );

            return;
        }

        var previous = stack;

        if (project.TryGetDocument(asset, out var existing) && existing is LayerStackDocument opened) {
            stack = opened;
        } else {
            stack = new LayerStackDocument(project, asset, project.Paths.Absolute(entry.Path));
        }

        // ⚠ A different stack's canvases are not this stack's, and the store is keyed by absolute
        // path rather than by document — so nothing would evict them except the budget, which is a
        // ceiling and not a schedule. The paint pane is refreshed below and re-pins whatever it
        // opens, so this costs one read of one canvas and gives back the whole of the last stack's.
        if (!ReferenceEquals(previous, stack)) {
            canvases.Clear();
        }

        project.Activate(stack);

        shell.Workspace.Open(StackPanel);
        RefreshStack();

        // ⚠ The paint pane too, and it is the same document. Opening a second stack while the pane
        // was showing the first one's canvas would leave the brush aiming at a layer of a file
        // nobody has open — and the pixels under the pointer would be the old stack's.
        RefreshPaint();
    }
}
