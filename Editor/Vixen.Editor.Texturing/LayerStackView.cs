// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Editor.Texturing.Painting;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Reactive;
using GraphPortDirection = Vixen.Editor.NodeGraph.PortDirection;

namespace Vixen.Editor.Texturing;

/// <summary>What a mask row reads, as the one value an editor writes back.</summary>
/// <param name="Source">Which of the six a mask reads.</param>
/// <param name="Value">The number, when it is <see cref="LayerMaskSource.Constant" />.</param>
/// <param name="Asset">The imported image, when it is <see cref="LayerMaskSource.Texture" />.</param>
/// <param name="Anchor">The layer read, when it is <see cref="LayerMaskSource.Anchor" />.</param>
/// <param name="Generator">The published compound, when it is <see cref="LayerMaskSource.Generator" />.</param>
/// <param name="Map">What the bake measures, when it is <see cref="LayerMaskSource.Bake" />.</param>
/// <remarks>
///     <para>
///         ⚠ <b>One shape for two records, which is what makes a single source editor possible</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/882">#882</a>). <c>MaskAsset</c> and
///         <c>MaskLayerAsset</c> carry the same discriminator and the same five members behind it, and
///         differ in what a mask <em>base</em> does not have — an <c>Enabled</c>, a blend mode and an
///         opacity. Reading and writing the half they share through one value is what lets the base
///         row and an entry row be the same six controls with two different write-backs.
///     </para>
///     <para>
///         ⚠ <b><c>Paint</c> is in the discriminator and has no editor.</b> A painted mask's canvas is
///         named by <c>MaskAsset.Paint</c>, and that name is written by the brush at the first stroke
///         — <c>TexturingModule.Recorded</c> — rather than typed. Offering a field for it would be
///         offering to point a layer at somebody else's pixels.
///     </para>
/// </remarks>
readonly record struct MaskSourceEdit(
    LayerMaskSource Source,
    float Value,
    string Asset,
    string Anchor,
    string Generator,
    string Map
);

/// <summary>A layer stack, open: the rows in composite order, and the map they make.</summary>
/// <remarks>
///     <para>
///         <b>What <a href="https://github.com/Rikarin/Vixen/issues/806">#806</a> is for.</b> Three
///         thousand four hundred lines of layer stack were reachable only from xunit because nothing
///         registered <c>.vxlayers</c> — no kind, no factory, no panel. This is the panel half; the
///         registration is <see cref="TexturingModule" />.
///     </para>
///     <para>
///         ⚠ <b>It edits now, and every edit is an <c>IEditorCommand</c> on the document's own
///         stack.</b> This panel was deliberately read-only until
///         <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a>, and the reason it gave was
///         not squeamishness: nothing in the layer stack was routed through
///         <c>EditorDocument.Stack</c>, so a panel that offered a reorder would have offered a
///         gesture with no undo and no dirty flag — one that a save might or might not have carried.
///         <c>LayerStackCommands</c> is the model that had to exist first; what changed here is that
///         the rows write through it.
///     </para>
///     <para>
///         ⚠ <b>The list under the rows is where a diagnostic goes, and it is what
///         <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a> found this panel had
///         nowhere for.</b> <c>TG0022</c> — the terminus rescale — was chosen over a silent rescale
///         because "it is said", and no production type in this tree rendered a
///         <c>NodeDiagnostic</c> at all: the one consumer read a list of them and kept the errors,
///         which drops precisely the diagnostics that did not stop the map. Every severity is listed,
///         whether or not there is a picture, because a warning is by definition the kind that comes
///         with one.
///     </para>
///     <para>
///         ⚠ <b>Top of the panel is the <em>last</em> layer, which is the reverse of the file.</b>
///         <c>TextureSetAsset.Layers</c> is stored in composite order so that reading the file top to
///         bottom is reading the arithmetic in the order it happens; every layers panel ever made
///         shows the topmost layer first. The reversal is here, in the view, rather than in the file
///         or in the compiler — which is what that member's own remarks ask for. It is also why the
///         button labelled <em>up</em> asks <c>MoveLayerCommand</c> for <c>+1</c>: the command speaks
///         the file's order and this class is the one thing that knows the two differ.
///     </para>
///     <para>
///         ⚠ <b>A mask's own entries are rows in this same list, indented under their layer, rather
///         than a second pane.</b> Doc 48 § D10 makes a mask a small stack of its own, and the
///         reference implementations put it behind a selection — a thumbnail on the row, its stack in
///         a properties pane. Two things argue against copying that here. A mask is the only thing in
///         a stack that can name <em>another layer</em>, and an anchor whose target is in a different
///         pane from the anchor is a reference an artist has to hold in their head; and a pane you
///         have to select a layer to see cannot answer "which of these twenty layers has a mask at
///         all", which is the question somebody scanning a stack is actually asking. One list answers
///         both, and the cost is a longer list — which is what the group indent already spends.
///     </para>
///     <para>
///         ⚠ <b>Every typed control here is named by <em>class</em>, and a container by tag.</b>
///         <c>UiElement.Add&lt;T&gt;(string)</c>'s first parameter is the tag, so
///         <c>row.Add&lt;Slider&gt;("layer-stack-opacity")</c> built a slider that no <c>slider</c>
///         rule in <c>ControlTheme.vcss</c> matched — no height, no minimum width and no
///         focus ring, for twenty-eight controls and sixteen batches
///         (<a href="https://github.com/Rikarin/Vixen/issues/1071">#1071</a>). A <see cref="Slider" />
///         survives that looking like a slider because it draws its own track, which is why nobody
///         saw it; a <c>TextBox</c> does not, since <c>textbox</c>'s rule carries the
///         <c>position: relative</c> its placeholder is laid out against. The untyped
///         <c>Add(string)</c> containers — <c>layer-stack-row</c>, <c>layer-stack-fill-channel</c> —
///         answer to no control rule and stay tags, which is what <c>TexturingTheme.vcss</c> selects
///         on.
///     </para>
///     <para>
///         ⚠ <b>The frame is <c>LayerStackChrome.vxml</c> and the rows are still built here</b> —
///         <a href="https://github.com/Rikarin/Vixen/issues/881">#881</a>, and the split is where it
///         is for a reason rather than for want of time. Doc 36 § P4 makes markup the authoring
///         path; what the two columns, the binding row, the actions row and the diagnostics block
///         have in common is that they are a <em>fixed tree</em>, which is exactly what markup
///         expresses. A row is not.
///     </para>
///     <para>
///         ⚠ <b>The three paragraphs below are no longer prose: <c>LayerRowKeyTests</c> runs both
///         candidate keys and the answer against a real document edited through this panel's own
///         slider, so each is a test that goes red if it stops being true.</b> ⚠ And it turned up a
///         fourth blocker nothing had written down — <c>UiElement.Text</c> on an element with
///         children throws, and an <c>Effect</c> answers a throw by <em>suspending itself</em>, so a row that bound its own <c>Text</c> renders once,
///         keeps the string, and follows nothing thereafter with no diagnostic
///         (<a href="https://github.com/Rikarin/Vixen/issues/1109">#1109</a>). Every row shape here
///         is a container, so the port owes a label element per bound string.
///     </para>
///     <para>
///         ⚠ <b>The rows were said to be a model change before a markup change, four times, and that
///         turned out to be a statement about one <em>technique</em> rather than about the rows.</b>
///         Three row kinds are markup now — <c>LayerRowView</c>, <c>FillRowView</c>,
///         <c>FilterRowView</c> — and none of them is a <c>@for</c> region: they are components the
///         walk below builds, which is markup without being reactive. The paragraph that follows is
///         kept because it is <em>true of a region</em> and is what the next person reaching for one
///         will need.
///     </para>
///     <para>
///         <c>BuildContext.For</c> matches a key, <em>reuses the region and does not re-run
///         the body</em>, so every binding inside a row closes over the item as it was when that key
///         first appeared. Keying on <c>LayerAsset.Id</c> — the only stable identity a layer has,
///         and what the issue asks for — therefore needs the row to take something whose
///         <em>contents</em> notify, which is a <c>Signal&lt;LayerAsset&gt;</c> per row;
///         <c>LayerAsset</c> holds no signal, so a reorder would keep the row and show the previous
///         layer's values. Keying on the layer <em>value</em> instead — <c>StatisticsView</c>'s
///         answer for an immutable snapshot — is not available either: a row carries a slider and a
///         dropdown an artist is holding, and a value key rebuilds the row on the keystroke that
///         changed it.
///     </para>
///     <para>
///         ⚠ <b>Which is the property <see cref="Shape" /> already buys, and is why the port cannot
///         simply drop it.</b> <c>Shape</c> deliberately omits <c>Mask.Source</c>, <c>Fill</c>,
///         <c>Filter</c>, <c>FilterNode</c> and <c>Projection</c> so that changing one of them
///         re-reads the row rather than replacing it — a control that tore its own tree down from
///         inside its own <c>SelectionChanged</c> is a thing that happened here. A <c>@for</c> over
///         the layers is the easy half; the hard half is that the <c>bindings</c> closures a row adds
///         are doing what a signal graph would do, and every one has to become a read whose identity
///         survives.
///     </para>
///     <para>
///         ⚠ <b>And one cost the port has to buy, measured rather than assumed.</b> A markup binding
///         is an <c>Effect</c>, and an effect never runs on the write — it queues, and
///         <c>EffectScheduler.Flush</c> runs it. <see cref="Show" /> is called from
///         <c>TexturingModule</c> on every evaluation and its result is read synchronously by six
///         test files and by <see cref="Status" />, so moving the rows into markup moves the whole
///         panel from synchronous to frame-deferred. ⚠ <b>Six is derived rather than remembered</b>,
///         and re-derived 2026-09-09: thirteen test files open this panel and seven of them ask for
///         a frame, so the six that do not are the ones a deferred row would surprise. That is survivable —
///         <c>UiDocument.Update</c> drains the queue before its first pass — but it is a change to
///         what this class promises its callers, and it is why only the message block was moved.
///     </para>
///     <para>
///         Both directions of the flex are set explicitly in the sheet, because
///         <c>flex-direction</c> is <c>row</c> by CSS default and <c>flex-grow</c> is not — a
///         container that set neither is full width and no height, which is the shape of "the panel
///         is blank".
///     </para>
/// </remarks>
sealed class LayerStackView : IDisposable {
    /// <summary>What the legend under the rows says about an unrestricted layer.</summary>
    /// <remarks>
    ///     ⚠ <b>The one defaulting decision in <c>.vxlayers</c> a reader could get wrong, said where
    ///     an author is looking at the tick boxes it is about.</b> <see cref="LayerAsset.Channels" />
    ///     empty means <em>every</em> channel, and its own remarks argue why: the alternative makes a
    ///     channel added to the texture set later invisible to every layer that already exists. That
    ///     argument lives in a source file. This sentence is the same fact in the panel, next to a row
    ///     of ticks that are all on and a stored list that is empty — which is exactly the state a
    ///     person would otherwise read as "this layer writes nothing".
    /// </remarks>
    public const string ChannelLegend =
        "Every channel ticked means the layer is unrestricted — it also writes a channel the set gains later. "
        + "A layer that should write nothing is switched off instead, so the last tick cannot be cleared.";

    /// <summary>What an unbound stack's binding row says, and it is the state every new one is in.</summary>
    /// <remarks>
    ///     ⚠ <b>An option rather than an empty <see cref="Select" />.</b> A dropdown whose first
    ///     entry is the first model in the project would make "no mesh" unreachable the moment a
    ///     project has one, and unbinding is a real gesture — a stack pointed at the wrong model has
    ///     to be able to stop being pointed at it.
    /// </remarks>
    public const string NoMesh = "(none)";

    /// <summary>What the part picker calls "every mesh in the model".</summary>
    /// <remarks>
    ///     ⚠ <b>An option and not an empty entry, for <see cref="NoMesh" />'s reason and one more.</b>
    ///     Every mesh is the <em>default</em> state of a set rather than an absence — a stack with one
    ///     texture set wants the whole model, and a picker whose first entry was the model's first
    ///     mesh would narrow every stack the moment its model was imported.
    /// </remarks>
    public const string EveryMesh = "(all)";

    /// <summary>The panel's tree, as markup. See <c>LayerStackChrome.vxml</c>.</summary>
    readonly LayerStackChrome chrome;

    readonly UiElement meshStatus;
    readonly UiElement root;
    readonly UiElement rows;
    readonly UiElement status;
    readonly UiElement title;

    /// <summary>The mesh picker. Its options are the project's models, and they are re-read per stack.</summary>
    readonly Select model;

    /// <summary>Which of the model's meshes the shown set is narrowed to.</summary>
    /// <remarks>
    ///     ⚠ <b>The shown set is now <see cref="SetName" />'s and no longer <c>Sets[0]</c></b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>. That is what #941 called
    ///     this control gated on: a per-set picker on a panel that showed one set was a control for a
    ///     set nobody chose. It was here anyway because the alternative was worse and was what was
    ///     shipping — the only way to narrow a set was to edit the <c>.vxlayers</c> by hand, and a
    ///     two-set stack that has not been narrowed lets <c>Body</c> be painted anywhere <c>Head</c>
    ///     has surface.
    /// </remarks>
    readonly Select part;

    /// <summary>Which texture set the panel is working on.</summary>
    /// <remarks>
    ///     ⚠ <b>One chooser rather than a pin per pane, which is
    ///     <a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>'s decision.</b> Its options
    ///     are the stack's sets and it is disabled for a stack with one, which is every stack that
    ///     exists today — so the ordinary case gains a control that says what it is already doing.
    /// </remarks>
    readonly Select sets;

    /// <summary>Which of the four kinds the <em>Add layer</em> button makes.</summary>
    /// <remarks>
    ///     ⚠ <b>A picker beside the button rather than four buttons, because the kinds are not four
    ///     gestures.</b> Doc 48 § D10's four are one decision an artist makes once per layer, and a
    ///     row of four buttons would spend the width the binding row above it already spends. It is
    ///     also the shape that survives a fifth kind.
    /// </remarks>
    readonly Select addKind;

    /// <summary>The brush this panel drives, or null in a host that never paints.</summary>
    /// <remarks>
    ///     ⚠ <b>Held, which it was not before.</b> The constructor used to hand it straight to
    ///     <see cref="PaintBrushInspector" /> and forget it; selecting a layer is a decision made in
    ///     these rows and read by the paint pane, and <c>PaintTool.LayerId</c> is where the two meet
    ///     — <a href="https://github.com/Rikarin/Vixen/issues/910">#910</a>.
    /// </remarks>
    readonly PaintTool? tool;

    /// <summary>What each row re-reads when the document changed without changing shape.</summary>
    readonly List<Action> bindings = [];

    /// <summary>What a row still owes once the elements a markup region makes actually exist.</summary>
    /// <remarks>
    ///     ⚠ <b>A second phase of the build rather than a nicety, and it is what a markup row costs.</b>
    ///     A <c>@for</c> or an <c>@if</c> in a <c>.vxml</c> is an <c>Effect</c>, and an effect never
    ///     runs on the write — so the check boxes <c>LayerRowView</c>'s channel loop declares do not
    ///     exist while <see cref="Build" /> is walking the layers. Anything that needs one goes here,
    ///     and <see cref="Build" /> drains the document's queue and runs it before
    ///     <see cref="Restate" /> reads a single row. Left as one phase the ticks would simply be
    ///     absent from every closure, which no assertion about the row's shape could see.
    /// </remarks>
    readonly List<Action> pending = [];

    /// <summary>What the rows currently on the screen were built for. See <see cref="Shape" />.</summary>
    string shape = "";

    /// <summary>Which document the rows on the screen were built against.</summary>
    /// <remarks>
    ///     ⚠ <b>Held beside the shape, because a shape is not an identity.</b> Every row's controls
    ///     close over the document they were built for, and two stacks made from
    ///     <c>LayerStackDocument.Starter</c> have the same layer ids, the same kinds and the same
    ///     channels — so opening the second one after the first would match on shape, keep the rows,
    ///     and leave every control editing the file that is no longer open.
    /// </remarks>
    LayerStackDocument? built;

    /// <summary>Which document the mesh picker's options were filled for.</summary>
    LayerStackDocument? bound;

    /// <summary>What the pickers were last told the binding is, so a rebind is not per keystroke.</summary>
    /// <remarks>
    ///     ⚠ <b>The model <em>and</em> the shown set's mesh, because there are two pickers.</b> An
    ///     undo of a narrowing changes neither the document reference nor the model path, and a gate
    ///     that watched only the model would leave the part picker showing the value the artist had
    ///     just taken back — the state <c>LayerStackBindingTests</c> describes for every other
    ///     control on this panel, which is a separate finding and not one to add to.
    /// </remarks>
    string boundModel = "";

    /// <summary>Which <c>LayerStackDocument.ModelsRevision</c> the picker's options were filled at.</summary>
    /// <remarks>
    ///     ⚠ <b>This view's own copy, because a notification with one reader is a notification with
    ///     one reader</b> — <a href="https://github.com/Rikarin/Vixen/issues/1006">#1006</a>. The
    ///     document used to carry a boolean that this refill cleared, so a second consumer of the
    ///     same "a model changed" would see nothing whenever this one read it first. Nothing clears a
    ///     number; zero here against a document that starts at one is what refills a picker once
    ///     before anything has happened.
    /// </remarks>
    int boundModels;

    /// <summary>The last picture, so an edit this view made can redraw without one being handed back.</summary>
    LayerStackPicture? shown;

    /// <summary>Which document the preview was last framed for. See <see cref="Show" />.</summary>
    LayerStackDocument? framed;

    /// <summary>The width it was framed at.</summary>
    int framedWidth;

    /// <summary>The height it was framed at.</summary>
    int framedHeight;

    /// <summary>Whether <c>ImageView.Fit</c> had a box to fit against when it was last asked.</summary>
    /// <remarks>
    ///     ⚠ <b>The answer and not the call, which is the half that stops "frame once" becoming
    ///     "never frame".</b> <c>Fit</c> returns false before the first layout — and a panel's first
    ///     <see cref="Show" /> runs before it has been laid out — so a view that recorded the attempt
    ///     rather than its result would open every stack at whatever zoom nothing set.
    /// </remarks>
    bool fitted;

    /// <summary>What makes an undo taken anywhere else reach these rows. See <see cref="Watch" />.</summary>
    Effect? watch;

    /// <summary>Which document <see cref="watch" /> is subscribed to.</summary>
    LayerStackDocument? watched;

    /// <summary>The undo depth these rows were last drawn at.</summary>
    /// <remarks>
    ///     ⚠ <b>What keeps the panel's own edits out of its own subscription.</b> A row's edit
    ///     executes a command and then refreshes on the spot, so the effect that wakes on the same
    ///     write would recompile the stack a second time on the next frame. Recording the depth on
    ///     the way through <see cref="Show" /> is what makes the effect fire for changes this view
    ///     did <em>not</em> make — which is the whole of what it is for.
    /// </remarks>
    int watchedDepth;

    /// <summary>Whether a control is being written to rather than read from.</summary>
    /// <remarks>
    ///     ⚠ <b>Every control here raises its change event however the change happened</b> — which is
    ///     the right design and is stated on <c>ToggleBase.CheckedChanged</c> — so a refresh that puts
    ///     the document's value into a slider would otherwise look exactly like an artist dragging it
    ///     and push a command undoing the undo that caused the refresh.
    /// </remarks>
    bool writing;

    /// <summary>Builds the view into a host element.</summary>
    /// <param name="host">Where it goes. A <c>DockPanel</c>, or anything inside one.</param>
    /// <param name="tool">
    ///     The brush to give a column to, or <see langword="null" /> for no brush inspector — which
    ///     is what a host that never paints wants.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="host" /> is null.</exception>
    public LayerStackView(UiElement host, PaintTool? tool = null) {
        ArgumentNullException.ThrowIfNull(host);

        DockPanel.Fills(host);

        // ⚠ Before the first element, because a sheet loaded after them still restyles them and the
        // ordering only looks harmless — #881. `TexturingTheme` is guarded against a second load
        // into the same document, which this constructor really does provoke: a panel's factory
        // re-runs whenever the workspace relays out.
        TexturingTheme.Install(host.Document);

        // ⚠ The tree is `LayerStackChrome.vxml` now — #881's second half, for the part of the panel
        // that is a fixed tree. The element flavour of a markup component *is* the `layer-stack`
        // element rather than something mounted inside one, so this line replaces
        // `host.Add("layer-stack")` and nothing below it moved a level: the stylesheet still selects
        // `layer-stack`, `PaintBrushInspector` still builds a third column into it, and
        // `layer-stack-empty` is still its sibling. What the markup builds is assigned to its `ref`s
        // by the time `Add` returns, because `@inherits` compiles the body into `OnCreated`.
        chrome = host.Add<LayerStackChrome>();
        root = chrome;

        this.tool = tool;

        sets = chrome.Sets;
        model = chrome.Model;
        part = chrome.Part;
        meshStatus = chrome.MeshStatus;
        addKind = chrome.AddKind;
        rows = chrome.Rows;
        title = chrome.Title;
        status = chrome.Status;

        Channels = chrome.Channels;
        Preview = chrome.Preview;

        // ⚠ The handlers stay here and are deliberately not `on:` attributes in the markup. Every
        // one of them closes over this view's document, its selection and its `writing` guard — the
        // state the markup has no view of — and a panel whose tree is markup and whose behaviour is
        // C# is the split `PluginManagerView` and `StatisticsView` already make.
        sets.SelectionChanged += (_, value) => ChooseSet(value ?? "");
        model.SelectionChanged += (_, value) => Bind(value ?? "");
        part.SelectionChanged += (_, value) => Narrow(value ?? "");

        foreach (var value in Enum.GetValues<LayerKind>()) {
            addKind.AddOption(value.ToString());
        }

        addKind.Value = LayerKind.Fill.ToString();

        chrome.AddButton.Clicked += _ => AddLayer();

        // ⚠ Written from here rather than spelled in the markup, because it is a `const` two tests
        // compare against: a copy of the sentence in the `.vxml` would be a second source of truth
        // that only a reader could tell had drifted.
        chrome.Legend.Text = ChannelLegend;

        // ⚠ Here rather than as a literal in the markup, for the reason that file gives on the
        // heading itself: markup text is a child element and an element with children may not also
        // carry `Text`, so a default written in the `.vxml` would make the first `Show` throw.
        title.Text = "Result";

        // ⚠ A third column and not a section of the preview one, and it is last so that the picture
        // keeps its width when the brush is not there. `PaintBrushInspector` builds its own root
        // into this element, which is why this file gains three lines rather than a panel.
        Brush = tool is null ? null : new PaintBrushInspector(root, tool);

        // A sibling of the layout rather than a child of it, because the empty state is shown by
        // hiding that layout — a message inside the thing being hidden is a message nobody sees.
        Empty = host.Add("layer-stack-empty");
        Empty.Text = "No layer stack is open. Select a .vxlayers in the Project panel and run Open Layer Stack.";
    }

    /// <summary>Stops following the open document's undo stack.</summary>
    /// <remarks>
    ///     ⚠ <b>What a caller that <em>replaces</em> this view owes it, and nothing else.</b> The
    ///     elements go with the panel they were built into; the one thing that outlives them is the
    ///     edge from <c>CommandStack.Depth</c> into <see cref="Watch" />'s effect, which keeps this
    ///     view — and therefore every row's closure — alive for as long as the document is open.
    ///     <c>TexturingModule</c>'s panel factory re-runs on every workspace relayout, so that is the
    ///     caller with a previous view to end. A view built by
    ///     <see cref="LayerStackEditorFactory" /> has no such caller and does not need one: its
    ///     effect stops reading the signal once the root has left the tree, which drops the last edge.
    /// </remarks>
    public void Dispose() {
        watch?.Dispose();

        watch = null;
        watched = null;
    }

    /// <summary>Everything this view built, for a caller that has to hand a root back.</summary>
    public UiElement Root => root;

    /// <summary>The pane the baked map is shown in.</summary>
    public ImageView Preview { get; }

    /// <summary>The channel and transfer-function pickers over that pane.</summary>
    /// <remarks>
    ///     ⚠ <b>The panel that most needs it, because a layer stack bakes a <em>set</em> of maps and
    ///     four of them are not colours</b> —
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1012">#1012</a>. A roughness shown
    ///     through an sRGB curve and a normal map shown through one look like different bugs and are
    ///     the same misreading; the strip is what settles it without leaving the panel.
    /// </remarks>
    public ImageViewBar Channels { get; }

    /// <summary>What is shown when no stack is open.</summary>
    public UiElement Empty { get; }

    /// <summary>The brush's column, or <see langword="null" /> when this host paints nothing.</summary>
    public PaintBrushInspector? Brush { get; }

    /// <summary>The stack currently shown.</summary>
    public LayerStackDocument? Document { get; private set; }

    /// <summary>Which texture set the panel is showing, or empty for "whichever is first".</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/927">#927</a>, and it is one
    ///         decision rather than seven edits.</b> Sets are independent — a set is a material slot
    ///         with its own atlas, its own channels and its own <c>.vxpaint</c> files — so the editor
    ///         works on one at a time and everything reads the same choice.
    ///         <c>LayerStackEdit.SetFor</c> is that rule; this is the panel's copy of the answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The paint half is wired now, and this is the panel's copy rather than the
    ///         durable one.</b> <c>ChooseSet</c> writes <see cref="LayerStackDocument.PaintSet" />
    ///         and <c>PaintSurface.Open</c> resolves it, so a row of any set can be selected and the
    ///         stroke lands where the artist is looking — the disarm and the sentence that used to
    ///         stand here for that are gone. The choice lives on the <em>document</em> and not on
    ///         <c>PaintTool</c>, which is what this remark used to predict: a set name means
    ///         something only inside one stack, and the tool outlives documents.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why an empty one is recovered from the document at the next show.</b>
    ///         This panel's factory re-runs on any workspace relayout, and a copy that reset while
    ///         the durable one did not is two answers to one question.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What still takes <c>Sets[0]</c> is <c>TexturingModule.Mesh</c>, and it is a
    ///         finding rather than a decision</b> — that file belongs to another slice. A stack whose
    ///         chosen set narrows to a different mesh gets the first set's coverage map, so a stroke
    ///         outside the shown set's islands is accepted or refused by the wrong geometry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty is a real state and not "not yet initialised".</b> It means the panel has
    ///         made no choice, which is what a stack with one set stays in forever — and
    ///         <c>SetFor</c>'s fallback is what makes that a no-op rather than a special case at
    ///         every reader.
    ///     </para>
    /// </remarks>
    public string SetName { get; private set; } = "";

    /// <summary>What the status line under the preview says.</summary>
    public string Status => status.Text ?? string.Empty;

    /// <summary>Everything the compile had to say, in the order it said it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This one <em>is</em> the derivation the panel draws from, which is why it stayed
    ///         when <c>Rows</c> went</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/898">#898</a> reported the two as the
    ///         same shape and they are not: <see cref="Show" /> assigns this and then adds one
    ///         element per entry of it, so there is exactly one thing that decides what a message
    ///         says. <c>Rows</c> was assigned from a second walk that nothing read and that had
    ///         already drifted — it listed layers only, while the tree the panel builds also has a
    ///         row per mask entry and per mask effect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This is the surface <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a>
    ///         found missing, and the argument it was missing from is <c>TG0022</c>'s.</b> The
    ///         terminus rescale was chosen over a silent one because "it is said" — and nothing said
    ///         it: no production type rendered a <c>NodeDiagnostic</c>, and the one consumer that
    ///         read a list of them kept the errors. So a warning reached an author only in the sense
    ///         that a value existed in a record. A diagnostic an author cannot see is not a
    ///         diagnostic.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is where the <em>file's</em> own failures are said too</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/983">#983</a>. A picture is one
    ///         attempt at the map and a load failure is a fact about the bytes, so the second had no
    ///         surface at all until it was folded in here. The two-argument <c>Describe</c> says why
    ///         it leads rather than trails, and why the status line was the wrong home for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is the signal the block is drawn from, read back — not a second copy of the
    ///         answer.</b> <see cref="Show" /> assigns <c>LayerStackChrome.Messages</c> and the
    ///         <c>@for</c> in the markup is the only thing that draws it, so the paragraph above
    ///         stays true through the port: a field assigned beside the signal would be exactly the
    ///         shape #898 found in <c>Rows</c>.
    ///     </para>
    /// </remarks>
    public IReadOnlyList<string> Messages => chrome.Messages.Value;

    /// <summary>Told after an edit, so whoever owns the evaluator can re-bake the map.</summary>
    /// <remarks>
    ///     ⚠ <b>A callback rather than a re-compile here, because this view has no evaluator and must
    ///     not acquire one.</b> <c>TexturingModule</c> holds the <c>LayerStackPreview</c> — two of
    ///     them over one device would be two pipeline caches, which is that field's own stated reason
    ///     — so an edit made here can redraw the rows on its own and cannot redraw the picture. When
    ///     nothing is subscribed the rows still update and the pane keeps the map it had, which is the
    ///     honest state for a tab opened by a double-click: <c>LayerStackEditorFactory</c> builds a
    ///     view with no graphics at all.
    /// </remarks>
    public Action? Edited { get; set; }

    /// <summary>How many times this view has walked the set to fill an anchor picker.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A count of work rather than a duration, because
    ///         <a href="https://github.com/Rikarin/Vixen/issues/979">#979</a> is a per-frame walk and
    ///         a millisecond budget on a laptop is this repository's largest flake source.</b> The
    ///         property under test is that a row walks the tree <em>once</em>, whatever an opacity
    ///         drag does to the refresh count.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it cannot see is a picker that stopped being filled at all</b> — an anchor
    ///         row that built no options would leave this at zero and read as a perfect result. The
    ///         test that reads it therefore asserts the options as well, which is the half that says
    ///         the work happened.
    ///     </para>
    /// </remarks>
    internal int AnchorWalks { get; private set; }

    /// <summary>Which layer the artist is working on, or <see langword="null" /> for none.</summary>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/910">#910</a>: nothing in this
    ///         plugin had one.</b> The 2D paint view therefore had to answer "which layer" itself,
    ///         and answered with <c>PaintTool.LayerId</c> defaulting to empty — <em>the first paint
    ///         layer in composite order</em>, which is right for a stack with one and is no answer at
    ///         all for a stack with two. A row here is now the writer and the tool is the mirror.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Null is not "the first one", and the difference is deliberate.</b> An empty
    ///         <c>LayerId</c> means the brush takes the first paint layer, which is the behaviour a
    ///         stack has before anybody has chosen — so a panel that selected the first row on open
    ///         would look identical and would silently make the artist's first stroke a decision
    ///         somebody else made.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Selecting a layer that is not a <see cref="LayerKind.Paint" /> layer is allowed,
    ///         and the brush then refuses by name.</b> A fill layer has no canvas; <c>PaintSurface</c>
    ///         answers "the set 'X' has no paint layer with the id 'Y'", which is what an artist who
    ///         selected a fill and reached for the brush needs to read. Silently painting somewhere
    ///         else is the defect the whole issue is about.
    ///     </para>
    /// </remarks>
    public LayerPath? Selected { get; private set; }

    /// <summary>Told when the selected layer changed, so a paint pane can re-aim.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Edited" /> because a selection is not an edit.</b> It puts
    ///     nothing on the undo stack and does not make the document dirty — an artist who clicked a
    ///     row and pressed undo means to undo the last thing they <em>changed</em>. Raising
    ///     <see cref="Edited" /> for it would push the picture through a recompile per click and,
    ///     worse, would make a selection look like a reason to save the file.
    /// </remarks>
    public Action? SelectionChanged { get; set; }

    /// <summary>Puts a stack in the panel, or takes the last one out.</summary>
    /// <param name="document">The stack, or <see langword="null" /> for none.</param>
    /// <param name="picture">What evaluating it produced, or <see langword="null" /> for nothing.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null is an ordinary state.</b> A panel's factory runs when the panel is opened,
    ///         which for a restored layout is before anybody has opened a stack — so a view that
    ///         demanded one would be a panel the editor could not show at start-up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The rows are rebuilt only when their <em>shape</em> changed, and that is a
    ///         correctness property rather than a saving.</b> A refresh runs on every evaluation and
    ///         on every edit, and rebuilding unconditionally destroys the control the artist is
    ///         holding — an opacity slider stops mid-drag, because the element under the captured
    ///         pointer has been removed and replaced by a copy of itself. What every refresh does
    ///         instead is re-read each row's values from the document, which is also what makes an
    ///         undo show up on a tick box that nothing rebuilt.
    ///     </para>
    /// </remarks>
    public void Show(LayerStackDocument? document, LayerStackPicture? picture = null) {
        Present(document, picture);

        // ⚠ **The blocker #881's remaining half is written against, closed here rather than
        // measured a fourth time.** A markup binding is an `Effect`, and
        // `Core/Vixen.Ui.Reactive/Effect.cs` is explicit that an effect never runs on the write — it
        // queues, and `EffectScheduler.Flush` runs it at the point in the frame the UI system chose.
        // So the moment any part of this panel became markup, `Show` stopped being a call that
        // leaves the panel showing what it was given: it left it showing the previous frame until
        // something else drained the queue. Six test files read this tree synchronously, and
        // `LayerStackPanelTests.Lines` had grown a flush of its own to cover for it — a cost being
        // paid once per reader instead of once at the source, and one every future reader would
        // have had to rediscover.
        //
        // ⚠ **Re-entrant by construction, so this is safe inside a frame.** `Flush` returns
        // immediately when one is already running and defers itself out of a `ReactiveGraph.Batch`,
        // so the drain `UiDocument.Update` makes on the way past is untouched and a `Show` reached
        // from inside one is a no-op here rather than a nested drain.
        //
        // ⚠ **Not `Update`, which would lay the document out.** What is owed is that the elements
        // the statements above asked for exist; a pass would also restyle and re-measure the whole
        // window, once per frame of an opacity drag.
        root.Document.Effects.Flush();
    }

    /// <summary>Everything <see cref="Show" /> does before the effect queue is drained.</summary>
    /// <param name="document">The stack, or <see langword="null" /> for none.</param>
    /// <param name="picture">What evaluating it produced, or <see langword="null" /> for nothing.</param>
    /// <remarks>
    ///     Split out for the early returns rather than for tidiness: this method leaves through
    ///     three of them, and a flush written at the bottom of it would run on one path in three.
    /// </remarks>
    void Present(LayerStackDocument? document, LayerStackPicture? picture) {
        Document = document;
        shown = picture;

        Watch(document);

        Empty.SetStyle("display", document is null ? "flex" : "none");
        root.SetStyle("display", document is null ? "none" : "flex");

        // ⚠ One assignment where a remove-every-child-then-add loop and a `display` write used to
        // be. The block is a `@for` in `LayerStackChrome.vxml` keyed on the line and its position,
        // and hidden by a class rather than by an inline style — so the rule and the toggle layer
        // instead of arguing. ⚠ The cost is that the rows appear at the next `EffectScheduler.Flush`
        // rather than inside this call: a markup binding is an `Effect` and an effect never runs on
        // the write. `UiDocument.Update` drains the queue before its first pass, so a frame is
        // enough; a test that reads the block without one has to ask for the flush, which is what
        // the finders in `LayerStackPanelTests` and `LayerStackEditingTests` now do.
        chrome.Messages.Value = Describe(document, picture);

        if (document is null) {
            Clear();

            title.Text = "Result";
            status.Text = "";

            Preview.Image = 0;
            Preview.ImageWidth = 0;
            Preview.ImageHeight = 0;

            // Taking the stack out of the panel ends the picture the zoom was about, so putting one
            // back in — the same document included — is a new subject and is framed afresh.
            framed = null;

            return;
        }

        // ⚠ Adopted from the document whenever the document changes, rather than recovered only
        // when the panel's copy is empty — #927. Two things sit behind that.
        //
        // The panel's factory re-runs whenever the workspace relays out, so a fresh view that
        // started at "" every time would put the artist back on the first set while the *brush*
        // stayed on the one they chose. That is the case a recovery covers.
        //
        // ⚠ What a recovery does *not* cover is a second stack opened into the same panel. This
        // view outlives the document it shows, so a non-empty `SetName` carried across the switch is
        // the previous stack's choice pointed at this one — and because two stacks made from
        // `LayerStackDocument.Starter` carry the same set names, `SetFor` resolves it rather than
        // refusing it. The picker would then read "Body" while `PaintSet` was still "" and the
        // stroke landed in the first set: #927's exact mis-aim, in the one window the `OtherSet`
        // disarm used to cover, and with no way to correct it from the picker because choosing the
        // set it already shows returns at the equality above in `ChooseSet`.
        if (!ReferenceEquals(built, document)) {
            SetName = document.PaintSet;
        }

        var wanted = Shape(document, SetName);

        if (!ReferenceEquals(built, document) || !string.Equals(wanted, shape, StringComparison.Ordinal)) {
            Build(document);

            built = document;
            shape = wanted;
        }

        // ⚠ Only when the stack or its binding changed, and not on every show. A show runs on every
        // edit, and refilling the picker walks the whole asset index — a project's worth of entries
        // per keystroke on a slider. The picker is also a control an artist can be holding, and
        // `ClearOptions` under an open dropdown is the same defect the shape comparison above exists
        // to prevent one level up.
        if (!ReferenceEquals(bound, document)
            || boundModels != document.ModelsRevision
            || !string.Equals(boundModel, Binding(document, SetName), StringComparison.Ordinal)) {
            Rebind(document);

            bound = document;
            boundModel = Binding(document, SetName);

            // ⚠ Recorded here and not cleared there — #954, and #1006 for why it is a number this
            // view copies rather than a flag it clears. The document is told a model file moved by
            // `ExternalEdits`, on the frame, once per drained change; acting on it at the
            // notification would mean a stack whose panel is closed forgets what happened before it
            // is opened, and clearing it here would mean whichever consumer read it first was the
            // only one that ever heard.
            boundModels = document.ModelsRevision;
        }

        Restate();

        title.Text = "Result — " + Resolution(document);
        status.Text = picture?.Status ?? "";

        // ⚠ The extent comes from the picture when there is one and from the *stack* when there is
        // not, so the zoom and the pointer readout keep meaning texels through a failed compile.
        // `ImageView.Image` is a number the renderer resolves; zero draws the chequerboard, which is
        // the honest picture of a stack this host could not bake.
        Preview.Image = picture?.Image?.Image ?? 0;
        Preview.ImageWidth = picture?.Width ?? document.Document.BaseWidth;
        Preview.ImageHeight = picture?.Height ?? document.Document.BaseHeight;

        // ⚠ **Framed when the subject changes and not on every refresh** — #979, and it is #957's
        // finding in the panel #957 did not touch. `Fit` overwrites `Zoom` and `Pan` outright and
        // this method runs on every edit — once per frame of an opacity drag — so fitting here
        // unconditionally threw away the corner an artist had zoomed into at exactly the moment
        // they were looking at it. A different stack, or the same one at a different extent, is a
        // different picture and is framed; the same stack recompiled is left where they put it.
        if (fitted
            && ReferenceEquals(framed, document)
            && framedWidth == Preview.ImageWidth
            && framedHeight == Preview.ImageHeight) {
            return;
        }

        framed = document;
        framedWidth = Preview.ImageWidth;
        framedHeight = Preview.ImageHeight;
        fitted = Preview.Fit();
    }

    /// <summary>Everything an open stack has to say about itself, as lines.</summary>
    /// <param name="document">The stack, or null when none is open.</param>
    /// <param name="picture">The latest attempt at its map, when there was one.</param>
    /// <returns>The file's own load failures first, then everything the compile had to say.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The load diagnostics are read here and nowhere else, which is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/983">#983</a>.</b>
    ///         <c>LayerStackDocument.LoadDiagnostics</c> exists because <c>LayerStackYaml.Read</c>
    ///         <em>refuses</em> a blend mode this build cannot spell rather than defaulting it — the
    ///         alternative to the report is not a wrong picture, it is no explanation for an empty
    ///         panel — and until this overload nothing in production asked for it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here rather than on the status line, because the two answer different
    ///         questions.</b> <c>picture.Status</c> is recomputed per evaluation and says why there
    ///         is no map; a load failure is a fact about the <em>file</em> that survives every
    ///         refresh, so a sentence written into the status line is one the next edit erases.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And they lead, because a file that did not parse makes every compile message
    ///         downstream of it.</b> A stack whose bytes were refused compiles as a stack with
    ///         nothing in it, so the diagnostics under this line are about a document the artist
    ///         never wrote.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Describe(LayerStackDocument? document, LayerStackPicture? picture) {
        List<string> lines = [];

        if (document is not null) {
            foreach (var diagnostic in document.LoadDiagnostics) {
                lines.Add($"{Severity(diagnostic.Severity)} — {diagnostic.Id}: {diagnostic.Message}");
            }
        }

        if (picture is not null) {
            lines.AddRange(Describe(picture));
        }

        return lines;
    }

    /// <summary>Everything one attempt at the map had to say, as lines.</summary>
    /// <param name="picture">The attempt.</param>
    /// <returns>One line per problem and per diagnostic, layers first.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="picture" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both lists and every severity, which is the whole of
    ///         <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a>.</b> The pane's status
    ///         line answers "why is there no map" and therefore reads errors only; a warning is by
    ///         definition a thing that did not stop the map, so filtering here as well left it with
    ///         nowhere at all to be shown.
    ///     </para>
    ///     <para>
    ///         <b>Layers first because that is the order an artist can act in.</b> A
    ///         <c>LayerStackProblem</c> names a row in the list directly above this one; a
    ///         <c>NodeDiagnostic</c> names a node in the graph those rows explode into, which is a
    ///         graph nobody has opened.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A line that reads exactly the same as one already listed is dropped, and finding
    ///         out why is <a href="https://github.com/Rikarin/Vixen/issues/842">#842</a>.</b>
    ///         <c>LayerStackGraph</c> walks a layer once per channel the texture set writes, so one
    ///         mistyped filter setting on one layer arrives here <em>seven times</em> — and because
    ///         the message names neither the channel nor anything else that differs, the seven are
    ///         character-for-character identical. Two identical sentences tell a reader nothing the
    ///         first did not. The multiplicity is real and the builder is where it should be said.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>But the count is said here, because a collapsed line was silently one of N —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/870">#870</a>.</b> Two layers each
    ///         carrying the same mistyped mask effect raise fourteen diagnostics from fourteen
    ///         distinct nodes, all character-identical, and the reader saw one sentence with nothing
    ///         to say whether one mistake or two were behind it. ⚠ <b>Naming
    ///         <c>NodeDiagnostic.Node</c> on the line — which is what #870 proposed — would undo
    ///         #842 rather than fix this</b>: the seven copies of one mistake are seven different
    ///         nodes, so it turns one mistyped setting into seven lines.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the line names the <em>layer</em>, out of
    ///         <see cref="LayerStackPicture.Layers" /> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/880">#880</a>, which is what actually
    ///         closes #870 and the readable half of #842.</b> The dedupe key is the rendered line, so
    ///         adding the layer to it collapses one layer's seven per-channel copies into one
    ///         sentence <em>and</em> keeps two layers' identical mistakes two sentences — the two
    ///         things naming the node could not do at once. A diagnostic whose node is in no layer —
    ///         a channel's base constant, its <c>Output</c> — keeps the unnamed form, because "no
    ///         layer" is a true answer and inventing one would be a row nobody can select.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Describe(LayerStackPicture picture) {
        ArgumentNullException.ThrowIfNull(picture);

        var lines = new List<string>(picture.Problems.Length + picture.Diagnostics.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var problem in picture.Problems) {
            Add($"{Severity(problem.Severity)} — layer '{problem.Layer}': {problem.Message}");
        }

        // Two passes, because the count belongs on the first occurrence rather than after the last:
        // a reader scanning the list top to bottom has to see the multiplicity on the line they read.
        Dictionary<string, HashSet<NodeId>> raisers = new(StringComparer.Ordinal);
        List<string> ordered = [];

        foreach (var diagnostic in picture.Diagnostics) {
            var line = picture.Layers.TryGetValue(diagnostic.Node, out var layer)
                ? $"{Severity(diagnostic.Severity)} — layer '{layer}' {diagnostic.Id}: {diagnostic.Message}"
                : $"{Severity(diagnostic.Severity)} — {diagnostic.Id}: {diagnostic.Message}";

            if (!raisers.TryGetValue(line, out var nodes)) {
                raisers[line] = nodes = [];
                ordered.Add(line);
            }

            nodes.Add(diagnostic.Node);
        }

        foreach (var line in ordered) {
            var nodes = raisers[line].Count;

            Add(nodes > 1 ? $"{line} · {nodes} nodes in the exploded graph" : line);
        }

        return lines;

        void Add(string line) {
            if (seen.Add(line)) {
                lines.Add(line);
            }
        }
    }

    /// <summary>How a severity reads at the head of a line.</summary>
    /// <remarks>
    ///     Spelled rather than <c>ToString</c>'d, because <c>NodeSeverity.Warning</c>'s name is the
    ///     word and a rename of the member would silently change what an artist reads.
    /// </remarks>
    static string Severity(NodeSeverity severity) => severity == NodeSeverity.Error ? "Error" : "Warning";

    /// <summary>The resolution readout, as the pane titles it.</summary>
    /// <param name="document">The stack.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    public static string Resolution(LayerStackDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{document.Document.BaseWidth} × {document.Document.BaseHeight}"
        );
    }

    /// <summary>One layer's summary line.</summary>
    static string Line(LayerAsset layer, int depth) {
        var name = layer.Name.Length > 0 ? layer.Name : layer.Id;

        return new string(' ', depth * 4)
            + string.Create(
                CultureInfo.InvariantCulture,
                $"{name} — {layer.Kind}, {layer.Blend}, {layer.Opacity:0.##}{(layer.Enabled ? "" : ", off")}"
            );
    }

    /// <summary>
    ///     Everything about a stack that decides which <em>elements</em> the rows are, as one string.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Identity and structure only, and deliberately not a value.</b> What this answers is
    ///     "would rebuilding produce a different set of controls" — so a layer's id, its kind and how
    ///     many mask entries and effects it has are in it, and its opacity, its mode and its ticks are
    ///     not. Putting a value in here would rebuild the tree on every slider tick, which is the
    ///     thing the comparison exists to stop; leaving the structure out would leave a row bound to a
    ///     layer that is no longer there.
    /// </remarks>
    static string Shape(LayerStackDocument document, string chosen) {
        StringBuilder builder = new();

        if (LayerStackEdit.SetFor(document.Document, chosen) is not { } set) {
            return "";
        }

        // ⚠ The set's own name is in it, and leaving it out was the trap #927 walks into: two sets
        // of one stack can carry the same channels and the same layer ids — a copied slot is exactly
        // that — so switching between them would match on shape, keep the rows, and leave every
        // control editing the set the artist just navigated away from.
        builder.Append(set.Name).Append('/');

        foreach (var channel in set.Channels) {
            builder.Append(channel.Usage).Append('|');
        }

        builder.Append('/');
        Walk(set.Layers, 0);

        return builder.ToString();

        void Walk(List<LayerAsset> layers, int depth) {
            for (var index = layers.Count - 1; index >= 0; index--) {
                var layer = layers[index];

                builder
                    .Append(depth)
                    .Append(':')
                    .Append(layer.Id)
                    .Append(':')
                    .Append((int)layer.Kind)
                    .Append(':')

                    // ⚠ `Mask.Source` is deliberately NOT here, and it used to be. It belonged while
                    // `MaskRows` returned early for `None`, because the source then decided whether a
                    // base row existed at all. The base row is unconditional now and every source's
                    // control is created with it, so the source changes what a row *shows* and not
                    // which elements exist — and leaving it in made the source dropdown tear down and
                    // rebuild the whole tree from inside its own `SelectionChanged`, which is exactly
                    // what this signature exists to prevent.
                    .Append(layer.Mask.Layers.Count)
                    .Append(':')
                    .Append(layer.Mask.Effects.Count)
                    .Append(':')

                    // ⚠ The node type each of them *resolves to*, and emphatically not the text
                    // somebody is typing — #1086. A filter's knob rows are one field per declared
                    // port, so which elements exist really does depend on which type it is; but a
                    // shape carrying `FilterNode` itself would tear the tree down on every keystroke
                    // of a path, which is the defect `FilterRows` is written around. Resolved, it
                    // changes exactly once — at the keystroke that turns an unknown path into a real
                    // node type, which is the keystroke the rows appear on.
                    .Append(FilterType(document, layer)?.Path ?? "");

                foreach (var effect in layer.Mask.Effects) {
                    builder
                        .Append(':')
                        .Append(
                            document.Library.Registry.TryGet(effect.Node.Trim(), out var type) ? type.Path : ""
                        );
                }

                builder.Append(';');

                Walk(layer.Children, depth + 1);
            }
        }
    }

    void Clear() {
        foreach (var child in rows.Children.ToArray()) {
            child.Remove();
        }

        bindings.Clear();
        pending.Clear();

        built = null;
        shape = "";
    }

    /// <summary>Builds one row per layer, and one per entry of each layer's mask.</summary>
    void Build(LayerStackDocument document) {
        // ⚠ Backwards, and over a copy, because `Children` is the live list: removing forwards
        // renumbers what is left under the loop and drops every second row — which looks like a
        // half-loaded stack rather than a bug in a panel.
        Clear();

        Selected = null;

        if (LayerStackEdit.SetFor(document.Document, SetName) is not { } set) {
            if (tool is not null) {
                tool.LayerId = "";
            }

            return;
        }

        // ⚠ Asked once per build rather than once per row, and asked of `LayerStackEdit` rather than
        // answered here — #893. The compiler refuses the same set on the same rule, and a panel with
        // its own copy of it is a panel that can offer to move a layer the compiler will not build.
        var ambiguous = LayerStackEdit.Ambiguous(set);

        // ⚠ The selection is recovered from the brush rather than reset, and which of the two is the
        // durable copy is the decision. A panel's factory re-runs whenever the workspace relays out
        // — opening the paint pane does it — so a view that cleared the selection on every build
        // would take the artist's chosen layer away every time they opened another panel. The brush
        // is the module's and survives that; this view is the presenter of it. What is *not* kept is
        // an id no layer of this stack answers to, which is what opening a second stack looks like:
        // two stacks made from `LayerStackDocument.Starter` have the same layer ids, so the check has
        // to be against this document rather than against a remembered one.
        // ⚠ And an ambiguous id is not recovered either: the brush would be aimed at whichever of
        // the layers sharing it the walk reaches first, which is the same wrong answer the rows are
        // refusing to give.
        if (tool is { LayerId.Length: > 0 }
            && !ambiguous.Contains(tool.LayerId)
            && LayerStackEdit.Find(document.Document, new(set.Name, tool.LayerId)) is not null) {
            Selected = new LayerPath(set.Name, tool.LayerId);
        } else if (tool is not null) {
            tool.LayerId = "";
        }

        Walk(set.Layers, 0);

        // ⚠ The drain, and it is the whole of what porting a row to markup changed about this
        // method. Every region a `.vxml` declares — `LayerRowView`'s channel loop and its refusal —
        // is an `Effect`, and an effect only ever queues, so the elements those regions make do not
        // exist until something flushes. `Present` restates every row immediately after this call
        // and six test files read the tree synchronously after `Show` returns, so waiting for the
        // frame's own flush would mean a first pass over rows that are half built. Draining here
        // costs one extra flush on the passes where the shape actually changed.
        // ⚠ **And the drain is allowed to have done nothing, which is the whole of why this is not
        // three lines.** `Flush` returns zero and runs nothing when a flush is already in progress —
        // deliberately, so the outer drain picks the work up — and `Build` is reached from inside one
        // on every *external* edit: the depth watcher is itself an `Effect`, so an undo or a redo of
        // an Add Layer arrives here mid-flush. Running the owed closures then reads `Ticks()` before
        // the `@for` that makes them has run, gets an empty list, wires nothing, and leaves every
        // channel box on every rebuilt row unchecked and inert — with the boxes appearing a moment
        // later when the outer drain gets to them, so it looks like a panel that works.
        //
        // So the owed work waits for a drain that actually happened. `Post` runs it at the start of
        // the next flush, which is after the outer one has made the regions, and it restates from
        // there because the closures it runs are what register the bindings `Present` already asked
        // for.
        if (root.Document.Effects.Flush() > 0 || root.Document.Effects.PendingCount == 0) {
            Drain();

            return;
        }

        // ⚠ A one-shot effect rather than `Post`, because `Post` runs at the start of the *next*
        // flush and the outer drain is still going: the regions this is waiting for are made in it,
        // and so is anything they queue in turn. An effect created here is picked up by that same
        // drain, so the owed work happens in the frame the edit did — not one later, with every tick
        // box drawn unchecked in between.
        //
        // ⚠ Untracked, and disposed the moment it has run. The closures below read the document, so
        // an effect that tracked them would take a dependency on the whole stack and re-run itself
        // on the next edit, wiring every handler a second time.
        var owed = new Effect[1];

        owed[0] = new Effect(
            () => {
                ReactiveGraph.Untracked(() => {
                    Drain();
                    Restate();
                });

                owed[0].Dispose();
            },
            root.Document.Effects
        );

        void Drain() {
            foreach (var owed in pending) {
                owed();
            }

            pending.Clear();
        }

        void Walk(List<LayerAsset> layers, int depth) {
            for (var index = layers.Count - 1; index >= 0; index--) {
                var layer = layers[index];

                if (ambiguous.Contains(layer.Id)) {
                    AmbiguousRow(layer, depth);
                } else {
                    LayerRow(document, set, layer, depth);
                    FillRows(document, set, layer, depth + 1);
                    FilterRows(document, set, layer, depth + 1);
                    MaskRows(document, set, layer, depth + 1);
                }

                Walk(layer.Children, depth + 1);
            }
        }
    }

    /// <summary>A row for a layer whose id names more than one layer: what it is, and no controls.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/893">#893</a>'s panel half, and
    ///         the reason the compile refusal was not enough.</b> <c>LayerPath</c> addresses a layer
    ///         by id and <c>LayerStackEdit</c> resolves it to the <em>first</em> match, so every
    ///         control on the second such row drives the first: an artist reorders row four and row
    ///         two moves. <c>LayerStackGraph.Duplicates</c> refuses the stack, but a refusal is a
    ///         message beside a list of rows that are still drawn and still clicked — the panel
    ///         builds its rows from the document rather than from a compilation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Listed and disarmed rather than hidden</b>, which is the same rule a disabled
    ///         layer's row follows: a row that vanished would leave an artist with a file they cannot
    ///         see the shape of, and the shape is what they have to fix.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And its mask rows are not drawn at all.</b> Every one of them describes itself
    ///         through <c>LayerStackEdit.Find</c>, so under an ambiguous id they would render the
    ///         <em>first</em> such layer's mask under the second one's name — a sentence that is
    ///         simply false rather than merely uneditable.
    ///     </para>
    ///     <para>
    ///         The text is written once rather than through <c>bindings</c>, because nothing this
    ///         panel offers can change a layer it refuses to edit; a change made to one from
    ///         somewhere else arrives on the next rebuild.
    ///     </para>
    /// </remarks>
    void AmbiguousRow(LayerAsset layer, int depth) {
        var row = rows.Add("layer-stack-row");

        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        row.Add("layer-stack-row-name").Text = Line(layer, depth);
        row.Add("layer-stack-row-refusal").Text = Ambiguity(layer.Id);
    }

    /// <summary>What a row says in place of its controls when its id names more than one layer.</summary>
    /// <param name="id">The shared <see cref="LayerAsset.Id" />.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>Public because it is what a test reads off the tree, and what a person reads is the
    ///     only evidence that the row was disarmed for a reason.</b> A row with no buttons and no
    ///     sentence is indistinguishable from a panel that failed to build.
    /// </remarks>
    public static string Ambiguity(string id) =>
        (id.Length > 0
            ? $"More than one layer has the id '{id}'"
            : "More than one layer has no id at all")
        + ", so this row cannot say which of them it is. Every edit here is addressed by id and would "
        + "move the first of them — give each layer its own 'id' in the file, and the controls come back.";

    /// <summary>What a row says in place of selecting, when the layer it draws has no id.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/966">#966</a>, and it is the tail
    ///         of #893 rather than a second case of it.</b> A <em>single</em> id-less layer addresses
    ///         perfectly well — <c>LayerStackEdit.Find</c> resolves <c>""</c>, the compiler accepts
    ///         it, and every other control on the row works — so refusing the whole row the way
    ///         <see cref="Ambiguity" /> does would turn a schema nicety into a panel that cannot edit
    ///         a one-layer file. The one gesture it cannot make is a selection, and only because
    ///         <c>PaintTool.LayerId</c> already gives <c>""</c> a second meaning: <em>the first paint
    ///         layer in composite order</em>. So clicking the row would set a value indistinguishable
    ///         from having selected nothing, the marker would come back off at the next refresh, and
    ///         on a stack with a second paint layer the brush would aim at <em>that</em> one — which
    ///         is #910's "silently painting somewhere else" reached by another door.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The button is disarmed rather than <c>PaintTool.LayerId</c> being split, which is
    ///         the other way out the issue names and the better one.</b> A member whose two meanings
    ///         collide should stop colliding; what stops that happening here is ownership —
    ///         <c>PaintTool</c> is the paint slice's file — so this is the half the panel can make
    ///         true on its own, and it leaves the artist a layer they cannot paint on and a reason
    ///         rather than a click that silently did something else.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Disabling the button is the whole of the refusal, and a second one in
    ///         <c>Choose</c> would be unreachable.</b> The channel ticks a few elements along say the
    ///         opposite about themselves and are right to: <c>ToggleBase.Activate</c> flips
    ///         <c>IsChecked</c> before it asks about <c>Disabled</c>, so a tick has to be put back
    ///         from the document. <c>ButtonBase.Activate</c> runs neither the bound command nor the
    ///         click when it is disabled — proved by sabotage, which is how this remark stopped
    ///         saying the reverse.
    ///     </para>
    /// </remarks>
    public const string Unnamed =
        "This layer has no 'id', and an empty id already means 'the first paint layer' to the brush — "
        + "so selecting this row would aim it at whichever paint layer comes first instead. Give the "
        + "layer its own 'id' in the file, and it can be selected.";

    void LayerRow(LayerStackDocument document, TextureSetAsset set, LayerAsset layer, int depth) {
        LayerPath path = new(set.Name, layer.Id);

        // ⚠ Decided once, from the layer the row was built for, because an id is structure rather
        // than a value: `Shape` carries it, so a layer that gained or lost one rebuilt this row.
        //
        // ⚠ And a second condition that used to be here has gone — #927. A row of any set but the
        // first was disarmed, with `OtherSet` under it, because the brush took `Sets[0]` whatever
        // the panel was showing and selecting by id alone would have sent the stroke into the wrong
        // set's layer of the same name. `LayerStackDocument.PaintSet` is now that choice and
        // `PaintSurface.Open` resolves it, so the shown set *is* the painted set and there is
        // nothing left to refuse.
        var named = layer.Id.Length > 0;

        // ⚠ `LayerRowView` and not `rows.Add("layer-stack-row")`, and the swap is element for
        // element: the component's host tag *is* `layer-stack-row`, so this is the same direct child
        // of `layer-stack-list` the stylesheet already reaches. #881.
        var row = rows.Add<LayerRowView>();

        row.Named = named;
        row.Usages = [.. set.Channels.Select(channel => channel.Usage)];

        // ⚠ Still written here and still `depth × 12px`, which is what `TexturingTheme.vcss` says
        // about it: a computed length is not a class, and a `layer-stack-depth-N` rule per possible
        // nesting would be a sheet that runs out at whatever N somebody guessed.
        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        var select = row.Choose;

        select.Clicked += _ => Choose(path);

        // ⚠ The button IS the guard here, which is NOT what the channel tick two elements along
        // says about itself — and the difference is real rather than an inconsistency.
        // `ToggleBase.Activate` flips `IsChecked` before it asks about `Disabled`, so a tick needs
        // the model to refuse as well; `ButtonBase.Activate` runs neither the command nor the click
        // when it is disabled, so a second refusal inside `Choose` would be a branch nothing in this
        // file can reach.
        select.Disabled = !named;

        var up = row.Up;
        var down = row.Down;

        // ⚠ Up is +1 in the file's order. `TextureSetAsset.Layers` is bottom first and this panel
        // draws it topmost first, so the button an artist reads as "over the one above it" is the one
        // that moves the layer *later* in the composite. Getting this backwards is invisible on a
        // one-layer stack and silent on two identical ones, which is why the test that covers it
        // compares compiled plans.
        up.Clicked += _ => Move(document, path, +1, "Move Layer Up");
        down.Clicked += _ => Move(document, path, -1, "Move Layer Down");

        row.Delete.Clicked += _ => RemoveLayer(document, path);

        var enabled = row.Enabled;

        enabled.CheckedChanged += (_, value) => Set(
            document,
            path,
            current => current with { Enabled = value },
            value ? "Show Layer" : "Hide Layer"
        );

        var name = row.Name;
        var blend = row.Blend;

        foreach (var mode in Enum.GetValues<LayerBlendMode>()) {
            blend.AddOption(mode.ToString());
        }

        blend.SelectionChanged += (_, value) => {
            if (Enum.TryParse<LayerBlendMode>(value, out var mode)) {
                Set(document, path, current => current with { Blend = mode }, "Set Blend Mode");
            }
        };

        var opacity = row.Opacity;

        // ⚠ One undo entry for a drag, which is what the merge key buys and what nothing else here
        // needs. Every other control on this row reports one decision per gesture; a slider reports
        // one per frame, so without the key a drag across the row is three hundred entries and with a
        // key that never sealed it would be one entry for every drag this artist ever makes. The seal
        // on pointer-release is the other half — `CommandStack.Seal` is explicit rather than a time
        // window precisely so that this is a decision a caller states.
        opacity.ValueChanged += (_, value) => Set(
            document,
            path,
            current => current with { Opacity = value },
            "Set Layer Opacity",
            "opacity"
        );

        // ⚠ `handledEventsToo`, and it is the whole of whether this line runs. `Range.Pointed` sets
        // `args.Handled = true` on the release that ends a drag, and `AddHandler` defaults to not
        // being called for a handled event — so the bubbling handler fires on every release EXCEPT
        // the one that matters. A test that raises a bare Released without a Pressed leaves
        // `dragging` false, takes the default branch, and sees an unhandled event, which is why this
        // read as covered.
        opacity.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Released) {
                    document.Stack.Seal();
                }
            },
            RoutingStrategy.Bubble,
            handledEventsToo: true
        );

        // ⚠ The ticks and this row's restating closure are deferred, and that deferral is the one
        // thing a markup row costs. The ticks are a `@for` in `LayerRowView`, so they are made by an
        // `Effect` and do not exist until `EffectScheduler.Flush` has run: a list read here would be
        // empty, every box would stay unticked whatever the layer writes, and nothing anywhere would
        // say so. `Build` drains the queue after the walk and then runs what is pending.
        pending.Add(() => {
            var ticks = row.Ticks();

            for (var index = 0; index < ticks.Count && index < set.Channels.Count; index++) {
                var usage = set.Channels[index].Usage;

                ticks[index].CheckedChanged += (_, value) => {
                    if (writing || LayerStackEdit.Find(document.Document, path) is not { } current) {
                        return;
                    }

                    if (Restrict(set, current, usage, value) is not { } channels) {
                        // ⚠ The last tick, refused in the model and not only greyed in the panel.
                        // `ToggleBase.Activate` flips `IsChecked` before it asks about `Disabled` — a
                        // real pointer never reaches it, because `Control.Refuse` stops the route, but
                        // an access key or an automation peer calls `Activate()` directly. `Restate`
                        // puts the box back from the document, which is the only copy that matters.
                        Restate();

                        return;
                    }

                    Set(
                        document,
                        path,
                        layer => layer with { Channels = channels },
                        value ? "Write Channel" : "Stop Writing Channel"
                    );
                };
            }

            bindings.Add(() => {
                if (LayerStackEdit.Find(document.Document, path) is not { } current) {
                    return;
                }

                var chosen = Selected == path;

                // The marker is in the row's own text rather than a style, so what the panel says
                // about which layer the brush is aimed at is something a test can read.
                name.Text = (chosen ? "● " : "") + Line(current, depth);
                select.Label = named ? chosen ? "Selected" : "Select" : "Cannot select";
                enabled.IsChecked = current.Enabled;
                blend.Value = current.Blend.ToString();
                opacity.Value = current.Opacity;

                up.Disabled = !MoveLayerCommand.CanMove(document.Document, path, +1);
                down.Disabled = !MoveLayerCommand.CanMove(document.Document, path, -1);

                var written = 0;

                foreach (var channel in set.Channels) {
                    if (current.Writes(channel.Usage)) {
                        written++;
                    }
                }

                for (var index = 0; index < ticks.Count && index < set.Channels.Count; index++) {
                    var writes = current.Writes(set.Channels[index].Usage);

                    ticks[index].IsChecked = writes;

                    // ⚠ The last remaining tick cannot be cleared, and this is where the ambiguity in
                    // the file is kept out of the panel. Clearing it would leave `Channels` empty —
                    // and empty means *all*, so the gesture an artist reads as "and now it writes
                    // nothing" would make the layer write everything. A layer that should write
                    // nothing is one that is switched off, which is the tick box two elements left.
                    ticks[index].Disabled = writes && written == 1;
                }
            });
        });
    }

    /// <summary>What a fill layer <em>is</em>: where its pixels come from, and its value per channel.</summary>
    /// <param name="document">The stack being edited.</param>
    /// <param name="set">The texture set the layer is in — its channels are the rows.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="depth">How far in to indent, in the layer list's own units.</param>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/986">#986</a>: a fill added from
    ///         this panel kept the mid-grey it was born with, because nothing here could edit
    ///         <c>LayerAsset.Values</c> or <c>LayerAsset.Textures</c>.</b> The panel could already
    ///         edit a layer's blend, opacity, channels and its whole mask — every property except the
    ///         two that decide what the layer actually puts on the surface. The flow that left was
    ///         press <em>Add layer</em>, get a grey row, and open the <c>.vxlayers</c> in a text
    ///         editor.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A row per channel and not a colour swatch, which is the whole shape of it.</b>
    ///         <c>Values</c> is keyed by usage and a fill that sets roughness alone is one entry
    ///         rather than seven — deliberately, per that member's own remarks — so a single swatch
    ///         would have to pick a channel to be about and would silently be about the wrong one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A row per channel of the <em>set</em>, shown or hidden — not a row per channel
    ///         the layer writes.</b> Which channels a layer writes is a tick on the row above, so
    ///         building only the written ones would put <c>LayerAsset.Channels</c> into
    ///         <see cref="Shape" /> and tear the whole tree down from inside a tick box's own
    ///         handler. This is the rule <c>MaskAsset.Source</c> already follows one method down: the
    ///         controls all exist, and what changes is which of them are displayed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The tick is presence in <c>Values</c> and it is not the same question as the
    ///         channel tick above.</b> A layer restricting no channels writes all of them
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> · 2), and an absent
    ///         entry is then the only way to say "this layer has nothing to say about that channel" —
    ///         so a fill writes a channel exactly when both are true, and the panel that showed one
    ///         of the two would be lying about the picture. A tick switched on starts from the
    ///         channel's own <c>ChannelAsset.Default</c>, which is #807's rejected <em>fallback</em>
    ///         used as a starting value, where it is the honest answer rather than a number taken
    ///         from a layer that is not there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sliders run 0 to 1 and a value above 1 is therefore not authorable here</b> —
    ///         an emissive of 4 cd/m² stays a thing the file says and this panel cannot. What it must
    ///         not do is <em>lose</em> it: each slider rewrites its own component of the array read
    ///         back out of the document, so dragging red on an HDR colour leaves the other three
    ///         exactly as the file has them. Filed rather than solved, because the fix is a numeric
    ///         field this framework does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The projection is here and its axis beside it</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1032">#1032</a>. It belongs on the
    ///         fill row rather than on the layer's own, because a projection means something only on
    ///         a fill: <c>LayerStackGraph.Project</c> warns on every other kind by name. The axis is
    ///         shown for <c>Planar</c> alone, which is the one projection that chooses a plane.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Graph</c> is offered, and #986's "refused in this build" is wrong.</b>
    ///         <c>LayerStackGraph.Fill</c> resolves a graph fill through the compound library exactly
    ///         as a generator mask is resolved — same mechanism, pointed at the colour — so what it
    ///         wants is the published path, which is the field beside the picker.
    ///     </para>
    /// </remarks>
    void FillRows(LayerStackDocument document, TextureSetAsset set, LayerAsset layer, int depth) {
        if (layer.Kind != LayerKind.Fill) {
            return;
        }

        LayerPath path = new(set.Name, layer.Id);

        // ⚠ `FillRowView` and not `rows.Add("layer-stack-fill-row")`, and the swap is element for
        // element: the component's host tag *is* `layer-stack-fill-row`, so this is the same direct
        // child of `layer-stack-list` the stylesheet already reaches. #881.
        var source = rows.Add<FillRowView>();

        source.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        // ⚠ Read straight away rather than through `pending`, and that is a property of this row
        // rather than of the port. `FillRowView` declares no region — no `@if`, no `@for` — so every
        // element it holds exists the moment `Add` returns. `LayerRowView`'s channel strip is a
        // `@for` and its `ref`s therefore do not, which is what the drain in `Build` is for.
        var kind = source.Kind;

        foreach (var choice in Enum.GetValues<LayerFillSource>()) {
            kind.AddOption(choice.ToString());
        }

        kind.SelectionChanged += (_, chosen) => {
            if (!Enum.TryParse<LayerFillSource>(chosen, out var wanted)) {
                return;
            }

            Set(document, path, current => current with { Fill = wanted }, "Set Fill Source");
        };

        var graph = source.Graph;

        graph.ValueChanged += (_, typed) => Set(
            document,
            path,
            current => current with { Graph = typed ?? "" },
            "Set Fill Graph",
            "fill-graph:" + layer.Id
        );

        graph.Submitted += _ => document.Stack.Seal();

        var projection = source.Projection;

        foreach (var choice in Enum.GetValues<LayerProjection>()) {
            projection.AddOption(choice.ToString());
        }

        // ⚠ Moving off Planar puts the axis back to y, and it is the panel deciding rather than the
        // file. An axis means one plane for a planar projection and nothing anywhere else, so a
        // stale one left behind makes `LayerStackGraph.Project` warn about a value the artist can no
        // longer see — a diagnostic about this panel's bookkeeping rather than about their stack.
        // `LayerStackYaml` still writes the key whatever the projection is: a hand-written file may
        // carry one, and a *writer* silently dropping it is a different thing from an edit that is
        // visible, undoable and asked for. #1032.
        projection.SelectionChanged += (_, chosen) => {
            if (!Enum.TryParse<LayerProjection>(chosen, out var wanted)) {
                return;
            }

            Set(
                document,
                path,
                current => wanted == LayerProjection.Planar
                    ? current with { Projection = wanted }
                    : current with { Projection = wanted, PlanarAxis = LayerAxis.Y },
                "Set Projection"
            );
        };

        var axis = source.Axis;

        foreach (var choice in Enum.GetValues<LayerAxis>()) {
            axis.AddOption(choice.ToString());
        }

        axis.SelectionChanged += (_, chosen) => {
            if (!Enum.TryParse<LayerAxis>(chosen, out var wanted)) {
                return;
            }

            Set(document, path, current => current with { PlanarAxis = wanted }, "Set Projection Axis");
        };

        bindings.Add(() => {
            if (LayerStackEdit.Find(document.Document, path) is not { } current) {
                return;
            }

            kind.Value = current.Fill.ToString();
            graph.Value = current.Graph;
            graph.SetStyle("display", current.Fill == LayerFillSource.Graph ? "flex" : "none");

            projection.Value = current.Projection.ToString();
            axis.Value = current.PlanarAxis.ToString();

            // Shown for the one projection it decides anything about — the other two have no plane
            // to choose, and `Project` says so rather than ignoring an axis set on them.
            axis.SetStyle("display", current.Projection == LayerProjection.Planar ? "flex" : "none");
        });

        foreach (var channel in set.Channels) {
            ChannelRow(document, set, path, channel, depth + 1);
        }
    }

    /// <summary>What the filter picker calls one of the five built-in adjustments.</summary>
    /// <remarks>
    ///     ⚠ <b>A word rather than an empty <see cref="LayerAsset.FilterNode" />.</b> The two ways of
    ///     naming a filter are exclusive in the file — a path wins, and <c>LayerFilterKind.Levels</c>
    ///     is zero, so a layer carrying both would say one thing and compile another — and a picker
    ///     that showed only the five with a path field beside it would leave "which of these two is
    ///     in force" to be inferred from whether a box happened to be empty.
    /// </remarks>
    public const string PresetFilter = "Preset";

    /// <summary>What the filter picker calls a node type named by path, doc 48 § D10's fourth kind.</summary>
    public const string NodeFilter = "Node";

    /// <summary>Which adjustment a filter layer applies: one of the five, or a node it names.</summary>
    /// <param name="document">The stack being edited.</param>
    /// <param name="set">The texture set the layer is in.</param>
    /// <param name="layer">The layer.</param>
    /// <param name="depth">How far in to indent.</param>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/1078">#1078</a>: a layer kind an
    ///         artist could add and could not configure.</b> The <em>Add layer</em> picker offers
    ///         every <c>LayerKind</c>, so a filter layer is two clicks away; until this row no view
    ///         in the tree read <c>LayerFilterKind</c> at all — a sweep over <c>*.cs</c> and
    ///         <c>*.vxml</c> found it in three model files and nowhere else. So every filter an
    ///         artist added was a <c>Colour/Levels</c> on its defaults for ever, which is worse than
    ///         the kind not being offered: the gesture succeeds and produces a layer whose effect
    ///         cannot be explained.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Switching between the two rewrites the layer to the node the other half already
    ///         meant, so the picture does not move.</b> Choosing <see cref="NodeFilter" /> seeds the
    ///         path with <c>LayerStackGraph.Filter(current.Filter).Type</c> — the very type the enum
    ///         compiles to — and choosing <see cref="PresetFilter" /> clears the path, which is what
    ///         puts the enum back in force. A control that switched to an <em>empty</em> path would
    ///         be a control whose model still said "preset", and the picker would snap back on the
    ///         next bind with nothing to show for the click.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Every control is created and the binding decides which are shown</b>, which is
    ///         this panel's rule and a correctness one: <see cref="Shape" /> does not carry
    ///         <c>Filter</c> or <c>FilterNode</c>, so building only the controls the current choice
    ///         needs would tear the row down from inside its own <c>SelectionChanged</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The filter's <em>numbers</em> are not here, and that is a limit rather than an
    ///         omission.</b> <c>LayerAsset.Settings</c> is by port name and a port's declared default
    ///         lives on the node type — which this view has no registry to ask, by
    ///         <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>. A field that showed 0
    ///         for a <c>Levels</c> whose <c>Input White</c> is 1 would be an interface stating
    ///         something the picture contradicts, so the row says which filter and not how much of
    ///         it. A mask effect's <c>Values</c> has no row either, for the same reason.
    ///     </para>
    /// </remarks>
    void FilterRows(LayerStackDocument document, TextureSetAsset set, LayerAsset layer, int depth) {
        if (layer.Kind != LayerKind.Filter) {
            return;
        }

        LayerPath path = new(set.Name, layer.Id);

        // ⚠ `FilterRowView`, on `FillRows`' reasoning and with `FillRowView`'s guarantee: the
        // component's host tag *is* `layer-stack-filter-row`, and it declares no region, so its
        // `ref`s are readable the moment `Add` returns. #881.
        var row = rows.Add<FilterRowView>();

        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        var source = row.Source;

        // ⚠ **The chosen source is the row's own state and is not read back off the path.** It was,
        // and a node path is a field an artist clears with backspace — so the last keystroke made
        // `FilterNode` empty, the binding decided the layer was a preset again, and the field
        // vanished from under the caret with the picker snapping to Preset. A row that can only
        // hold a *non-empty* path is a row nobody can retype.
        var named = layer.FilterNode.Trim().Length > 0;

        source.AddOption(PresetFilter);
        source.AddOption(NodeFilter);

        source.SelectionChanged += (_, chosen) => named = string.Equals(chosen, NodeFilter, StringComparison.Ordinal);

        source.SelectionChanged += (_, chosen) => Set(
            document,
            path,
            current => string.Equals(chosen, NodeFilter, StringComparison.Ordinal)
                ? current.FilterNode.Trim().Length > 0
                    ? current
                    : current with { FilterNode = LayerStackGraph.Filter(current.Filter).Type }
                : current.FilterNode.Length == 0
                    ? current
                    : current with { FilterNode = "" },
            "Set Filter Source"
        );

        var kind = row.Kind;

        foreach (var choice in Enum.GetValues<LayerFilterKind>()) {
            kind.AddOption(choice.ToString());
        }

        kind.SelectionChanged += (_, chosen) => {
            if (!Enum.TryParse<LayerFilterKind>(chosen, out var wanted)) {
                return;
            }

            Set(document, path, current => current with { Filter = wanted }, "Set Filter");
        };

        var node = row.Node;

        // ⚠ Trimmed nowhere here and trimmed everywhere it is read: `LayerStackGraph` decides that a
        // layer names a node by `FilterNode.Trim().Length`, so a field holding spaces is a preset —
        // and a view that trimmed on the way in would silently delete the artist's cursor position
        // between two keystrokes of a path they are still typing.
        node.ValueChanged += (_, typed) => Set(
            document,
            path,
            current => current with { FilterNode = typed ?? "" },
            "Set Filter Node",
            "filter-node:" + layer.Id
        );

        node.Submitted += _ => document.Stack.Seal();

        bindings.Add(() => {
            if (LayerStackEdit.Find(document.Document, path) is not { } current) {
                return;
            }

            // ⚠ Content may turn the field *on* and never off — an undo or an edit made elsewhere
            // can put a path back while this row is open, and adopting it is right; hiding a field
            // because it is momentarily empty is the defect above.
            named |= current.FilterNode.Trim().Length > 0;

            source.Value = named ? NodeFilter : PresetFilter;
            kind.Value = current.Filter.ToString();
            kind.SetStyle("display", named ? "none" : "flex");
            node.Value = current.FilterNode;
            node.SetStyle("display", named ? "flex" : "none");
        });

        if (FilterType(document, layer) is { } type) {
            KnobRows(
                document,
                path,
                type,
                depth + 1,
                "Filter",
                "filter:" + layer.Id,
                current => current.Settings,
                current => current.Texts,
                (current, port, lanes) => current with { Settings = Numbered(current.Settings, port.Name, port, lanes) },
                (current, setting, text) => current with { Texts = Named(current.Texts, setting, text) }
            );
        }
    }

    /// <summary>The node type a filter layer compiles to, or null when the library has not got it.</summary>
    /// <param name="document">The stack being edited, which is where the library is.</param>
    /// <param name="layer">The layer.</param>
    /// <returns>The type, or null.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/1086">#1086</a> says this view
    ///         has no registry to ask and that is refuted: <c>LayerStackDocument.Library</c> has
    ///         carried one since <a href="https://github.com/Rikarin/Vixen/issues/858">#858</a>.</b>
    ///         What the issue is right about is why it matters — a numeric field for
    ///         <c>Colour/Levels</c>' <c>Input White</c> opening at <b>0</b> where the node uses
    ///         <b>1</b> is an interface the picture contradicts — and
    ///         <c>LayerStackGraph.Filter(kind)</c>, which is all this row had, gives port names and
    ///         no defaults.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And <a href="https://github.com/Rikarin/Vixen/issues/820">#820</a> does not
    ///         forbid it.</b> That issue is about two <c>TexturePlanEvaluator</c>s compiling every
    ///         kernel twice: what it objects to is a second holder of pipelines, shader modules and
    ///         a set-layout cache on a <em>device</em>. A <see cref="NodeTypeRegistry" /> holds
    ///         declarations, and this one is the document's own rather than a second publication —
    ///         which is the half of #858 that would be worth objecting to.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The preset path resolves through the registry too, rather than trusting
    ///         <c>LayerStackGraph.Filter</c>'s type name.</b> A library that has not got
    ///         <c>Colour/Levels</c> is a library this panel must not draw fields against, and the
    ///         same call answers both halves.
    ///     </para>
    /// </remarks>
    static NodeTypeDefinition? FilterType(LayerStackDocument document, LayerAsset layer) {
        if (layer.Kind != LayerKind.Filter) {
            return null;
        }

        var path = layer.FilterNode.Trim();

        if (path.Length == 0) {
            path = LayerStackGraph.Filter(layer.Filter).Type;
        }

        return document.Library.Registry.TryGet(path, out var type) ? type : null;
    }

    /// <summary>Every input of a node type a stack file may write a number into.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The ports, in declaration order.</returns>
    /// <remarks>
    ///     ⚠ <b>Derived exactly as <c>LayerStackGraph.Published</c> and <c>Effect</c> derive it,
    ///     because a field this panel draws for a port those two drop is a field an artist edits and
    ///     the compiler warns about.</b> Both refuse a value whose port is not a declared input or is
    ///     an <see cref="PortKind.Image" /> — the named image input being one of those — so the
    ///     drawable set is the declared inputs that carry numbers. ⚠ It is <em>not</em>
    ///     <c>LayerStackGraph.Filter(kind).Ports</c>: that list is a hand-written one per preset with
    ///     no defaults in it, which is the whole of what #1086 could not ask for.
    /// </remarks>
    static IEnumerable<PortDefinition> Knobs(NodeTypeDefinition type) {
        foreach (var port in type.Ports) {
            if (port.Direction == GraphPortDirection.Input
                && port.Kind is not (PortKind.Image or PortKind.Flow or PortKind.Texture or PortKind.Sampler)) {
                yield return port;
            }
        }
    }

    /// <summary>A field per number and a control per setting of one node type, on their own rows.</summary>
    /// <param name="document">The stack being edited.</param>
    /// <param name="path">Which layer the edit is addressed to.</param>
    /// <param name="type">The node type whose declarations the rows are built from.</param>
    /// <param name="depth">How far in to indent.</param>
    /// <param name="what">What the undo entries are called — <c>Filter</c> or <c>Mask Effect</c>.</param>
    /// <param name="key">The prefix each field's coalescing key is built on.</param>
    /// <param name="numbers">Where this thing's numbers live on a layer.</param>
    /// <param name="settings">And its settings.</param>
    /// <param name="setNumber">How one number is written back.</param>
    /// <param name="setSetting">And one setting.</param>
    /// <remarks>
    ///     <para>
    ///         <b>One builder for a filter layer and for a mask effect, because they are one row
    ///         shape one level apart</b> — <c>LayerStackGraph.Published</c> <em>is</em> <c>Effect</c>
    ///         pointed at a layer, down to the two loops and the warning each drops a stray key with.
    ///         #1086 asks for both together for that reason, and doing the layer alone would leave a
    ///         mask effect that can name <c>Filters/Blur</c> and cannot say how wide.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A field opens at the port's <em>declared</em> default and never at zero.</b> That
    ///         is the whole of why this could not be built before the view could reach a registry: a
    ///         field showing a number the compiler is not using is worse than no field, because the
    ///         artist reads it, believes it, and cannot explain the render.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A lane at its default writes nothing, and that is what keeps a saved stack from
    ///         growing a key per port the moment somebody opens the panel.</b> A file that stored
    ///         every declared default would also pin them: a node type that improved one would leave
    ///         every stack ever opened holding the old number, which is the bargain
    ///         <c>NodeGraphCompiler.Bind</c> makes the other way round for a node's settings.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>How many fields a port gets is <see cref="Width" />'s answer and comes from the
    ///         port's <see cref="PortKind" />, not from the length of its declared default</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1097">#1097</a>. A default is
    ///         optional and a kind is not: <c>GraphModel.Interface</c> lets a published compound
    ///         expose <c>("Colour", Input, Float4)</c> with no default at all, and counting lanes off
    ///         the default drew that port <em>one</em> field. <see cref="Lanes" /> then treats a
    ///         stored value whose width is not the drawn count as absent, so opening such a row and
    ///         touching its one field replaced the compound's four numbers with one — data lost by a
    ///         panel that looked like it was only reading.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a port wider than four is refused rather than truncated.</b> No
    ///         <see cref="PortKind" /> carries more than four lanes, so this is reachable only from a
    ///         declaration whose default says more numbers than its kind holds; drawing its first
    ///         four would be the same silent narrowing one door along. The row is listed and
    ///         disarmed, which is what <see cref="Ambiguity" /> and <see cref="Unnamed" /> already do
    ///         with a row this panel will not edit.
    ///     </para>
    /// </remarks>
    void KnobRows(
        LayerStackDocument document,
        LayerPath path,
        NodeTypeDefinition type,
        int depth,
        string what,
        string key,
        Func<LayerAsset, Dictionary<string, float[]>> numbers,
        Func<LayerAsset, Dictionary<string, string>> settings,
        Func<LayerAsset, PortDefinition, float[], LayerAsset> setNumber,
        Func<LayerAsset, string, string, LayerAsset> setSetting
    ) {
        foreach (var port in Knobs(type)) {
            var name = port.Name;
            var lanes = Width(port);
            var row = rows.Add("layer-stack-knob-row");

            row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");
            row.Add("layer-stack-knob-label").Text = name;

            // ⚠ Listed and disarmed, and the sentence is the whole of the refusal: a row that drew
            // its first four fields would write four numbers over however many the file holds the
            // moment one of them is touched, which is the narrowing #1097 is about with a smaller
            // number in it.
            if (lanes > ComponentClasses.Length || port.Default.Length > lanes) {
                row.Add("layer-stack-row-refusal").Text = WideKnob(name, port.Kind, lanes, port.Default.Length);

                continue;
            }

            List<NumericInput> fields = [];

            for (var index = 0; index < lanes; index++) {
                var lane = index;
                var field = row.Add<NumericInput>(null, null, "layer-stack-knob-value");

                // `ChannelRow`'s three, for its reasons: three places because a `Decimals = 0` field
                // writes "0" for 0.25 and reads it back on the next submit, and a hundredth because
                // `Step` is also the floor of the scrub rate.
                field.Decimals = 3;
                field.Step = 0.01d;

                field.NumberChanged += (typed, value) => {
                    // Gated on the field's own verdict, exactly as the colour components are: this
                    // control holds and reports a number rather than clamping it.
                    if (!typed.IsValid) {
                        return;
                    }

                    Set(
                        document,
                        path,
                        current => {
                            var held = Lanes(numbers(current), name, port, lanes);

                            if (held[lane] == (float)value) {
                                return current;
                            }

                            held[lane] = (float)value;

                            return setNumber(current, port, held);
                        },
                        "Set " + what + " Number",
                        $"{key}:{name}:{lane.ToString(CultureInfo.InvariantCulture)}"
                    );
                };

                field.AddHandler<PointerEvent>(
                    (_, args) => {
                        if (args.Action == PointerAction.Released) {
                            document.Stack.Seal();
                        }
                    },
                    RoutingStrategy.Bubble,
                    handledEventsToo: true
                );

                field.Submitted += _ => document.Stack.Seal();
                fields.Add(field);
            }

            bindings.Add(() => {
                if (LayerStackEdit.Find(document.Document, path) is not { } current) {
                    return;
                }

                var held = Lanes(numbers(current), name, port, lanes);

                for (var index = 0; index < fields.Count; index++) {
                    fields[index].Number = held[index];
                }
            });
        }

        foreach (var setting in type.Settings) {
            var name = setting.Name;
            var row = rows.Add("layer-stack-knob-row");

            row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");
            row.Add("layer-stack-knob-label").Text = name;

            // ⚠ A picker when the type says what it accepts and a box when it does not, which is
            // `NodeSettingMember`'s own rule and has to be the same one: a free-text field over a
            // setting with a stated list is where `ture` becomes a value, and a dropdown over one
            // without a list has nothing to offer.
            if (setting.IsChoice) {
                var choice = row.Add<Select>(null, null, "layer-stack-knob-choice");

                foreach (var accepted in setting.Accepted) {
                    choice.AddOption(accepted);
                }

                choice.SelectionChanged += (_, chosen) => Set(
                    document,
                    path,
                    current => setSetting(current, name, chosen ?? setting.Default),
                    "Set " + what + " Setting"
                );

                bindings.Add(() => {
                    if (LayerStackEdit.Find(document.Document, path) is { } current) {
                        choice.Value = settings(current).TryGetValue(name, out var held) && held.Length > 0
                            ? held
                            : setting.Default;
                    }
                });

                continue;
            }

            var text = row.Add<TextBox>(null, null, "layer-stack-knob-text");

            text.Placeholder = setting.Default;

            text.ValueChanged += (_, typed) => Set(
                document,
                path,
                current => setSetting(current, name, typed ?? ""),
                "Set " + what + " Setting",
                $"{key}:{name}"
            );

            text.Submitted += _ => document.Stack.Seal();

            bindings.Add(() => {
                if (LayerStackEdit.Find(document.Document, path) is { } current) {
                    text.Value = settings(current).TryGetValue(name, out var held) ? held : setting.Default;
                }
            });
        }
    }

    /// <summary>How many numbers one port holds, and therefore how many fields its row draws.</summary>
    /// <param name="port">The declared port.</param>
    /// <returns>Its lane count, at least one.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The <see cref="PortKind" /> first and the default only where the kind has no
    ///         width</b> — <a href="https://github.com/Rikarin/Vixen/issues/1097">#1097</a>. Reading
    ///         the count off <c>Default.Length</c> alone is wrong in the one direction that loses
    ///         data: a default is optional, so a <c>Float4</c> declared with none reported
    ///         <em>one</em> lane, and <see cref="Lanes" /> then read the file's four numbers as
    ///         absent and let one field overwrite them. ⚠ It is not a hypothetical shape — the
    ///         generator only ever derives a default from a field initializer, and a vector port's
    ///         initializer is not a constant, so every vector input that does not spell
    ///         <c>[Input(Default = …)]</c> out by hand arrives here with an empty one;
    ///         <c>GraphModel.Interface</c> is the same shape for a published compound, where the
    ///         port is a row in an editor and the default is a thing an author may simply not fill
    ///         in.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="PortKinds.Fields" /> and not a fourth table.</b> An earlier draft of
    ///         this method answered <see cref="PortKind.Dynamic" /> with the declared default's
    ///         length, on the reading that a dynamic port is as wide as whatever reaches it. That is
    ///         true of the <em>value</em> and wrong for the <em>editor</em>, and the shared member's
    ///         own remark says why: drawing four boxes because the node happened to resolve to a
    ///         <c>float4</c> would make the same graph offer a different editor depending on what
    ///         was wired to a different port. This panel has no business disagreeing with the node
    ///         inspector about that.
    ///     </para>
    ///     <para>
    ///         The one thing left to decide here is the kinds with no fields at all — an image port,
    ///         a <see cref="PortKind.None" /> — which reach this only through a declaration that
    ///         should not have produced a knob row. One field is what the refusal below can then
    ///         measure a too-long default against.
    ///     </para>
    /// </remarks>
    static int Width(PortDefinition port) => PortKinds.Fields(port.Kind) is var fields && fields > 0 ? fields : 1;

    /// <summary>What a knob row says in place of its fields when the declaration disagrees with itself.</summary>
    /// <param name="port">The port's name.</param>
    /// <param name="kind">Its kind.</param>
    /// <param name="fields">How many numbers that kind takes.</param>
    /// <param name="declared">How many its default lists.</param>
    /// <returns>The sentence.</returns>
    /// <remarks>
    ///     ⚠ <b>Two arms and, until #1108's review, one sentence — which was false on the arm it did
    ///     not describe.</b> Four is the widest kind there is, so no <see cref="PortKind" /> ever
    ///     trips the width half on its own; what actually reaches here is a default listing more
    ///     numbers than its kind holds, and that includes a <c>Float</c> declared with three, which
    ///     the old wording announced as a port carrying more than four. Both arms say the same true
    ///     thing once the two counts are named separately. A row that quietly drew the first
    ///     <paramref name="fields" /> would hand the author a panel that narrows their port on the
    ///     first click instead.
    /// </remarks>
    public static string WideKnob(string port, PortKind kind, int fields, int declared) =>
        $"'{port}' is a {kind} and takes {fields.ToString(CultureInfo.InvariantCulture)} "
        + $"number{(fields == 1 ? "" : "s")}, but its declaration lists "
        + $"{declared.ToString(CultureInfo.InvariantCulture)} — so this panel will not edit it. A row "
        + "of fields would write its own count over all of them the moment one was touched. Fix the "
        + "port's declaration, and the fields come back.";

    /// <summary>What one port is worth on this layer: what was stored, or the port's own default.</summary>
    /// <remarks>
    ///     ⚠ <b>A copy, always.</b> The array this answers with is written into and handed to
    ///     <c>with</c>, so returning the stored one would edit the layer an undo entry is holding as
    ///     its before-image — <c>WithEffect</c>'s reason, one container down. And a stored value of
    ///     the wrong width is treated as absent rather than padded, because a file that says three
    ///     numbers for a one-lane port is saying something this panel cannot mean.
    /// </remarks>
    static float[] Lanes(Dictionary<string, float[]> stored, string port, PortDefinition declared, int lanes) {
        if (stored.TryGetValue(port, out var held) && held.Length == lanes) {
            return (float[])held.Clone();
        }

        return Default(declared, lanes);
    }

    /// <summary>A port's declared default, as many numbers wide as its row draws.</summary>
    /// <param name="declared">The declaration.</param>
    /// <param name="lanes">How many fields the row has.</param>
    /// <returns>A fresh array of that many numbers.</returns>
    /// <remarks>
    ///     ⚠ <b>One number is splatted across the lanes and not written into the first of them</b>,
    ///     because that is what the frame does: <c>NodeGraphCompiler.Value</c> fills every lane of a
    ///     vector port from a one-element default. Opening the surplus fields at nought instead
    ///     showed <c>0.5, 0, 0, 0</c> over a render using <c>0.5, 0.5, 0.5, 0.5</c>, and the first
    ///     nudge of any lane wrote the noughts into the file — the mirror of
    ///     <a href="https://github.com/Rikarin/Vixen/issues/1097">#1097</a>'s narrowing, reached
    ///     through the widening that fixed it. ⚠ It also keeps <see cref="Numbered" />'s
    ///     back-at-the-default test able to fire, which a comparison against a shorter array cannot.
    ///     A default of some other length that is neither one nor the row's width is refused before
    ///     the row is built at all, so this only ever splats or copies.
    /// </remarks>
    static float[] Default(PortDefinition declared, int lanes) {
        var made = new float[lanes];

        if (declared.Default.Length == 1) {
            Array.Fill(made, declared.Default[0]);

            return made;
        }

        for (var index = 0; index < lanes && index < declared.Default.Length; index++) {
            made[index] = declared.Default[index];
        }

        return made;
    }

    /// <summary>A copy of one table with a port's lanes set, or taken out when they are the default.</summary>
    /// <param name="stored">What the layer holds.</param>
    /// <param name="port">Which port.</param>
    /// <param name="declared">Its declaration, whose default decides whether the key is kept.</param>
    /// <param name="lanes">What the fields say.</param>
    /// <returns>A new dictionary.</returns>
    /// <remarks>
    ///     ⚠ <b>A lane back at the port's own default takes the key out.</b> Keeping it would make
    ///     opening the panel and nudging a field back where it was a change to the file — and worse,
    ///     it would <em>pin</em> the number: a node type that improved its default would leave every
    ///     stack ever touched holding the old one, with nothing anywhere saying why.
    /// </remarks>
    static Dictionary<string, float[]> Numbered(
        Dictionary<string, float[]> stored,
        string port,
        PortDefinition declared,
        float[] lanes
    ) {
        Dictionary<string, float[]> next = new(stored, StringComparer.Ordinal);

        var fallback = Default(declared, lanes.Length);
        var same = declared.Default.Length > 0;

        for (var index = 0; same && index < lanes.Length; index++) {
            same = lanes[index] == fallback[index];
        }

        if (same) {
            next.Remove(port);
        } else {
            next[port] = lanes;
        }

        return next;
    }

    /// <summary>A copy of one table with a setting set, or taken out when the text is empty.</summary>
    /// <remarks>
    ///     ⚠ <b>Empty removes the key rather than storing <c>""</c>.</b> Both
    ///     <c>LayerStackGraph.Published</c> and <c>Effect</c> write whatever is there into the node,
    ///     so an empty string is a setting the node reads as empty rather than as unset — and
    ///     clearing the box is how an artist goes back to the type's own default.
    /// </remarks>
    static Dictionary<string, string> Named(Dictionary<string, string> stored, string setting, string text) {
        Dictionary<string, string> next = new(stored, StringComparer.Ordinal);

        if (text.Length == 0) {
            next.Remove(setting);
        } else {
            next[setting] = text;
        }

        return next;
    }

    /// <summary>One channel's constant or image, on a fill layer.</summary>
    /// <param name="document">The stack being edited.</param>
    /// <param name="set">The texture set, for the channel's own default.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="channel">Which channel.</param>
    /// <param name="depth">How far in to indent.</param>
    /// <remarks>
    ///     ⚠ <b>Every control is created and the binding decides which are on screen</b> — see
    ///     <see cref="FillRows" /> for why that is a correctness rule here rather than a saving.
    /// </remarks>
    void ChannelRow(
        LayerStackDocument document,
        TextureSetAsset set,
        LayerPath path,
        ChannelAsset channel,
        int depth
    ) {
        var usage = channel.Usage;
        var row = rows.Add("layer-stack-fill-channel");

        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        row.Add("layer-stack-fill-usage").Text = usage;

        var writes = row.Add<CheckBox>(null, null, "layer-stack-fill-writes");

        writes.Label = usage;

        writes.CheckedChanged += (_, on) => Set(
            document,
            path,
            current => current with { Values = Valued(current, usage, on ? Starting(channel) : null) },
            on ? "Give Channel a Colour" : "Clear Channel's Colour"
        );

        List<NumericInput> components = [];

        for (var index = 0; index < 4; index++) {
            var component = index;
            // These four were the first controls in the panel to be named by class, and #1071 is
            // the other twenty-eight following them.
            var field = row.Add<NumericInput>(null, null, ComponentClasses[index]);

            // ⚠ A floor and no ceiling, and that is the whole of
            // <a href="https://github.com/Rikarin/Vixen/issues/1004">#1004</a>. The renderer works
            // in cd/m² and an emissive fill of 4 is an ordinary thing for a `.vxlayers` to hold, so
            // a control whose top end is 1 can hold that value and cannot produce it. Nought is a
            // real floor rather than a matching guess: a negative radiance is not a quantity, and a
            // field that reports it out of range is what the arrow keys stop at.
            field.Minimum = 0d;

            // Three places, matching `PropertyGrid`'s non-integral fields. ⚠ Not the default, which
            // is *nought*: the text a `Decimals = 0` field writes for 0.25 is "0", and the next
            // submit reads that back — so the panel would quietly round every colour it displayed
            // to an integer the first time anybody pressed Return in it.
            field.Decimals = 3;

            // A hundredth, because `Step` is also the floor of the scrub rate and a step of one —
            // the default — is the whole of a 0…1 channel per arrow press.
            field.Step = 0.01d;

            field.NumberChanged += (typed, value) => {
                // ⚠ Gated on the field's own verdict, exactly as `PropertyGrid`'s numeric rows are
                // and for the same reason: this control holds and reports an out-of-range number
                // rather than clamping it, so an ungated write would put a negative component into
                // the document. A refused value stays in the field, ringed, where it can be
                // corrected.
                if (!typed.IsValid) {
                    return;
                }

                Set(
                    document,
                    path,

                    // ⚠ Read out of the document and rewrite one component, rather than gathering
                    // the four fields. It was load-bearing while these were sliders — a 4 arrived at
                    // a 0…1 control as a 1 — and it stays load-bearing for a *different* reason
                    // now, which is worth writing down because #1004's own account of it is wrong:
                    // a field does not clamp and `Decimals` rounds only the text, so gathering the
                    // four would write back exactly what it read and every value-preserving
                    // assertion in the suite stays green (checked by sabotage). What a gather would
                    // break is the refusal below — a field holding a number this row has just
                    // refused still has that number, and gathering it puts the negative into the
                    // document through the neighbour's edit.
                    current => {
                        if (!current.Values.TryGetValue(usage, out var colour)
                            || colour.Length != 4
                            || colour[component] == (float)value) {
                            return current;
                        }

                        var next = (float[])colour.Clone();

                        next[component] = (float)value;

                        return current with { Values = Valued(current, usage, next) };
                    },
                    "Set Fill Colour",
                    $"fill-colour:{path.Id}:{usage}:{component.ToString(CultureInfo.InvariantCulture)}"
                );
            };

            // The slider's own reason, unchanged from the opacity row and still true of a field that
            // scrubs: a drag is one undo entry and the release is what ends it, and
            // `handledEventsToo` is what makes this run at all.
            field.AddHandler<PointerEvent>(
                (_, args) => {
                    if (args.Action == PointerAction.Released) {
                        document.Stack.Seal();
                    }
                },
                RoutingStrategy.Bubble,
                handledEventsToo: true
            );

            // ⚠ And the seal a slider never needed: a number that is *typed* ends on Return rather
            // than on a pointer release, so without this the next keystroke in the next field would
            // coalesce into the same undo entry under a different key — or, worse, not coalesce and
            // leave the entry open until something else sealed it.
            field.Submitted += _ => document.Stack.Seal();

            components.Add(field);
        }

        var image = row.Add<TextBox>(null, null, "layer-stack-fill-texture");

        // ⚠ Empty text removes the entry rather than storing "", because the compiler answers a
        // texture fill naming no image with a refusal either way — so a blank field that left a key
        // behind would be a state the file can hold and nothing can mean.
        image.ValueChanged += (_, typed) => Set(
            document,
            path,
            current => current with { Textures = Imaged(current, usage, typed ?? "") },
            "Set Fill Image",
            "fill-image:" + path.Id + ":" + usage
        );

        image.Submitted += _ => document.Stack.Seal();

        bindings.Add(() => {
            if (LayerStackEdit.Find(document.Document, path) is not { } current) {
                return;
            }

            var written = current.Writes(usage);
            var constant = current.Fill == LayerFillSource.Constant;
            var texture = current.Fill == LayerFillSource.Texture;

            row.SetStyle("display", written && (constant || texture) ? "flex" : "none");

            var colour = current.Values.TryGetValue(usage, out var stored) && stored.Length == 4 ? stored : null;

            writes.IsChecked = colour is not null;
            writes.SetStyle("display", constant ? "flex" : "none");

            for (var index = 0; index < components.Count; index++) {
                components[index].SetStyle("display", constant && colour is not null ? "flex" : "none");

                if (colour is not null) {
                    components[index].Number = colour[index];
                }
            }

            image.SetStyle("display", texture ? "flex" : "none");
            image.Value = current.Textures.TryGetValue(usage, out var named) ? named : "";
        });
    }

    /// <summary>What each of a colour's four fields is classed on the tree.</summary>
    /// <remarks>
    ///     Written out rather than indexed into a single name, because a test reading "the red field"
    ///     off the panel should be naming red rather than counting.
    /// </remarks>
    static readonly string[] ComponentClasses = [
        "layer-stack-fill-red",
        "layer-stack-fill-green",
        "layer-stack-fill-blue",
        "layer-stack-fill-alpha"
    ];

    /// <summary>What switching a channel on starts from.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>Four numbers.</returns>
    /// <remarks>
    ///     ⚠ <b>The channel's own default, which is exactly the number #807 · 2 refused as a
    ///     <em>fallback</em>.</b> The two are opposite decisions about the same value: reading it
    ///     when there is no entry invents a layer's opinion out of the set's, while starting an entry
    ///     the artist just asked for is the set's opinion offered as a first draft. A malformed
    ///     default becomes mid-grey, which is what <see cref="Blank" /> gives a new fill.
    /// </remarks>
    static float[] Starting(ChannelAsset channel) =>
        channel.Default.Length == 4 ? [.. channel.Default] : [0.5f, 0.5f, 0.5f, 1f];

    /// <summary>A layer's colours with one channel's set or taken out.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="usage">The channel.</param>
    /// <param name="colour">Four numbers, or null to remove the entry.</param>
    /// <returns>A new dictionary.</returns>
    /// <remarks>
    ///     ⚠ <b>A copy, and that is load-bearing twice over.</b> <c>LayerAsset</c> is a record whose
    ///     equality over a dictionary is by reference, so <see cref="Set" /> would compare a mutated
    ///     dictionary equal to itself and execute nothing; and <c>SetLayerCommand</c> holds the
    ///     before and the after, so a mutation in place would edit the undo entry as well as the
    ///     document.
    /// </remarks>
    static Dictionary<string, float[]> Valued(LayerAsset layer, string usage, float[]? colour) {
        Dictionary<string, float[]> values = new(layer.Values);

        if (colour is null) {
            values.Remove(usage);
        } else {
            values[usage] = colour;
        }

        return values;
    }

    /// <summary>A layer's images with one channel's set, or taken out when the name is empty.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="usage">The channel.</param>
    /// <param name="asset">What was typed.</param>
    /// <returns>A new dictionary.</returns>
    static Dictionary<string, string> Imaged(LayerAsset layer, string usage, string asset) {
        Dictionary<string, string> textures = new(layer.Textures);

        if (asset.Length == 0) {
            textures.Remove(usage);
        } else {
            textures[usage] = asset;
        }

        return textures;
    }

    /// <summary>The rows for one layer's mask: its effects, its entries, and its base.</summary>
    /// <remarks>
    ///     ⚠ <b>Outermost first, which is the same rule as the layer list and the reverse of the
    ///     file.</b> <c>MaskAsset.Effects</c> run in list order, each over the result of the one
    ///     before, so the last of them is the outermost; <c>MaskAsset.Layers</c> composite bottom
    ///     first over the base. Reading down the panel is therefore reading backwards through the
    ///     arithmetic, exactly as it is for the layers — a mask pane that listed its entries in file
    ///     order beside a layer list that did not would be two orders in one list.
    /// </remarks>
    void MaskRows(LayerStackDocument document, TextureSetAsset set, LayerAsset layer, int depth) {
        LayerPath path = new(set.Name, layer.Id);
        var mask = layer.Mask;

        for (var index = mask.Effects.Count - 1; index >= 0; index--) {
            var position = index;

            var effect = MaskRow(
                depth,
                () => Describe(document, path, effect: position),
                () => LayerStackEdit.Find(document.Document, path)?.Mask.Effects is { } effects
                    && position < effects.Count && effects[position].Enabled,
                value => Set(
                    document,
                    path,
                    current => current with { Mask = ToggleEffect(current.Mask, position, value) },
                    value ? "Show Mask Effect" : "Hide Mask Effect"
                )
            );

            // ⚠ A node path field, and it is what stops the *add* button being a mechanism whose
            // caller passes the default. `MaskEffectAsset.Node` is the whole of what an effect is —
            // "any single-input graph", named by its path in the node menu — so an add with no field
            // to type into offers a row an artist can create and cannot make mean anything.
            var node = effect.Add<TextBox>(null, null, "layer-stack-effect-node");

            node.Placeholder = "Colour/Levels";

            node.ValueChanged += (_, typed) => Set(
                document,
                path,
                current => current with { Mask = WithEffect(current.Mask, position, typed ?? "") },
                "Set Mask Effect Node",
                "mask-effect:" + position.ToString(CultureInfo.InvariantCulture)
            );

            node.Submitted += _ => document.Stack.Seal();

            var remove = effect.Add<Button>(null, null, "layer-stack-effect-delete");

            remove.Label = "Delete";
            remove.Clicked += _ => Set(
                document,
                path,
                current => current with { Mask = WithoutEffect(current.Mask, position) },
                "Delete Mask Effect"
            );

            bindings.Add(() => {
                if (LayerStackEdit.Find(document.Document, path)?.Mask.Effects is { } effects
                    && position < effects.Count) {
                    node.Value = effects[position].Node;
                }
            });

            if (document.Library.Registry.TryGet(mask.Effects[position].Node.Trim(), out var type)) {
                KnobRows(
                    document,
                    path,
                    type,
                    depth + 1,
                    "Mask Effect",
                    $"mask-effect:{layer.Id}:{position.ToString(CultureInfo.InvariantCulture)}",
                    current => Effect(current, position).Values,
                    current => Effect(current, position).Texts,
                    (current, declared, lanes) => current with {
                        Mask = WithEffectValues(
                            current.Mask,
                            position,
                            Numbered(Effect(current, position).Values, declared.Name, declared, lanes)
                        )
                    },
                    (current, setting, text) => current with {
                        Mask = WithEffectTexts(
                            current.Mask,
                            position,
                            Named(Effect(current, position).Texts, setting, text)
                        )
                    }
                );
            }
        }

        for (var index = mask.Layers.Count - 1; index >= 0; index--) {
            var position = index;

            var entry = MaskRow(
                depth,
                () => Describe(document, path, entry: position),
                () => LayerStackEdit.Find(document.Document, path)?.Mask.Layers is { } entries
                    && position < entries.Count && entries[position].Enabled,
                value => Set(
                    document,
                    path,
                    current => current with { Mask = ToggleEntry(current.Mask, position, value) },
                    value ? "Show Mask Entry" : "Hide Mask Entry"
                )
            );

            SourceEditor(
                entry,
                document,
                set,
                path,
                position.ToString(CultureInfo.InvariantCulture),
                () => LayerStackEdit.Find(document.Document, path)?.Mask.Layers is { } entries
                    && position < entries.Count
                        ? Read(entries[position])
                        : null,
                (value, name, key) => Set(
                    document,
                    path,
                    current => current with { Mask = WithEntry(current.Mask, position, value) },
                    name,
                    key
                )
            );

            var remove = entry.Add<Button>(null, null, "layer-stack-entry-delete");

            remove.Label = "Delete";
            remove.Clicked += _ => Set(
                document,
                path,
                current => current with { Mask = WithoutEntry(current.Mask, position) },
                "Delete Mask Entry"
            );
        }

        // ⚠ The base row is drawn whatever the source is, and that is the change #882 asked for
        // rather than a longer list for its own sake. Switching a base off means setting its source
        // to `None`, and a row that then vanished would be a trapdoor: an artist could take a mask
        // off a layer and never put one back. A mask slot on every layer is what both references do.
        var row = rows.Add("layer-stack-mask-row");

        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        // ⚠ The base has no `Enabled` of its own and therefore no tick, which is a fact about
        // `MaskAsset` rather than an omission here: its source, its value and its asset are the
        // mask's own members, kept flat because every `.vxlayers` that exists names a mask that way.
        var name = row.Add("layer-stack-mask-name");

        bindings.Add(() => name.Text = Describe(document, path));

        // ⚠ The two adds live on the base row, which is the one row of a mask that always exists —
        // an entry row is a thing there may be none of, and hanging "add another" off it would make
        // the first one unreachable. They are here rather than on the layer row because what they
        // add belongs to the mask.
        var addEntry = row.Add<Button>(null, null, "layer-stack-mask-add-entry");

        addEntry.Label = "Add mask entry";
        addEntry.Clicked += _ => Set(
            document,
            path,
            current => current with { Mask = WithEntryAdded(current.Mask) },
            "Add Mask Entry"
        );

        var addEffect = row.Add<Button>(null, null, "layer-stack-mask-add-effect");

        addEffect.Label = "Add effect";
        addEffect.Clicked += _ => Set(
            document,
            path,
            current => current with { Mask = WithEffectAdded(current.Mask) },
            "Add Mask Effect"
        );

        SourceEditor(
            row,
            document,
            set,
            path,
            "base",
            () => LayerStackEdit.Find(document.Document, path)?.Mask is { } current ? Read(current) : null,
            (value, undo, key) => Set(
                document,
                path,
                current => current with { Mask = WithBase(current.Mask, value) },
                undo,
                key
            )
        );
    }

    /// <summary>What a mask base reads, as the value a source editor writes back.</summary>
    static MaskSourceEdit Read(MaskAsset mask) =>
        new(mask.Source, mask.Value, mask.Asset, mask.Anchor, mask.Generator, mask.Map);

    /// <summary>What one mask entry reads, as the value a source editor writes back.</summary>
    static MaskSourceEdit Read(MaskLayerAsset entry) =>
        new(entry.Source, entry.Value, entry.Asset, entry.Anchor, entry.Generator, entry.Map);

    /// <summary>A mask whose base reads something else.</summary>
    static MaskAsset WithBase(MaskAsset mask, MaskSourceEdit value) =>
        mask with {
            Source = value.Source,
            Value = value.Value,
            Asset = value.Asset,
            Anchor = value.Anchor,
            Generator = value.Generator,
            Map = value.Map
        };

    /// <summary>A mask one of whose entries reads something else.</summary>
    /// <remarks>
    ///     ⚠ A new list, for <see cref="ToggleEntry" />'s reason: <c>with</c> shares every collection
    ///     member, so writing into the one this mask holds would change the layer the undo entry is
    ///     holding as its before-image.
    /// </remarks>
    static MaskAsset WithEntry(MaskAsset mask, int index, MaskSourceEdit value) {
        if (index < 0 || index >= mask.Layers.Count) {
            return mask;
        }

        List<MaskLayerAsset> entries = [.. mask.Layers];

        entries[index] = entries[index] with {
            Source = value.Source,
            Value = value.Value,
            Asset = value.Asset,
            Anchor = value.Anchor,
            Generator = value.Generator,
            Map = value.Map
        };

        return mask with { Layers = entries };
    }

    /// <summary>A mask with one more entry over the top of its stack.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Appended, which is the <em>top</em> of the mask's stack and the row the panel
    ///         draws first.</b> <c>MaskAsset.Layers</c> composite bottom first, so the last of them is
    ///         the outermost — the same reversal every list in this panel spends once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Multiply and not <c>MaskLayerAsset</c>'s own default, and the exception is the
    ///         first entry over no base.</b> A mask entry's default operator is <c>Copy</c>, which at
    ///         a value of 1 <em>replaces</em> whatever is under it with white — so an artist who
    ///         pressed this on a bake mask would watch the mask they were building disappear. Multiply
    ///         at 1 changes nothing, which is what adding an unconfigured row should do. The one case
    ///         that wants <c>Copy</c> is an entry with nothing beneath it at all, because
    ///         <c>MaskAsset.Layers</c>' own remarks say the bottom entry's operator does nothing and
    ///         is <em>warned about</em> — so the neutral choice there would be a warning on a row the
    ///         artist has not touched yet.
    ///     </para>
    /// </remarks>
    static MaskAsset WithEntryAdded(MaskAsset mask) {
        var bottom = mask.Layers.Count == 0 && mask.Source == LayerMaskSource.None;

        return mask with {
            Layers = [
                .. mask.Layers,
                new MaskLayerAsset { Blend = bottom ? LayerBlendMode.Copy : LayerBlendMode.Multiply }
            ]
        };
    }

    /// <summary>A mask with one fewer entry.</summary>
    static MaskAsset WithoutEntry(MaskAsset mask, int index) {
        if (index < 0 || index >= mask.Layers.Count) {
            return mask;
        }

        List<MaskLayerAsset> entries = [.. mask.Layers];

        entries.RemoveAt(index);

        return mask with { Layers = entries };
    }

    /// <summary>A mask with one more effect over its result.</summary>
    /// <remarks>
    ///     ⚠ <b>Switched off, and that is the difference between adding a row and breaking the
    ///     picture.</b> <c>LayerStackGraph</c> <em>refuses</em> an effect that names no node type —
    ///     an error, which stops the map — and an effect with no node is exactly what pressing this
    ///     makes. So it is added disabled: the row appears with a field to type a node path into, and
    ///     the tick beside it is the second half of the gesture. An effect added enabled would blank
    ///     the preview the artist was looking at, on a click that was meant to be additive.
    /// </remarks>
    static MaskAsset WithEffectAdded(MaskAsset mask) =>
        mask with { Effects = [.. mask.Effects, new MaskEffectAsset { Enabled = false }] };

    /// <summary>A mask with one fewer effect.</summary>
    static MaskAsset WithoutEffect(MaskAsset mask, int index) {
        if (index < 0 || index >= mask.Effects.Count) {
            return mask;
        }

        List<MaskEffectAsset> effects = [.. mask.Effects];

        effects.RemoveAt(index);

        return mask with { Effects = effects };
    }

    /// <summary>One of a layer's mask effects, or an empty one when the index has gone.</summary>
    /// <param name="layer">The layer.</param>
    /// <param name="index">Which effect.</param>
    /// <returns>The effect.</returns>
    /// <remarks>
    ///     ⚠ <b>An empty effect rather than a null, because every caller is inside a
    ///     <c>Set</c> callback and a null there is a crash from a control the artist is holding.</b>
    ///     An effect can go while its rows are on screen — a delete on another row, an undo from the
    ///     keyboard — and what the write then does is put a number on a table nothing reads, which
    ///     <c>Set</c> discards as an unchanged layer.
    /// </remarks>
    static MaskEffectAsset Effect(LayerAsset layer, int index) =>
        index >= 0 && index < layer.Mask.Effects.Count ? layer.Mask.Effects[index] : new MaskEffectAsset();

    /// <summary>A mask one of whose effects carries different numbers.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="index">Which effect.</param>
    /// <param name="values">Its numbers by port.</param>
    /// <returns>A new mask.</returns>
    static MaskAsset WithEffectValues(MaskAsset mask, int index, Dictionary<string, float[]> values) {
        if (index < 0 || index >= mask.Effects.Count) {
            return mask;
        }

        List<MaskEffectAsset> effects = [.. mask.Effects];

        effects[index] = effects[index] with { Values = values };

        return mask with { Effects = effects };
    }

    /// <summary>A mask one of whose effects carries different settings.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="index">Which effect.</param>
    /// <param name="texts">Its settings by name.</param>
    /// <returns>A new mask.</returns>
    static MaskAsset WithEffectTexts(MaskAsset mask, int index, Dictionary<string, string> texts) {
        if (index < 0 || index >= mask.Effects.Count) {
            return mask;
        }

        List<MaskEffectAsset> effects = [.. mask.Effects];

        effects[index] = effects[index] with { Texts = texts };

        return mask with { Effects = effects };
    }

    /// <summary>A mask one of whose effects names a different node type.</summary>
    /// <param name="mask">The mask.</param>
    /// <param name="index">Which effect.</param>
    /// <param name="node">The node type's path.</param>
    /// <returns>A new mask.</returns>
    /// <remarks>
    ///     ⚠ A new list, for <see cref="WithEntry" />'s reason: <c>with</c> shares every collection
    ///     member, so writing into the one this mask holds would change the layer the undo entry is
    ///     holding as its before-image.
    /// </remarks>
    static MaskAsset WithEffect(MaskAsset mask, int index, string node) {
        if (index < 0 || index >= mask.Effects.Count) {
            return mask;
        }

        List<MaskEffectAsset> effects = [.. mask.Effects];

        effects[index] = effects[index] with { Node = node };

        return mask with { Effects = effects };
    }

    /// <summary>What no anchor reads, as the picker's first option.</summary>
    /// <remarks>
    ///     The mesh picker's <see cref="NoMesh" /> argument, one level down: a dropdown whose first
    ///     entry is a real layer makes "anchored at nothing" unreachable the moment a stack has two.
    /// </remarks>
    public const string NoAnchor = "(none)";

    /// <summary>The layers of a set an anchor on one layer may name, in composite order.</summary>
    /// <param name="set">The texture set.</param>
    /// <param name="id">The <see cref="LayerAsset.Id" /> doing the anchoring.</param>
    /// <returns>Every id whose result exists before this layer's does.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="set" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Post-order, because that is the order results are emitted in.</b>
    ///         <c>LayerStackGraph.Stack</c> composites a list bottom first and a group's children
    ///         <em>inside</em> the group's own composite — so a group's blend node exists only after
    ///         every child's does. A picker built on the panel's own top-to-bottom row order would
    ///         offer a group to its own children, which is a cycle.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Strictly before, and the refusal says why:</b> "an anchor onto a layer at or above
    ///         its own is a loop, and the graph model is what says so". So the picker offers what the
    ///         model would accept rather than everything and a refusal afterwards — a dropdown that
    ///         lists an option which always fails is a dropdown that lied.
    ///     </para>
    ///     <para>
    ///         An id no layer can be addressed by is left out for the same reason: an empty one names
    ///         nothing, and one <c>LayerStackEdit.Ambiguous</c> reports names more than one.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Anchorable(TextureSetAsset set, string id) {
        ArgumentNullException.ThrowIfNull(set);

        var ambiguous = LayerStackEdit.Ambiguous(set);
        List<string> before = [];
        var reached = false;

        Walk(set.Layers);

        return before;

        void Walk(List<LayerAsset> layers) {
            foreach (var layer in layers) {
                if (reached) {
                    return;
                }

                Walk(layer.Children);

                if (reached) {
                    return;
                }

                if (string.Equals(layer.Id, id, StringComparison.Ordinal)) {
                    reached = true;

                    return;
                }

                if (layer.Id.Length > 0 && !ambiguous.Contains(layer.Id)) {
                    before.Add(layer.Id);
                }
            }
        }
    }

    /// <summary>The controls that change what one mask row reads, and the one that says which.</summary>
    /// <param name="row">The row they go on.</param>
    /// <param name="document">The stack being edited.</param>
    /// <param name="set">The texture set the row's layer is in — what an anchor picker offers from.</param>
    /// <param name="path">Which layer.</param>
    /// <param name="slot">
    ///     What tells two rows of one layer apart in a merge key: an entry's index, or <c>base</c>.
    /// </param>
    /// <param name="read">The row's current source, or <see langword="null" /> when it is gone.</param>
    /// <param name="write">Puts one back, with the undo entry's name and its merge key.</param>
    /// <remarks>
    ///     <para>
    ///         <b><a href="https://github.com/Rikarin/Vixen/issues/882">#882</a>, and the issue's own
    ///         warning is the shape: a source editor is not one control.</b> The discriminator is a
    ///         <c>Select</c>; behind it a constant wants a number, an anchor wants a picker over the
    ///         layers below it, and a texture, a generator and a bake each want a reference that is a
    ///         name. All of them are built and all but the relevant one is hidden, rather than the
    ///         row being rebuilt when the source changes — a rebuild while a slider is captured is
    ///         the defect <see cref="Show" />'s shape comparison exists to prevent, one level down.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A bake is a picker and no longer a text box —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/964">#964</a>.</b> It used to share
    ///         the reference <c>TextBox</c> with a texture and a generator, because the nine names it
    ///         may hold were <c>internal</c> to <c>Vixen.Editor.TextureGraph</c> and this assembly
    ///         could not ask for them — and writing the nine here would have been the second
    ///         transcription of a known set that five roll calls in this workstream have gone red on.
    ///         What crosses that wall now is the node type's own declaration, through
    ///         <see cref="TextureNodeLibrary.MeshMaps" />: one list, offered here and refused by the
    ///         node, so the picker and the diagnostic cannot disagree.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A texture and a generator still share the <c>TextBox</c>, and that is still a
    ///         limit.</b> A generator's options are the published compounds, which a library this
    ///         view must not acquire produces (<a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>);
    ///         a texture's are a project's files. Neither is a fact about the build, so neither is
    ///         reachable the way the nine now are.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A stored map the list does not hold stays on the screen</b>, which is
    ///         <c>Rebind</c>'s three-state rule and the anchor picker's below: a dropdown that
    ///         silently showed the first option would say the mask measures that, and then write it
    ///         on the next click.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A keystroke is one undo entry per typing run, not per character</b> — the merge
    ///         key the slider taught, keyed by the row so that two entries of one layer do not
    ///         collapse into each other. Enter seals it; so does releasing the slider.
    ///     </para>
    /// </remarks>
    void SourceEditor(
        UiElement row,
        LayerStackDocument document,
        TextureSetAsset set,
        LayerPath path,
        string slot,
        Func<MaskSourceEdit?> read,
        Action<MaskSourceEdit, string, string> write
    ) {
        var kind = row.Add<Select>(null, null, "layer-stack-mask-source");

        foreach (var source in Enum.GetValues<LayerMaskSource>()) {
            kind.AddOption(source.ToString());
        }

        kind.SelectionChanged += (_, chosen) => {
            if (read() is { } current && Enum.TryParse<LayerMaskSource>(chosen, out var source)) {
                write(current with { Source = source }, "Set Mask Source", "");
            }
        };

        var number = row.Add<Slider>(null, null, "layer-stack-mask-value");

        number.Minimum = 0f;
        number.Maximum = 1f;

        number.ValueChanged += (_, value) => {
            if (read() is { } current) {
                write(current with { Value = value }, "Set Mask Value", "mask-value:" + slot);
            }
        };

        number.AddHandler<PointerEvent>(
            (_, args) => {
                if (args.Action == PointerAction.Released) {
                    document.Stack.Seal();
                }
            },
            RoutingStrategy.Bubble,
            handledEventsToo: true
        );

        var anchor = row.Add<Select>(null, null, "layer-stack-mask-anchor");

        anchor.SelectionChanged += (_, chosen) => {
            if (read() is not { } current) {
                return;
            }

            var wanted = chosen is null || string.Equals(chosen, NoAnchor, StringComparison.Ordinal) ? "" : chosen;

            write(current with { Anchor = wanted }, "Set Mask Anchor", "");
        };

        var map = row.Add<Select>(null, null, "layer-stack-mask-map");

        foreach (var measurement in TextureNodeLibrary.MeshMaps) {
            map.AddOption(measurement);
        }

        map.SelectionChanged += (_, chosen) => {
            if (read() is { } current && chosen is not null) {
                write(current with { Map = chosen }, "Set Mask Map", "");
            }
        };

        var reference = row.Add<TextBox>(null, null, "layer-stack-mask-text");

        reference.ValueChanged += (_, typed) => {
            if (read() is not { } current) {
                return;
            }

            var written = typed ?? "";

            var after = current.Source switch {
                LayerMaskSource.Texture => current with { Asset = written },
                LayerMaskSource.Generator => current with { Generator = written },
                _ => current
            };

            write(after, "Set Mask Reference", "mask-reference:" + slot);
        };

        reference.Submitted += _ => document.Stack.Seal();

        // ⚠ The *walk* is cached and not only the options, which is #979: this used to compute
        // `Anchorable` and join its result into a key on every refresh and then compare the key, so
        // the guard skipped `ClearOptions` and paid for two tree walks and three collections anyway
        // — once per anchor-masked row per frame of an opacity drag. What a row may anchor onto is
        // decided by the set's ids and their composite order, and every change to either goes
        // through `Shape` and rebuilds this row, so for the life of the row it is a constant.
        // Computed on first need rather than at build time, because a row whose source is not
        // `Anchor` never asks.
        IReadOnlyList<string>? targets = null;

        // What the picker was last offered, so the options are not rebuilt per refresh — `Rebind`'s
        // argument, and `ClearOptions` under an open dropdown is the same defect. ⚠ Null rather
        // than an empty string, because "" is what a row with no unofferable anchor legitimately
        // has: a sentinel a value can produce turns a comparison into a coincidence.
        string? offered = null;

        bindings.Add(() => {
            if (read() is not { } current) {
                return;
            }

            kind.Value = current.Source.ToString();

            number.SetStyle("display", current.Source == LayerMaskSource.Constant ? "flex" : "none");
            anchor.SetStyle("display", current.Source == LayerMaskSource.Anchor ? "flex" : "none");

            reference.SetStyle(
                "display",
                current.Source is LayerMaskSource.Texture or LayerMaskSource.Generator ? "flex" : "none"
            );

            map.SetStyle("display", current.Source == LayerMaskSource.Bake ? "flex" : "none");

            number.Value = current.Value;

            reference.Value = current.Source switch {
                LayerMaskSource.Texture => current.Asset,
                LayerMaskSource.Generator => current.Generator,
                _ => ""
            };

            reference.Placeholder = current.Source switch {
                LayerMaskSource.Texture => "Assets/Textures/rust.png",
                LayerMaskSource.Generator => "Generators/Dirt",
                _ => ""
            };

            if (current.Source == LayerMaskSource.Bake) {
                // ⚠ Offered rather than dropped, the anchor picker's rule one control along: a map
                // this build does not bake is still what the stack says, and the refusal beneath the
                // rows is what says it is wrong.
                if (current.Map.Length > 0 && !map.Options.Any(option => option.Value == current.Map)) {
                    map.AddOption(current.Map);
                }

                map.Value = current.Map.Length > 0 ? current.Map : null;
                map.Placeholder = current.Map.Length > 0 ? null : "curvature";
            }

            if (current.Source != LayerMaskSource.Anchor) {
                return;
            }

            if (targets is null) {
                targets = Anchorable(set, path.Id);
                AnchorWalks++;
            }

            // ⚠ A stored anchor this stack cannot offer is kept as an option rather than dropped,
            // which is `Rebind`'s three-state rule: a picker that silently showed `(none)` would say
            // the mask is unanchored and then unanchor it on the next click. What the anchor really
            // is stays on the screen, and the refusal beneath the rows is what says it is wrong.
            // It is also the only part of the list that can change without the row being rebuilt —
            // the anchor is a value and `Shape` deliberately holds no values — so it is the whole
            // of what the guard below compares.
            var unofferable = current.Anchor.Length > 0
                && !targets.Contains(current.Anchor, StringComparer.Ordinal)
                    ? current.Anchor
                    : "";

            if (offered is null || !string.Equals(unofferable, offered, StringComparison.Ordinal)) {
                offered = unofferable;

                anchor.ClearOptions();
                anchor.AddOption(NoAnchor);

                foreach (var target in targets) {
                    anchor.AddOption(target);
                }

                if (unofferable.Length > 0) {
                    anchor.AddOption(unofferable);
                }
            }

            anchor.Value = current.Anchor.Length > 0 ? current.Anchor : NoAnchor;
        });
    }

    /// <summary>One switchable mask row — an effect, or an entry a source editor is added to.</summary>
    /// <returns>The row, so that a caller with more to put on it can.</returns>
    UiElement MaskRow(
        int depth,
        Func<string> describe,
        Func<bool> enabled,
        Action<bool> toggle
    ) {
        var row = rows.Add("layer-stack-mask-row");

        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        var tick = row.Add<CheckBox>(null, null, "layer-stack-mask-enabled");

        tick.Label = "Enabled";
        tick.CheckedChanged += (_, value) => toggle(value);

        var name = row.Add("layer-stack-mask-name");

        bindings.Add(() => {
            name.Text = describe();
            tick.IsChecked = enabled();
        });

        return row;
    }

    /// <summary>One mask row's sentence: the base, one entry, or one effect.</summary>
    static string Describe(LayerStackDocument document, LayerPath path, int entry = -1, int effect = -1) {
        if (LayerStackEdit.Find(document.Document, path) is not { } layer) {
            return "";
        }

        var mask = layer.Mask;

        if (effect >= 0) {
            if (effect >= mask.Effects.Count) {
                return "";
            }

            var value = mask.Effects[effect];

            return $"Mask effect — {(value.Node.Length > 0 ? value.Node : "(none)")}"
                + (value.Enabled ? "" : ", off");
        }

        if (entry >= 0) {
            if (entry >= mask.Layers.Count) {
                return "";
            }

            var value = mask.Layers[entry];

            return string.Create(
                CultureInfo.InvariantCulture,
                $"Mask — {Source(value.Source, value.Value, value.Asset, value.Anchor, value.Generator, value.Map)}, "
                + $"{value.Blend}, {value.Opacity:0.##}{(value.Enabled ? "" : ", off")}"
            );
        }

        return "Mask base — "
            + Source(mask.Source, mask.Value, mask.Asset, mask.Anchor, mask.Generator, mask.Map);
    }

    /// <summary>What a mask source reads, said the way the source itself names it.</summary>
    static string Source(
        LayerMaskSource source,
        float value,
        string asset,
        string anchor,
        string generator,
        string map
    ) =>
        source switch {
            LayerMaskSource.Constant => string.Create(CultureInfo.InvariantCulture, $"Constant {value:0.##}"),
            LayerMaskSource.Texture => $"Texture '{asset}'",
            LayerMaskSource.Anchor => $"Anchor on '{anchor}'",
            LayerMaskSource.Generator => $"Generator '{generator}'",
            LayerMaskSource.Bake => $"Bake '{map}'",
            _ => source.ToString()
        };

    static MaskAsset ToggleEntry(MaskAsset mask, int index, bool enabled) {
        if (index < 0 || index >= mask.Layers.Count) {
            return mask;
        }

        // ⚠ A new list rather than a write into the one this mask holds. `with` shares every
        // collection member with the value it copied, so mutating in place would change the layer the
        // undo entry is holding as its before-image — and the undo would put back the new value.
        List<MaskLayerAsset> entries = [.. mask.Layers];

        entries[index] = entries[index] with { Enabled = enabled };

        return mask with { Layers = entries };
    }

    static MaskAsset ToggleEffect(MaskAsset mask, int index, bool enabled) {
        if (index < 0 || index >= mask.Effects.Count) {
            return mask;
        }

        List<MaskEffectAsset> effects = [.. mask.Effects];

        effects[index] = effects[index] with { Enabled = enabled };

        return mask with { Effects = effects };
    }

    /// <summary>
    ///     The channel list a tick box's new state means, or <see langword="null" /> when it would
    ///     mean none.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A list that covers every channel is stored as no list at all</b>, which is the
    ///         round trip that makes "unrestricted" reachable from the panel. An artist who clears a
    ///         tick and puts it back must end with the layer they started with, and a stack that
    ///         recorded the seven names instead would be one where a channel added to the set later
    ///         is silently not written — <see cref="LayerAsset.Channels" />' own argument, from the
    ///         other end.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is exactly why clearing the last tick is refused rather than stored.</b>
    ///         The two states are one value: a layer that writes nothing and a layer that writes
    ///         everything are both the empty list, and the empty list means everything. So the
    ///         gesture an artist reads as "and now it writes nothing" is the one that would make it
    ///         write all seven. A layer that should contribute nothing is switched off instead.
    ///     </para>
    /// </remarks>
    static List<string>? Restrict(TextureSetAsset set, LayerAsset layer, string usage, bool writes) {
        List<string> chosen = [];

        foreach (var channel in set.Channels) {
            var keep = string.Equals(channel.Usage, usage, StringComparison.OrdinalIgnoreCase)
                ? writes
                : layer.Writes(channel.Usage);

            if (keep) {
                chosen.Add(channel.Usage);
            }
        }

        if (chosen.Count == 0) {
            return null;
        }

        return chosen.Count == set.Channels.Count ? [] : chosen;
    }

    /// <summary>What the two pickers are showing, as one string, so a change to either is one test.</summary>
    /// <remarks>
    ///     The newline is a separator no path and no mesh name can contain, which is
    ///     <c>TexturingModule</c>'s own key one assembly along and the same argument: a sentinel a
    ///     value can produce turns a comparison into a coincidence.
    /// </remarks>
    static string Binding(LayerStackDocument document, string chosen) {
        StringBuilder key = new(document.Document.Model);

        // ⚠ Every set's name is in the key and not only the chosen one's, because this is also what
        // decides whether the *set* picker is refilled — a stack that gained or lost a set, or had
        // one renamed, changes nothing else on this row.
        foreach (var set in document.Document.Sets) {
            key.Append('\n').Append(set.Name);
        }

        return key
            .Append('\n')
            .Append(chosen)
            .Append('\n')
            .Append(LayerStackEdit.SetFor(document.Document, chosen)?.Mesh ?? "")
            .ToString();
    }

    /// <summary>Puts the project's models in the picker and the stack's own binding on it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Re-read when the project's models change, which is what
    ///         <a href="https://github.com/Rikarin/Vixen/issues/954">#954</a> found this did not
    ///         do.</b> Importing a model is an ordinary thing to do while a stack is open, and the
    ///         gate above this used to be the document reference and the bound path alone — while
    ///         the module hands the same reference to every refresh. So the mesh an artist had just
    ///         added was the one mesh the picker did not offer, which reads as the import having
    ///         failed. <c>LayerStackDocument.ModelsRevision</c> is the third term, and it is a
    ///         number rather than a walk because this walks every asset in the project and a show
    ///         runs on every edit — and a number rather than a flag because a flag its reader clears
    ///         has exactly one reader (#1006).
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A binding this build cannot offer is kept as an option rather than dropped.</b> A
    ///         stack whose model has been deleted or moved still names it, and a picker that silently
    ///         showed <see cref="NoMesh" /> for that would tell an artist the stack is unbound and
    ///         then rebind it to nothing on the next click. The row's status line is what says the
    ///         file is missing.
    ///     </para>
    /// </remarks>
    void Rebind(LayerStackDocument document) {
        var bound = document.Document.Model.Trim();

        writing = true;

        try {
            // ⚠ Filled here rather than per refresh, for the model picker's reason, and gated by the
            // same key — `Binding` carries every set's name so that a stack that gained, lost or
            // renamed one refills this. Disabled for a stack with one set, which is every stack that
            // exists today: a picker with one option is a statement rather than a choice.
            sets.ClearOptions();

            foreach (var set in document.Document.Sets) {
                sets.AddOption(set.Name);
            }

            sets.Value = ShownSet(document);
            sets.Disabled = document.Document.Sets.Count < 2;

            model.ClearOptions();
            model.AddOption(NoMesh);

            var offered = false;

            foreach (var entry in document.Project.Assets.Entries.OrderBy(
                         entry => entry.Path,
                         StringComparer.Ordinal
                     )) {
                if (!LayerStackMesh.Extensions.Contains(Path.GetExtension(entry.Path).ToLowerInvariant())) {
                    continue;
                }

                model.AddOption(entry.Path);
                offered |= string.Equals(entry.Path, bound, StringComparison.Ordinal);
            }

            if (bound.Length > 0 && !offered) {
                model.AddOption(bound);
            }

            model.Value = bound.Length > 0 ? bound : NoMesh;

            Parts(document);

            // ⚠ Three states rather than two, and `offered` is what separates the middle one. A
            // stack whose model was renamed, moved or deleted still names it, so the picker shows
            // the path and reads as bound — while every stroke is refused and no island is drawn.
            // Saying nothing there is the same silence as saying nothing about an unbound stack.
            meshStatus.Text = bound.Length == 0
                ? "Unbound: no islands, no coverage map, no 3D paint."
                : offered
                    ? ""
                    : $"'{bound}' is not in this project's assets, so there are no islands and every "
                    + "stroke is refused. Re-bind it, or restore the file.";
        } finally {
            writing = false;
        }
    }

    /// <summary>Binds the stack to a model, as one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the document's own command stack like every other edit in this panel.</b>
    ///     Binding a mesh changes which texels the brush will accept and which islands are drawn, so
    ///     it is exactly the kind of change an artist tries and takes back — and a gesture with no
    ///     undo is one a save might or might not carry, which is the argument
    ///     <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a> made about the rows.
    /// </remarks>
    void Bind(string value) {
        if (writing || Document is not { } document) {
            return;
        }

        var wanted = string.Equals(value, NoMesh, StringComparison.Ordinal) ? "" : value;

        if (string.Equals(wanted, document.Document.Model, StringComparison.Ordinal)) {
            return;
        }

        document.Stack.Execute(
            new SetModelCommand(document, wanted, wanted.Length == 0 ? "Unbind Mesh" : "Bind Mesh")
        );

        Refresh();
    }

    /// <summary>Puts the model's meshes in the part picker and the set's own narrowing on it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The names come from the sidecar and never from the model file, which is what
    ///         <a href="https://github.com/Rikarin/Vixen/issues/941">#941</a> assumed was impossible.</b>
    ///         That issue declined this control because offering the names means knowing them and
    ///         knowing them means an Assimp parse — <c>ModelReader.Read</c> on a hero asset is
    ///         seconds, and this runs from a panel build. It is not: an import writes the sub-asset
    ///         names it declared back into the <c>.meta</c>, so <c>LayerStackMesh.Names</c> is one
    ///         small YAML file and no geometry at all. The objection was true of the file and false
    ///         of the project.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A model whose import has not run offers nothing, and says so rather than
    ///         emptying the field.</b> There is nowhere but the file to read a name from before an
    ///         import, so the picker keeps whatever the set already names as its own option — a
    ///         control that silently showed <see cref="EveryMesh" /> would tell an artist the set is
    ///         un-narrowed and then un-narrow it on the next click, which is exactly the failure the
    ///         model picker's third state exists to prevent.
    ///     </para>
    /// </remarks>
    void Parts(LayerStackDocument document) {
        var set = LayerStackEdit.SetFor(document.Document, SetName);
        var narrowed = (set?.Mesh ?? "").Trim();

        part.ClearOptions();
        part.AddOption(EveryMesh);

        var offered = false;

        foreach (var name in LayerStackMesh.Names(document.Project, document.Document)) {
            part.AddOption(name);
            offered |= string.Equals(name, narrowed, StringComparison.Ordinal);
        }

        if (narrowed.Length > 0 && !offered) {
            part.AddOption(narrowed);
        }

        part.Value = narrowed.Length > 0 ? narrowed : EveryMesh;
        part.Disabled = set is null;
    }

    /// <summary>Narrows the shown set to one mesh, as one undo entry.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the document's command stack, like the binding above it.</b> Narrowing
    ///     changes which islands are drawn and which texels a stroke is allowed to reach, so it is
    ///     the same kind of change <see cref="Bind" /> is and takes the same answer.
    /// </remarks>
    void Narrow(string value) {
        if (writing || Document is not { } document) {
            return;
        }

        // ⚠ The chosen set's index and no longer a hard 0 — #927. `SetMeshCommand` is keyed by
        // position because a `TextureSetAsset` is a record the document replaces wholesale, so this
        // is the one place that has to turn the panel's choice back into one.
        var index = document.Document.Sets.FindIndex(
            one => string.Equals(one.Name, ShownSet(document), StringComparison.Ordinal)
        );

        if (index < 0) {
            return;
        }

        var wanted = string.Equals(value, EveryMesh, StringComparison.Ordinal) ? "" : value;

        if (string.Equals(wanted, document.Document.Sets[index].Mesh, StringComparison.Ordinal)) {
            return;
        }

        document.Stack.Execute(
            new SetMeshCommand(document, index, wanted, wanted.Length == 0 ? "Widen to Every Mesh" : "Narrow to Mesh")
        );

        Refresh();
    }

    /// <summary>The name of the set actually on the screen, after <c>SetFor</c>'s fallback.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <see cref="SetName" />, which is what was <em>asked for</em>.</b> The two differ
    ///     exactly when the choice names nothing — an empty one, or a set that has been renamed — and
    ///     that is the case where a command keyed by position would otherwise be keyed by a name
    ///     nothing answers to.
    /// </remarks>
    string ShownSet(LayerStackDocument document) =>
        LayerStackEdit.SetFor(document.Document, SetName)?.Name ?? "";

    /// <summary>Puts the panel on a different texture set.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not an undo entry, which is the same answer the layer selection gives.</b> It
    ///         changes nothing in the file — an artist who looked at another set and pressed Ctrl+Z
    ///         means to undo the last thing they <em>changed</em>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The layer selection goes with it, and the brush with the selection.</b> A
    ///         <see cref="LayerPath" /> carries a set name, so a selection made in one set means
    ///         nothing in another — and an id both sets happen to carry would leave the brush aimed
    ///         at a layer the artist is no longer looking at.
    ///     </para>
    /// </remarks>
    void ChooseSet(string value) {
        if (writing || string.Equals(value, SetName, StringComparison.Ordinal)) {
            return;
        }

        SetName = value;
        Selected = null;

        // ⚠ And the brush goes with it — #927. This is the one write that makes the picker mean
        // anything to a stroke: `PaintSurface.Open` resolves `LayerStackDocument.PaintSet` through
        // the same `SetFor` this view does, so "the set I am looking at" and "the set I am painting
        // into" stopped being two answers. Written unconditionally rather than only when the panel
        // has a brush, because the preview and the pane are two panels and either may be closed.
        if (Document is { } document) {
            document.PaintSet = value;
        }

        if (tool is not null) {
            tool.LayerId = "";
        }

        Refresh();
        SelectionChanged?.Invoke();
    }

    /// <summary>Makes a row the selected one, and mirrors it into the brush.</summary>
    /// <remarks>
    ///     ⚠ <b>Clicking the selected row clears the selection rather than doing nothing.</b> Empty
    ///     is a state with its own meaning — the brush takes the first paint layer — and a panel
    ///     that could enter a selection and never leave it would make that state unreachable after
    ///     the first click of a session.
    /// </remarks>
    void Choose(LayerPath path) {
        Selected = Selected == path ? null : path;

        // ⚠ The mirror, and it is the whole of what #910 asked for: `PaintTool.LayerId` was the only
        // writer of "which layer" and had no reader that a person could reach. An id that names no
        // paint layer is deliberately allowed through — `PaintSurface` refuses it by name.
        if (tool is not null) {
            tool.LayerId = Selected?.Id ?? "";
        }

        Restate();
        SelectionChanged?.Invoke();
    }

    void Restate() {
        writing = true;

        try {
            foreach (var binding in bindings) {
                binding();
            }
        } finally {
            writing = false;
        }
    }

    void Set(
        LayerStackDocument document,
        LayerPath path,
        Func<LayerAsset, LayerAsset> change,
        string name,
        string mergeKey = ""
    ) {
        if (writing || LayerStackEdit.Find(document.Document, path) is not { } before) {
            return;
        }

        var after = change(before);

        if (after == before) {
            return;
        }

        document.Stack.Execute(new SetLayerCommand(document, path, before, after, name, mergeKey));
        Refresh();
    }

    /// <summary>What a layer this panel just made starts as.</summary>
    /// <param name="set">The set it goes into — its first channel is what a fill writes.</param>
    /// <param name="kind">Which of the four.</param>
    /// <returns>The layer.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A <see cref="LayerKind.Fill" /> is given a value on the set's first channel, and
    ///         an empty <see cref="LayerAsset.Values" /> would have been the defect this workstream
    ///         names.</b> "A channel with no entry is a channel this layer does not write"
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/807">#807</a> · 2), so a fill added
    ///         with no values writes nothing at all — an artist presses <em>Add layer</em>, a row
    ///         appears, the picture does not change, and nothing on this panel says why. Mid-grey on
    ///         the first channel is <c>LayerStackDocument.Starter</c>'s own answer to the same
    ///         question.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is a starting value again rather than the only one</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/986">#986</a>. This paragraph used to
    ///         say there was no editor for a fill's colour, so the value a layer was born with was
    ///         the value it kept until somebody opened the <c>.vxlayers</c> in a text editor;
    ///         <see cref="FillRows" /> is that editor.
    ///     </para>
    ///     <para>
    ///         The id is <see cref="LayerStackEdit.FreeId" />'s, so it is unique in the set from the
    ///         moment the row exists — a layer added with the default empty id would be the second
    ///         id-less layer the instant somebody pressed the button twice, and both rows would go
    ///         to <see cref="AmbiguousRow" />.
    ///     </para>
    /// </remarks>
    static LayerAsset Blank(TextureSetAsset set, LayerKind kind) {
        LayerAsset layer = new() {
            Id = LayerStackEdit.FreeId(set, "layer"),
            Name = kind.ToString(),
            Kind = kind
        };

        if (kind != LayerKind.Fill || set.Channels.Count == 0) {
            return layer;
        }

        layer.Values[set.Channels[0].Usage] = [0.5f, 0.5f, 0.5f, 1f];

        return layer;
    }

    /// <summary>Puts a new layer over the selected one, or on top of the stack.</summary>
    /// <remarks>
    ///     ⚠ <b>Over the selected row and in <em>its</em> list, which is the only placement that can
    ///     reach the inside of a group.</b> A button that always appended to the set's own list would
    ///     make a group something an artist can open and never add to; the selection is already the
    ///     panel's word for "the layer I am working on", and it is what the brush reads. With nothing
    ///     selected the new layer goes on top, which is where a layers panel puts one.
    /// </remarks>
    void AddLayer() {
        if (writing || Document is not { } document
            || LayerStackEdit.SetFor(document.Document, SetName) is not { } set) {
            return;
        }

        var kind = Enum.TryParse<LayerKind>(addKind.Value, out var chosen) ? chosen : LayerKind.Fill;

        // ⚠ `Index + 1` is *over* the selected layer, because the file is bottom first and this panel
        // is not — the same reversal the `up` button spends, and getting it backwards puts every new
        // layer under the one an artist was pointing at.
        var slot = Selected is { } path && LayerStackEdit.SlotOf(document.Document, path) is { } at
            ? at with { Index = at.Index + 1 }
            : new LayerSlot(set.Name, null, set.Layers.Count);

        document.Stack.Execute(new AddLayerCommand(document, slot, Blank(set, kind), "Add " + kind + " Layer"));
        Refresh();
    }

    /// <summary>Takes a layer out.</summary>
    /// <remarks>
    ///     ⚠ <b>The selection goes with it when it was the selected layer, and the brush with the
    ///     selection.</b> <see cref="Build" /> recovers a selection from <c>PaintTool.LayerId</c> and
    ///     drops one no layer answers to, so leaving it would be harmless — but the drop happens on
    ///     the rebuild, and the refresh in between is a frame in which the brush is aimed at a layer
    ///     that is gone. An empty <c>LayerId</c> is the brush's "the first paint layer", which is
    ///     where a deleted selection honestly leaves it.
    /// </remarks>
    void RemoveLayer(LayerStackDocument document, LayerPath path) {
        if (writing || LayerStackEdit.Find(document.Document, path) is null) {
            return;
        }

        if (Selected == path) {
            Selected = null;

            if (tool is not null) {
                tool.LayerId = "";
            }

            SelectionChanged?.Invoke();
        }

        document.Stack.Execute(new RemoveLayerCommand(document, path, "Delete Layer"));
        Refresh();
    }

    void Move(LayerStackDocument document, LayerPath path, int delta, string name) {
        if (writing || !MoveLayerCommand.CanMove(document.Document, path, delta)) {
            return;
        }

        document.Stack.Execute(new MoveLayerCommand(document, path, delta, name));
        Refresh();
    }

    /// <summary>Follows a document's undo stack, so a change made anywhere else reaches these rows.</summary>
    /// <param name="document">The stack to follow, or <see langword="null" /> to stop following one.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><a href="https://github.com/Rikarin/Vixen/issues/933">#933</a>, and it is the
    ///         defect this panel's whole undoable model was built for.</b> Every edit a row makes
    ///         ends in <see cref="Refresh" />, and nothing else did — so Ctrl+Z, taken through the
    ///         editor's own verb or from any other panel, changed the document and left the
    ///         <c>Select</c>, the <c>Slider</c>, the ticks and the row order showing what was last
    ///         clicked. It survived because every test drove a control and then asserted on the
    ///         <em>document</em>, which is exactly the shape a panel that never reads the document
    ///         back still satisfies.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Here and not in <c>TexturingModule</c>, which is where the issue proposed it.</b>
    ///         <see cref="LayerStackEditorFactory" /> builds a view with no module at all — that is
    ///         the tab a double-click opens — so a subscription owned by the module would leave the
    ///         one route an artist reaches without opening a panel exactly as broken as before. The
    ///         view is also the thing that already knows which document its controls close over.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The depth is compared rather than the run counted, and a flag saying "not the
    ///         first run" is what this had and why it did not work.</b> An effect is <em>queued</em>
    ///         when it is created rather than executed — <c>EffectScheduler</c>'s first sentence — so
    ///         its first run is not the constructor, it is the first flush after one, which in a
    ///         panel that has just been built is the same frame as the artist's first undo. The flag
    ///         swallowed exactly the refresh it was meant to allow. <c>Depth</c> is also a
    ///         <c>Computed</c>, so an opacity drag — one merged command — moves the count once and
    ///         the equality short-circuit stops the effect being woken for the rest of the gesture.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The effect belongs to the host document's queue, not the thread's.</b>
    ///         <c>UiDocument.Effects</c> says why: an editor has several documents on one thread, and
    ///         flushing the thread's queue runs the bindings of every one of them including the
    ///         disposed. A view whose root has left the tree stops reading <c>Depth</c> altogether,
    ///         which drops the last edge and is what unsubscribes it — there is no teardown hook on a
    ///         panel factory to do it from.
    ///     </para>
    /// </remarks>
    void Watch(LayerStackDocument? document) {
        if (ReferenceEquals(watched, document)) {
            // Every refresh comes through here, so this is where "what is on the screen" is recorded.
            watchedDepth = document?.Stack.Depth.Peek() ?? 0;

            return;
        }

        watch?.Dispose();
        watch = null;
        watched = document;

        if (document is null || root.IsRemoved) {
            return;
        }

        watchedDepth = document.Stack.Depth.Peek();

        watch = new Effect(
            () => {
                if (root.IsRemoved) {
                    return;
                }

                var depth = document.Stack.Depth.Value;

                if (depth == watchedDepth) {
                    return;
                }

                watchedDepth = depth;
                Refresh();
            },
            root.Document.Effects
        );
    }

    /// <summary>Redraws after an edit this view made.</summary>
    /// <remarks>
    ///     Through <see cref="Edited" /> when somebody owns an evaluator, so that the picture catches
    ///     up as well as the rows; on our own otherwise, because a tab with no graphics still has to
    ///     show the layer it was just told to move.
    /// </remarks>
    void Refresh() {
        if (Edited is { } edited) {
            edited();

            return;
        }

        Show(Document, shown);
    }
}
