// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Yaml;
using Vixen.Editor.AssetEditors;
using Vixen.Editor.Assets.Content;
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

    /// <summary>The verb that turns the open <c>.vxtexgraph</c> into a <c>.vxmat</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/48 § M5's first exit word, and this module contained the string
    ///         <c>bake</c> nowhere until it was added</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a>. Two documents, three
    ///         panels and three verbs were registered, every piece of the bake existed and had tests,
    ///         and <c>new ProjectMaterialBaker</c> had one caller outside tests: a command-line verb
    ///         that reads a folder of PNGs and evaluates no graph. So a graph an artist authored here
    ///         could not become a material by any route a person can take.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It bakes the graph on the canvas rather than the selection</b>, which the two
    ///         Open verbs above do not. What is baked has to be what the preview pane is showing —
    ///         an artist reads the picture and then asks for it — and the selection in the Project
    ///         panel is whatever they last clicked, which after a bake is usually one of the maps.
    ///     </para>
    /// </remarks>
    public const string BakeCommand = "texturing.bake-material";

    /// <summary>The verb that turns the open <c>.vxlayers</c> into one <c>.vxmat</c> per texture set.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § M7's exit word, owed one document after
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a> closed the graph's</b>
    ///         — <a href="https://github.com/Rikarin/Vixen/issues/1029">#1029</a>. Both material-bake
    ///         callers read a graph, so an artist who built a layer stack could not turn it into a
    ///         material by any route a person can take. It is what makes a smart material worth
    ///         applying: somebody who applies one wants a material out.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A second verb rather than <see cref="BakeCommand" /> baking whichever document is
    ///         open.</b> Both can be open at once — that is the point of two panels — so one verb
    ///         would have to guess, and the guess is wrong exactly when an artist has a graph open
    ///         beside the stack they are looking at. Two verbs each say what they bake in their own
    ///         name.
    ///     </para>
    /// </remarks>
    public const string BakeStackCommand = "texturing.bake-stack-material";

    /// <summary>The verb that re-runs the last bake a painted-over map refused.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>: § D4's refusal
    ///         was a dead end in the editor.</b> <c>ProjectMaterialBaker.Write</c> refuses an output
    ///         whose bytes are no longer what the last bake wrote, which is the whole point of the
    ///         digest — and <c>force</c> is how a person says they meant it. The command line carries
    ///         one; a command handler takes no argument, so every editor bake was called with the
    ///         default and the notification could only send the artist away.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It repeats the refused bake rather than forcing a fresh one, and that is what
    ///         makes one verb enough for two.</b> The obvious shape is a forced twin of each bake
    ///         verb — four verbs, two of which are a loaded gun on the Tools menu. This one is armed
    ///         only by a refusal that force answers (<see cref="MaterialBakeOutcome.Painted" />), it
    ///         repeats <em>that</em> bake with the same document, name and folder, and it disarms
    ///         afterwards. So it cannot overwrite anything the artist has not just been told about,
    ///         which a plain force verb can.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The three other refusals do not arm it.</b> A graph that does not compile, a host
    ///         with no device and a file the asset database did not pick up are all things force
    ///         cannot help with — <c>MaterialBakeOutcome.Painted</c> is the flag that tells them
    ///         apart, and offering the control for all four would send an artist to something that
    ///         changes nothing.
    ///     </para>
    /// </remarks>
    public const string ForceBakeCommand = "texturing.bake-material-force";

    /// <summary>The verb that saves the open stack's chosen texture set as a <c>.vxsmartmat</c>.</summary>
    /// <remarks>
    ///     <b>Doc 48 § M10, and until <see cref="SmartMaterial" /> the extension existed in the plan,
    ///     in <c>docs/overview.md</c> and in five <c>.cs</c> comments and in no type at all</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/575">#575</a>. It writes onto the shelf
    ///     (<see cref="SmartMaterial.ShelfFolder" />) rather than beside the stack, because a smart
    ///     material's whole point is that it is applied to something else.
    /// </remarks>
    public const string SaveSmartCommand = "texturing.save-smart-material";

    /// <summary>The verb that puts the selected <c>.vxsmartmat</c> on top of the open stack's set.</summary>
    /// <remarks>
    ///     ⚠ <b>The selection and not the canvas, unlike <see cref="BakeCommand" />.</b> What is
    ///     applied is a file an artist picks out of the shelf and what it is applied <em>to</em> is
    ///     the stack they are looking at, so the two halves come from the two places an artist is
    ///     already pointing.
    /// </remarks>
    public const string ApplySmartCommand = "texturing.apply-smart-material";

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

    /// <summary>The swatch under every node of the open graph: doc 48 § M4's per-node previews.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The assignment <a href="https://github.com/Rikarin/Vixen/issues/1015">#1015</a> is
    ///         about.</b> <c>TextureGraphPreviews</c> had device tests and no production caller for
    ///         four batches — the shader graph wires its equivalent through
    ///         <c>EditorApplication.ShaderGraphPreviews</c> and the texture graph wired nothing, so
    ///         the swatch under a node that asked for one was never drawn. Every part of it existed;
    ///         the four lines that make it appear are the construction here,
    ///         <c>view.Canvas.PreviewSource</c> in the graph panel's factory,
    ///         <see cref="PluginContext.OnUpdate" /> and <see cref="Release" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A third pane and not a third evaluator</b>, which is the whole reason this could
    ///         be wired at all — <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>,
    ///         <a href="https://github.com/Rikarin/Vixen/issues/988">#988</a>. It takes the same
    ///         <see cref="Evaluator" /> lease <see cref="preview" /> and <see cref="stackPreview" />
    ///         take, asked per rebuild rather than held, so a session with the graph panel open pays
    ///         one pipeline cache and not two.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The compiler comes off the <em>canvas</em> and not off <see cref="document" />,
    ///         and the difference is a descent.</b> An author who has double-clicked into a published
    ///         compound is looking at the library's model rather than the document's graph, and
    ///         <c>TextureGraphView.Show</c> keeps <c>Canvas.Registry</c> and
    ///         <c>Canvas.SubGraphSource</c> pointed at whatever is on screen. Compiling the graph the
    ///         canvas is drawing against the document's library would resolve a published node type
    ///         through the wrong one.
    ///     </para>
    /// </remarks>
    TextureGraphPreviews? nodePreviews;

    /// <summary>Where those swatches' pixels become numbers the canvas can draw.</summary>
    /// <remarks>
    ///     ⚠ <b>Held so it can be disposed, and for nothing else.</b> Each picture is a texture and a
    ///     descriptor set of the host's, and <see cref="Release" />'s own remark — "a picture left
    ///     behind is a texture the renderer holds for the rest of the session" — is about exactly one
    ///     of these per node of every graph that has been open.
    /// </remarks>
    TexturePreviewImages? previewImages;

    /// <summary>What the force verb says to run, appended to every refusal force can answer.</summary>
    /// <remarks>
    ///     ⚠ <b>Only on <see cref="MaterialBakeOutcome.Painted" />.</b> The other three refusals are
    ///     things force cannot help with, and a sentence offering it for them would send an artist to
    ///     a control that changes nothing — which is the whole reason that flag exists rather than
    ///     the caller matching on the message.
    /// </remarks>
    const string ForceAdvice =
        "Replacing it is a deliberate act: run Bake Material (Force) to repeat exactly this bake "
        + "over it. That verb does nothing until a bake has been refused for this reason.";

    /// <summary>How to re-run the last bake a painted-over map refused, or null when there was none.</summary>
    /// <remarks>
    ///     ⚠ <b>A closure and not a flag, because the refusal can have come from either document</b>
    ///     — <a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>. What has to be repeated
    ///     is the bake that was refused, with the document, the name and the folder it had; a boolean
    ///     read by whichever bake verb ran next would force a bake of whatever happened to be open.
    /// </remarks>
    Action? forced;

    /// <summary>What turns the open graph into a material, once there is a device to run it on.</summary>
    /// <remarks>
    ///     ⚠ <b>Built beside the previews and on the same terms, which is what makes "the bake runs
    ///     the code the pane runs" a fact rather than an intention.</b> It shares
    ///     <see cref="evaluator" /> and <see cref="canvases" /> with both panes — so a bake dispatches
    ///     through the pipelines the preview already compiled, and reads the pixels a stroke has not
    ///     yet saved, which is what the artist is looking at when they ask for it.
    /// </remarks>
    MaterialBakeRoute? baker;

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

        // ⚠ **The starter shelf, and it is the only route doc 48 § M10's five have into a project.**
        // Every verb that consumes a smart material consumes an *asset* — `ApplySmartMaterial` reads
        // `project.Selection.Primary`, and `LayerStackEditorFactory` claims a file extension — so a
        // `.vxsmartmat` this assembly embeds and never writes down is content nothing can select.
        // `Install` writes only the names the shelf has not got, so an artist's edited copy survives
        // every later activation and the shipped one stays unreachable behind it.
        //
        // ⚠ **And the scan is the half that makes any of that true.** A plugin activates *after* the
        // project has been indexed, so five files written here are on the disk and not in the asset
        // database — `project.Selection.Primary` can never name one and `AssetEditorRegistry` can
        // never open one — until the editor is restarted. Writing them was the visible half of the
        // work and reaching them was the whole point of it.
        if (SmartMaterial.Install(project.Paths.Assets).Count > 0) {
            project.Assets.Scan();
        }

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
            baker = new MaterialBakeRoute(graphics, Evaluator, canvases);

            // ⚠ #1015, and the two delegates are what make it a third *pane* rather than a third
            // evaluator. The first answers the one evaluator for whatever device the host has right
            // now — asked per rebuild, so a device that has gone is never handed back to the lease —
            // and the second reads the node library off the canvas, which is the graph that is
            // actually being drawn. Both may answer null, which is an ordinary state: the editor
            // publishes graphics before it has a device, and the panel may not be open.
            previewImages = new TexturePreviewImages(graphics);
            nodePreviews = new TextureGraphPreviews(
                () => graphics?.Device is { } ready ? Evaluator(ready) : null,
                () => view?.Canvas is { } canvas
                    ? new TextureGraphCompiler(canvas.Registry) { SubGraphSource = canvas.SubGraphSource }
                    : null,
                previewImages
            );

            // ⚠ The per-frame half, and without it the swatches would be assigned and never drawn:
            // `TryGet` runs from the canvas's draw and deliberately never evaluates, so something
            // outside a draw has to run the expensive tier. `PluginContext.OnUpdate` is that
            // something — the same door `TerrainModule` follows a selection through — and it is
            // rationed to one graph per frame inside `Update` for `ShaderGraphPreviewRenderer`'s
            // reason.
            context.OnUpdate(_ => nodePreviews?.Update());

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

                // ⚠ The line #1015 is, and it is assigned on every build of the panel rather than
                // once: a dock panel's factory runs again on reopen, and the canvas it made last
                // time went with the elements. A source left unassigned is the state the issue
                // describes — every node's picture computed and none of them drawn.
                view.Canvas.PreviewSource = nodePreviews;

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

        context.AddCommand(
            BakeCommand,
            new StringId("editor.command." + BakeCommand, "Bake Material"),
            BakeMaterial
        );

        context.AddCommand(
            BakeStackCommand,
            new StringId("editor.command." + BakeStackCommand, "Bake Material from Layers"),
            BakeStackMaterial
        );

        context.AddCommand(
            ForceBakeCommand,
            new StringId("editor.command." + ForceBakeCommand, "Bake Material (Force)"),
            BakeForced
        );

        context.AddCommand(
            SaveSmartCommand,
            new StringId("editor.command." + SaveSmartCommand, "Save as Smart Material"),
            SaveSmartMaterial
        );

        context.AddCommand(
            ApplySmartCommand,
            new StringId("editor.command." + ApplySmartCommand, "Apply Smart Material"),
            ApplySmartMaterial
        );

        // Where the verb belongs rather than a menu of its own — doc 36, and `PluginContext.FindMenu`
        // says why. A host with no Tools menu gets the command in the palette and the keymap, which
        // is the whole of what a menu entry adds.
        if (context.FindMenu(EditorStrings.MenuTools.Id) is { } tools) {
            context.AddMenuItem(tools, OpenCommand);
            context.AddMenuItem(tools, OpenStackCommand);
            context.AddMenuItem(tools, PaintCommand);
            context.AddMenuItem(tools, BakeCommand);
            context.AddMenuItem(tools, BakeStackCommand);

            // ⚠ Under the two it answers, which is where #1019 asks for it: an artist reads a refusal
            // naming a file they painted over and looks for the control in the menu they just used.
            context.AddMenuItem(tools, ForceBakeCommand);
            context.AddMenuItem(tools, SaveSmartCommand);
            context.AddMenuItem(tools, ApplySmartCommand);
        }
    }

    /// <summary>Turns the graph on the canvas into a material in the project.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The route <a href="https://github.com/Rikarin/Vixen/issues/1009">#1009</a> says did
    ///         not exist.</b> Everything below the notification is
    ///         <see cref="MaterialBakeRoute.Bake" />, which is the compile the pane runs, the
    ///         evaluator both panes share, and the <see cref="ProjectMaterialBaker" /> the command
    ///         line calls. There is one baker and this is a second caller of it, which is the only
    ///         arrangement in which "the same code the CLI runs" is a fact.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The material is named after the graph's file and there is no dialog</b>, which is
    ///         a decision worth stating because it is what an artist will ask about first. A name is
    ///         the set's file name and not its identity — <c>MaterialBakeRecord.SourceAsset</c> is
    ///         that, so re-baking the same graph overwrites its own maps and keeps their GUIDs
    ///         however the file is called. A name control is a panel this verb does not have yet;
    ///         until it does, the graph's own name is the one answer that cannot silently adopt
    ///         another graph's set.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Force is now offered, and it is a second verb rather than an argument</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>. A map somebody has
    ///         painted over stops the bake, which is § D4's whole point, and a command handler
    ///         carries no argument to force with. ⚠ <b>The sentence here used to send the artist to
    ///         <c>vixen texture bake --force</c> and that verb cannot re-bake a graph</b>: its
    ///         <c>--from</c> is required and reads a folder of maps
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/1020">#1020</a>), so running it
    ///         would have written a <em>second</em> set beside the painted one rather than replacing
    ///         anything. <see cref="ForceBakeCommand" /> repeats <em>this</em> bake instead.
    ///     </para>
    /// </remarks>
    void BakeMaterial() => BakeGraph(document, force: false);

    /// <summary>Turns the graph on the canvas into a material, forcing or not.</summary>
    /// <param name="subject">The document to bake.</param>
    /// <param name="force">Whether to overwrite outputs somebody has painted over.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The forced run is a closure this arms rather than a flag the verb reads</b>, so
    ///         that <see cref="ForceBakeCommand" /> can answer a refusal from either document with
    ///         one handler and cannot force a bake nobody asked about. See <see cref="Refused" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the document is a <em>parameter</em> rather than the field, which is the
    ///         whole of what makes that closure safe.</b> Reading <c>document</c> inside the forced
    ///         run would force-bake whatever happened to be open when the artist reached for the
    ///         verb — and the ordinary way to meet a painted-over refusal is to go and look at the
    ///         file that was painted, which means opening something else first. Capturing the
    ///         subject is what stops a verb whose whole purpose is overwriting somebody's work from
    ///         overwriting the <em>wrong</em> work.
    ///     </para>
    /// </remarks>
    void BakeGraph(TextureGraphDocument? subject, bool force) {
        if (subject is null) {
            shell.Notifications.Show(
                "No graph is open",
                NotificationSeverity.Warning,
                "Bake Material bakes the graph on the Texture Graph canvas. Select a .vxtexgraph and "
                + "run Open Texture Graph first."
            );

            return;
        }

        if (baker is null) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                TexturePreview.Describe(TexturePreview.Blocking(graphics))
            );

            return;
        }

        var outcome = baker.Bake(subject, Path.GetFileNameWithoutExtension(subject.AssetPath), force: force);

        Refused(outcome.Painted ? () => BakeGraph(subject, force: true) : null);

        shell.Notifications.Show(
            outcome.Set is null ? "Nothing baked" : "Baked '" + outcome.Set.Name + "'",
            outcome.Set is null ? NotificationSeverity.Warning : NotificationSeverity.Info,
            outcome.Painted ? outcome.Status + " " + ForceAdvice : outcome.Status
        );

        // ⚠ The pane too, and not because the picture changed — it did not. `Bake` compiles through
        // the document, which republishes the compound library; a pane left alone would go on showing
        // the picture it had while the material on disk was made from a different graph. It is also
        // what puts the compiler's warnings back under the pane after a bake reported them.
        Refresh();
    }

    /// <summary>Turns the open layer stack into one material per texture set.</summary>
    /// <remarks>
    ///     <b><a href="https://github.com/Rikarin/Vixen/issues/1029">#1029</a>'s editor half.</b>
    ///     Everything below the notification is <see cref="MaterialBakeRoute" />, which is the
    ///     compile the layers pane runs, the evaluator both panes share and the
    ///     <see cref="ProjectMaterialBaker" /> the command line calls.
    /// </remarks>
    void BakeStackMaterial() => BakeStack(stack, force: false);

    /// <summary>Turns the open layer stack into materials, forcing or not.</summary>
    /// <param name="subject">The stack to bake.</param>
    /// <param name="force">Whether to overwrite outputs somebody has painted over.</param>
    /// <remarks>
    ///     ⚠ <b>The stack is a parameter and not the field, for <see cref="BakeGraph" />'s
    ///     reason</b>: the closure <see cref="Refused" /> arms must repeat the bake that was
    ///     refused, not whatever is open when somebody presses the verb.
    ///     ⚠ <b>One notification for the whole stack rather than one per set.</b> A set that refused
    ///     and a set that wrote are both facts about the same gesture, and an artist reading two
    ///     notifications has to work out which of them was the answer to what they pressed. Every
    ///     set's own sentence is in the detail, named by its set.
    /// </remarks>
    void BakeStack(LayerStackDocument? subject, bool force) {
        if (subject is null) {
            shell.Notifications.Show(
                "No layer stack is open",
                NotificationSeverity.Warning,
                "Bake Material from Layers bakes the stack in the Layer Stack panel. Select a "
                + ".vxlayers and run Open Layer Stack first."
            );

            return;
        }

        // ⚠ Before the device, and it is the verb's answer to #1070 rather than a guard against a
        // crash. A shelf entry bakes *something* — it is a stack — and what it bakes is wrong twice
        // over: a smart material has no model, so every mesh-map mask in it refuses and the artist
        // reads a wall of diagnostics; and the materials it does write land in the project named
        // after a shelf entry nothing in a scene refers to. The gesture should not have been offered,
        // so the answer names the file that should have been baked instead.
        if (subject.IsSmartMaterial) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                $"'{Path.GetFileName(subject.AssetPath)}' is a smart material, which is a stack "
                + "fragment with no model and no meshes — so its mesh-map masks have nothing to "
                + "measure and its materials would belong to no asset. Apply it to a .vxlayers and "
                + "bake that."
            );

            return;
        }

        if (baker is null) {
            shell.Notifications.Show(
                "Nothing baked",
                NotificationSeverity.Warning,
                TexturePreview.Describe(TexturePreview.Blocking(graphics))
            );

            return;
        }

        var outcomes = baker.Bake(subject, Path.GetFileNameWithoutExtension(subject.AssetPath), force: force);
        var written = outcomes.Count(one => one.Set is not null);
        var painted = outcomes.Any(one => one.Painted);

        Refused(painted ? () => BakeStack(subject, force: true) : null);

        var detail = string.Join(" · ", outcomes.Select(one => one.Status));

        shell.Notifications.Show(
            written == 0
                ? "Nothing baked"
                : $"Baked {written.ToString(CultureInfo.InvariantCulture)} of "
                + $"{outcomes.Length.ToString(CultureInfo.InvariantCulture)}",
            written == 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
            painted ? detail + " " + ForceAdvice : detail
        );

        RefreshStack();
    }

    /// <summary>Re-runs the last bake a painted-over map refused.</summary>
    /// <remarks>
    ///     ⚠ <b>It is armed by a refusal and disarmed by running, which is what makes one verb safe
    ///     enough to sit on the Tools menu</b> — <a href="https://github.com/Rikarin/Vixen/issues/1019">#1019</a>.
    ///     A plain "bake forced" verb is a control that overwrites an artist's paint whenever
    ///     somebody reaches for it out of order; this one does nothing at all until a bake has been
    ///     refused for the one reason force answers, and it repeats <em>that</em> bake — same
    ///     document, same name, same folder — rather than starting a new one from whatever happens to
    ///     be open now.
    /// </remarks>
    void BakeForced() {
        if (forced is not { } repeat) {
            shell.Notifications.Show(
                "Nothing to force",
                NotificationSeverity.Warning,
                "Bake Material (Force) repeats a bake that was refused because one of its maps had "
                + "been painted over, and no bake has been refused for that reason. Run Bake Material "
                + "or Bake Material from Layers first."
            );

            return;
        }

        // ⚠ Cleared before the run and not after it. The repeat arms this again if it is refused
        // again — for a *second* painted file, say — and clearing afterwards would throw that away.
        forced = null;

        repeat();
    }

    /// <summary>Remembers, or forgets, the bake a force verb would repeat.</summary>
    /// <param name="repeat">How to re-run it forced, or null when nothing force answers was refused.</param>
    /// <remarks>
    ///     ⚠ <b>Called on every bake, including the ones that worked, and the null half is the load
    ///     bearing one.</b> An arming that were never cleared would leave the force verb pointing at
    ///     a refusal the artist has since resolved by hand — so pressing it would overwrite a file
    ///     that had stopped being the one they were told about.
    /// </remarks>
    void Refused(Action? repeat) => forced = repeat;

    /// <summary>Saves the open stack's chosen texture set onto the shelf as a <c>.vxsmartmat</c>.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Doc 48 § M10's file, given the one thing a file format needs to stop being a
    ///         plan: somebody who makes one</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/575">#575</a>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Named after the stack's file with no dialog</b>, which is
    ///         <see cref="BakeMaterial" />'s decision and is taken here for its reason: a name control
    ///         is a panel this verb does not have, and the stack's own name is the one answer that
    ///         cannot silently adopt another stack's shelf entry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An existing shelf entry of that name is replaced, and the notification says so.</b>
    ///         The alternative — writing <c>Hull_2</c> beside it — makes the ordinary case, which is
    ///         re-saving after an edit, litter the shelf with every draft; and a shelf a third party
    ///         reads is exactly where a pile of near-identical names is expensive.
    ///     </para>
    /// </remarks>
    void SaveSmartMaterial() {
        if (stack is null) {
            shell.Notifications.Show(
                "No layer stack is open",
                NotificationSeverity.Warning,
                "Save as Smart Material takes the layers out of the stack in the Layer Stack panel. "
                + "Select a .vxlayers and run Open Layer Stack first."
            );

            return;
        }

        // ⚠ The second half of #1070's verb list. Extracting a shelf entry from a shelf entry writes
        // it back onto the shelf under its own name — harmless, and confusing in the way that costs
        // an artist ten minutes: the file it replaced is the file they were editing, and the
        // notification would say it had been saved as something new. What they want is the ordinary
        // save, which the document already does.
        if (stack.IsSmartMaterial) {
            shell.Notifications.Show(
                "Nothing saved",
                NotificationSeverity.Warning,
                $"'{Path.GetFileName(stack.AssetPath)}' is already a smart material. Save the "
                + "document to write your edits back onto the shelf; Save as Smart Material is for "
                + "taking the layers out of a .vxlayers."
            );

            return;
        }

        var name = Path.GetFileNameWithoutExtension(stack.AssetPath);
        var extract = SmartMaterial.Extract(stack.Document, name, stack.PaintSet);

        if (extract.Material is not { } material) {
            shell.Notifications.Show("Nothing saved", NotificationSeverity.Warning, extract.Status);

            return;
        }

        if (SmartMaterial.FolderOf(project.Paths.Assets) is not { } shelf) {
            shell.Notifications.Show(
                "Nothing saved",
                NotificationSeverity.Warning,
                "This project publishes no assets folder, so there is no shelf to save onto."
            );

            return;
        }

        var file = Path.Combine(shelf, SmartMaterial.Safe(name) + SmartMaterial.Extension);
        var replaced = File.Exists(file);

        try {
            Directory.CreateDirectory(shelf);
            File.WriteAllText(file, LayerStackYaml.Write(material));
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            // ⚠ A sentence rather than an exception, which is this module's rule for every route that
            // runs from a command handler: a throw out of one takes the editor's frame with it.
            shell.Notifications.Show("Nothing saved", NotificationSeverity.Warning, failure.Message);

            return;
        }

        // The shelf is a folder under `Assets/`, so what lands in it is an asset — and a scan is what
        // puts it in the Project panel the artist will select it from to apply it.
        project.Assets.Scan();

        shell.Notifications.Show(
            "Saved '" + name + "'",
            NotificationSeverity.Info,
            extract.Status
            + $" It is on the shelf at Assets/{SmartMaterial.ShelfFolder}/, ready to apply to another "
            + "stack."
            + (replaced ? " ⚠ It replaced the entry of that name that was already there." : "")
        );
    }

    /// <summary>Puts the selected <c>.vxsmartmat</c> on top of the open stack's chosen texture set.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The open stack may itself be a shelf entry, and that is allowed rather than
    ///         overlooked</b> — <a href="https://github.com/Rikarin/Vixen/issues/1070">#1070</a>. Its
    ///         two neighbours refuse a <c>.vxsmartmat</c> in the panel and this one does not: a shelf
    ///         entry composed of two others — rust over panel wear — is a real thing to author, and
    ///         <see cref="SmartMaterial.Prepare" /> already drops everything a fragment must not
    ///         carry, so the result is a fragment either way. What it needs is no code: the
    ///         difference between the three verbs is only which of them should have been offered.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Through the document's command stack, as one undo entry.</b> A verb that mutated
    ///         <c>TextureSetAsset.Layers</c> directly would put ten layers into a stack the artist
    ///         could not take back out except by hand — and it would leave <c>CommandStack.Depth</c>
    ///         unmoved, which is the edge <c>LayerStackView</c> follows to notice that the document
    ///         changed (<a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>), so the rows
    ///         would not even redraw.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The refusal names what the shelf holds.</b> "No smart material is selected"
    ///         leaves an artist unable to tell an empty shelf from a wrong selection, and those two
    ///         have different next steps.
    ///     </para>
    /// </remarks>
    void ApplySmartMaterial() {
        if (stack is null) {
            shell.Notifications.Show(
                "No layer stack is open",
                NotificationSeverity.Warning,
                "Apply Smart Material puts a .vxsmartmat on top of the stack in the Layer Stack "
                + "panel. Select a .vxlayers and run Open Layer Stack first."
            );

            return;
        }

        var asset = project.Selection.Primary;

        // ⚠ `Primary` on an empty selection is `AssetId.Empty` rather than null — it is a struct — so
        // the emptiness is asked about by name, which is `Open`'s finding one verb over.
        if (asset.IsEmpty
            || !project.Assets.TryGetByGuid(asset, out var entry)
            || !entry.Path.EndsWith(SmartMaterial.Extension, StringComparison.OrdinalIgnoreCase)) {
            var shelf = SmartMaterial.Shelf(project.Paths.Assets);

            shell.Notifications.Show(
                "Select a .vxsmartmat first",
                NotificationSeverity.Warning,
                "Apply Smart Material applies whatever is selected in the Project panel. "
                + (shelf.Count > 0
                    ? $"This project's shelf holds {string.Join(", ", shelf)}."
                    : $"This project's shelf (Assets/{SmartMaterial.ShelfFolder}/) is empty — run Save "
                    + "as Smart Material on a stack to put one there.")
            );

            return;
        }

        LayerStackAsset material;

        try {
            material = LayerStackYaml.Read(File.ReadAllText(project.Paths.Absolute(entry.Path)));
        } catch (Exception failure) when (failure is YamlBindingException or YamlParseException
            or NotSupportedException or IOException) {
            shell.Notifications.Show("Nothing applied", NotificationSeverity.Warning, failure.Message);

            return;
        }

        // ⚠ The set the panel is showing and not `Sets[0]` — #927. `SetFor` falls back to the first
        // set for a name nothing matches, which is what an empty `PaintSet` means, and answers null
        // only for a stack with no set at all.
        if (LayerStackEdit.SetFor(stack.Document, stack.PaintSet) is not { } target) {
            shell.Notifications.Show(
                "Nothing applied",
                NotificationSeverity.Warning,
                "This stack has no texture set, so there is nothing to apply a smart material to."
            );

            return;
        }

        var applied = SmartMaterial.Prepare(target, material);

        if (applied.Layers.IsDefaultOrEmpty) {
            shell.Notifications.Show("Nothing applied", NotificationSeverity.Warning, applied.Status);

            return;
        }

        stack.Stack.Execute(
            new ApplySmartMaterialCommand(stack, target.Name, applied.Layers, "Apply Smart Material")
        );

        shell.Notifications.Show("Applied '" + material.Name + "'", NotificationSeverity.Info, applied.Status);

        RefreshStack();
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
    ///     ⚠ <b>The set is named, and it is now the set that was actually chosen.</b>
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>: every path here used to
    ///     take <c>Sets[0]</c> while the messages read as though one had been picked, so a multi-set
    ///     stack painted into the first one and said nothing. The selector is built and
    ///     <c>LayerStackDocument.PaintSet</c> is the choice, so this sentence names <em>that</em>
    ///     set — ⚠ naming the first one after the selector landed would have been worse than the
    ///     original silence, because it would be a confident wrong answer.
    /// </remarks>
    string Aimed() {
        var chosen = stack is null ? null : LayerStackEdit.SetFor(stack.Document, stack.PaintSet);
        var set = chosen is not null ? $"set '{chosen.Name}'" : "this stack";

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
    ///     ⚠ <b>The set is the chosen one, and this was the last <c>Sets[0]</c> left</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>,
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1048">#1048</a>. Once
    ///     <c>PaintSurface.Open</c>, <c>LayerStackView</c> and <c>LayerStackPreview</c> all resolved
    ///     <c>LayerStackDocument.PaintSet</c> through <c>LayerStackEdit.SetFor</c>, a mesh still
    ///     resolved for the first set became the only thing in the plugin that disagreed — and the
    ///     disagreement is silent: a stroke aimed at set B would be dilated and refused against set
    ///     A's islands.
    ///     ⚠ <b>Which is why <c>PaintSet</c> is a term of the cache key</b>, not only of the
    ///     resolve: without it, switching sets keeps the previous set's mesh.
    /// </remarks>
    LayerStackMesh? Mesh() {
        if (stack is null) {
            mesh = null;
            meshKey = null;
            meshRefusal = "";

            return null;
        }

        var asset = stack.Document;
        var set = LayerStackEdit.SetFor(asset, stack.PaintSet);

        var key = stack.AssetPath
            + "\n" + asset.Model
            + "\n" + stack.PaintSet
            + "\n" + (set?.Mesh ?? "")
            + "\n" + Exported(asset.Model)
            + "\n" + ((geometry as ProjectMeshSource)?.Revision ?? 0).ToString(CultureInfo.InvariantCulture);

        if (!string.Equals(key, meshKey, StringComparison.Ordinal)) {
            meshKey = key;
            mesh = LayerStackMesh.Open(project, asset, set, geometry, out meshRefusal);
        }

        return mesh;
    }

    /// <summary>What the model file on disk is, as a string that moves when the file does.</summary>
    /// <param name="model">The stack's model, project-relative, or empty when it names none.</param>
    /// <returns>Its write time and length, or the empty string where there is no file to stat.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Half of <a href="https://github.com/Rikarin/Vixen/issues/971">#971</a>, and the
    ///         half the issue does not name.</b> A stack open while the artist re-exports the model
    ///         over the top of itself keeps the previous export's islands and goes on accepting the
    ///         previous export's texels: none of the three terms the key used to have — the stack's
    ///         path, its model and the set's mesh — moves when a file's contents change. It needs no
    ///         import to reproduce.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And the revision beside it is the other half, because a stat cannot see it.</b> A
    ///         re-import driven by a version bump or an import-settings edit rewrites the chunks under
    ///         an unchanged <c>.obj</c>, so the stamp is identical and only
    ///         <see cref="ProjectMeshSource.Revision" /> moves. Each term catches a case the other
    ///         misses, which is why both are here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A stat per gesture rather than per stamp.</b> <see cref="Mesh" /> is called from
    ///         a pointer-<i>down</i> and from a panel refresh, never from a pointer move — the whole
    ///         reason the resolve is cached is that it parses a model file, and this is three orders
    ///         of magnitude under that. A missing or unreadable file answers the empty string, which
    ///         is the same answer as "no model" and is right: there is nothing to re-read.
    ///     </para>
    /// </remarks>
    string Exported(string model) {
        if (model.Length == 0) {
            return "";
        }

        try {
            var file = new FileInfo(project.Paths.Absolute(model));

            return file.Exists
                ? file.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
                + ":" + file.Length.ToString(CultureInfo.InvariantCulture)
                : "";
        } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) {
            return "";
        }
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

        // ⚠ The source before the sink, and not the other way round. Disposing the source releases
        // every number it handed out, which is what empties the sink; disposing the sink first would
        // leave the source releasing numbers nothing holds any more — correct today because
        // `TexturePreviewImages.Release` ignores what it does not know, and the kind of order that
        // stops being correct the moment either side keeps a count.
        nodePreviews?.Dispose();
        nodePreviews = null;

        previewImages?.Dispose();
        previewImages = null;

        // ⚠ Dropped rather than disposed, because it owns nothing: the evaluator is lent to it, the
        // canvases are the module's, and every texture it makes is a `TextureUploads` inside one
        // call. What it does hold is the host's `IEditorGraphics`, which is exactly what the two
        // lines above and the `graphics = null` below exist to stop holding.
        baker = null;

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

        // ⚠ And the swatches, which are the one thing here that survives into the next device.
        // `Drop` gives the pictures back and marks every watched graph dirty rather than disposing
        // the source: the canvas is still holding it as its `PreviewSource`, and a disposed source
        // throws from `Update` — which is this module's per-frame work, which `PluginHost.Update`
        // answers by unloading the plugin. The numbers themselves name textures the host made on the
        // device that is going, so keeping them would draw a swatch through a handle whose texture
        // has been destroyed.
        nodePreviews?.Drop();
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
            stackPreview?.Evaluate(stack, LayerStackPreview.DefaultUsage, stackView?.SetName ?? "")
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
        //
        // ⚠ **And the set is asked for rather than indexed, because a stack can have none** — #983.
        // `LayerStackDocument` answers a file it could not read with a document holding no texture
        // set at all, which is the honest answer and is not the starter stack; this line indexed
        // `Sets[0]` and threw, so running `Open Layer Stack` on a `.vxlayers` naming a blend mode
        // this build lacks took the command handler down before the panel that was to explain it had
        // been built. The other `Sets[0]` in this file already asks the question this way.
        var mesh = stack.Document.Sets.Count > 0 ? stack.Document.Sets[0].Mesh : "";
        var binding = (stack.Document.Model, mesh, tool.LayerId, tool.Channel);

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

        // ⚠ Either extension — #1070. A `.vxsmartmat` is a `.vxlayers` byte for byte and this is the
        // verb half of the claim `LayerStackEditorFactory` makes for the double-click; a verb that
        // took only one of the two would leave the shelf openable by mouse and not by command.
        if (asset.IsEmpty
            || !project.Assets.TryGetByGuid(asset, out var entry)
            || !(entry.Path.EndsWith(LayerStackDocument.Extension, StringComparison.OrdinalIgnoreCase)
                || entry.Path.EndsWith(SmartMaterial.Extension, StringComparison.OrdinalIgnoreCase))) {
            shell.Notifications.Show(
                "Select a .vxlayers or a .vxsmartmat first",
                NotificationSeverity.Warning,
                "Open Layer Stack opens whatever is selected in the Project panel. A shelf entry "
                + "opens in the same rows: it is a stack with no model and nothing painted."
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
