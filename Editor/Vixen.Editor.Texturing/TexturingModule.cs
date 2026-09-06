// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.Core;
using Vixen.Editor.Plugin;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
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
///                 <b>The compiler — closed, and this entry was stale.</b>
///                 <c>TextureGraphCompiler</c> is <c>public</c>; <see cref="LayerStackPreview" />
///                 compiles the open stack through it and shows the map that comes out.
///                 <a href="https://github.com/Rikarin/Vixen/issues/738">#738</a>. ⚠ <b>The
///                 <em>graph</em> pane still evaluates a fixed checkerboard</b> — but its status line
///                 no longer gives the closed reason for it. <c>TexturePreview</c> names
///                 <a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>, the gap that is
///                 actually open: the compiler is public and nothing in the graph pane calls it.
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
    ///     claims.</b> Both hold a <c>TexturePlanEvaluator</c> — that is a pipeline cache per kernel
    ///     and has to be held across evaluations — but both compile through the same public
    ///     <c>TextureGraphCompiler</c> and run the same kernels. Sharing one evaluator between the
    ///     two panels would be the better shape and is not this slice's:
    ///     <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>.
    /// </remarks>
    LayerStackPreview? stackPreview;

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

    /// <summary>What the paint pane is showing, so it can be given back.</summary>
    IEditorImage? painted;

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
            stackPreview = new LayerStackPreview(graphics);

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
                Refresh();
            }
        );

        context.AddPanel(
            StackPanel,
            new StringId("editor.panel.layer-stack", "Layer Stack"),
            panel => {
                stackView = new LayerStackView(panel, tool);

                // ⚠ The one line that makes the panel's edits reach the picture. `LayerStackView`
                // holds no evaluator — two of them over one device would be two pipeline caches,
                // which is `stackPreview`'s own stated reason — so an edit made in a row can redraw
                // the rows and cannot redraw the map. #819.
                stackView.Edited = RefreshStack;

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
            mode == PaintToolMode.Paint
                ? "The brush is " + tool.Describe()
                + ". Drag in the Paint pane to lay a stroke into this stack's first paint layer. ⚠ The 3D "
                + "projection path is still doc 48 § D13 (#574), so a drag in the scene paints nothing."
                : "A drag selects rows and pans the preview."
        );
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

        surface = PaintSurface.Open(stack, tool.LayerId, out var refusal);

        if (surface is null) {
            paintView.Say(refusal);

            return null;
        }

        return surface.Target(tool.Channel);
    }

    /// <summary>A move, an undo or a redo dirtied a rectangle: put the composite back on the screen.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole picture is re-uploaded and the rectangle is only what says one is needed.</b>
    ///     <c>IEditorGraphics.Upload</c> takes a whole image and has no sub-rectangle form, so a
    ///     pointer move at 4K moves 67 MB whatever the stamp covered — which is the cost
    ///     <c>PaintComposite.Resolve</c>'s rectangles were bought to avoid, paid one level up.
    ///     <a href="https://github.com/Rikarin/Vixen/issues/912">#912</a>.
    /// </remarks>
    void Redraw(PaintRect rect) {
        if (paintView?.Live is not { } composite || rect.IsEmpty) {
            return;
        }

        Show(composite.Result, "Painting: " + tool.Describe());
    }

    /// <summary>An undo or a redo moved texels, so the canvas goes back to disk and the map redraws.</summary>
    /// <remarks>
    ///     ⚠ <b>Without this an undo is invisible where it matters most.</b> Undoing a stroke mends
    ///     the <c>PaintImage</c> in memory and nothing else, and <c>LayerStackPreview</c> resolves a
    ///     paint layer by opening the <c>.vxpaint</c> off the disk — so the layers pane went on
    ///     showing the stroke the artist had just taken back, until the next pointer-up happened to
    ///     write the file for its own reasons.
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
    ///         ⚠ <b>The file is written before either.</b> <c>LayerStackPreview</c> resolves a paint
    ///         layer by opening the <c>.vxpaint</c> off the disk, so a stroke that is only in memory
    ///         is a stroke the map cannot show — see <see cref="PaintSurface" />'s remarks for why
    ///         that is forced rather than chosen.
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

        RefreshStack();
        RefreshPaint();
    }

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

            return;
        }

        var opened = PaintSurface.Open(stack, tool.LayerId, out var refusal);

        if (opened is null) {
            paintView.Show(0, stack.Document.BaseWidth, stack.Document.BaseHeight, refusal);

            return;
        }

        Show(
            opened.Canvas.Channel(tool.Channel),
            $"'{opened.Layer.Name}' · {tool.Channel} · this layer's own pixels, not the stack's composite (#849)."
        );
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
        stackView = null;
        paintView = null;

        // ⚠ The surface and not only the pane. It holds a `PaintCanvas`, which at 4K is 67 MB a
        // channel — a module that let the pane go and kept the canvas would leave the largest thing
        // it ever allocated alive for the session.
        surface = null;

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

        // The paint pane's own upload: it is made here rather than by a preview, so nothing else
        // would ever give it back.
        painted?.Dispose();
        painted = null;

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

        if (project.TryGetDocument(asset, out var existing) && existing is LayerStackDocument opened) {
            stack = opened;
        } else {
            stack = new LayerStackDocument(project, asset, project.Paths.Absolute(entry.Path));
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
