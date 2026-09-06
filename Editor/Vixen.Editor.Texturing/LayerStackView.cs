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

namespace Vixen.Editor.Texturing;

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
///         ⚠ <b>Built in C# rather than <c>.vxml</c>, and that is a debt.</b>
///         <c>TextureGraphView</c>'s reason unchanged: doc 36 § P4 makes markup the authoring path,
///         and porting is worth doing when the panel grows a form to edit with. It has now grown one,
///         so the debt is real rather than theoretical — <a
///         href="https://github.com/Rikarin/Vixen/issues/881">#881</a>. Both directions of the flex
///         are set explicitly, because <c>flex-direction</c> is <c>row</c> by CSS default and
///         <c>flex-grow</c> is not — a container that set neither is full width and no height, which
///         is the shape of "the panel is blank".
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

    readonly UiElement messages;
    readonly UiElement meshStatus;
    readonly UiElement root;
    readonly UiElement rows;
    readonly UiElement status;
    readonly UiElement title;

    /// <summary>The mesh picker. Its options are the project's models, and they are re-read per stack.</summary>
    readonly Select model;

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

    /// <summary>What the picker was last told the binding is, so a rebind is not per keystroke.</summary>
    string boundModel = "";

    /// <summary>The last picture, so an edit this view made can redraw without one being handed back.</summary>
    LayerStackPicture? shown;

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

        root = host.Add("layer-stack");

        root.SetStyle("display", "flex");
        root.SetStyle("flex-direction", "row");
        root.SetStyle("flex-grow", "1");

        this.tool = tool;

        var left = root.Add("layer-stack-rows");

        left.SetStyle("display", "flex");
        left.SetStyle("flex-direction", "column");
        left.SetStyle("flex-grow", "1");

        // ⚠ Above the rows and not beside the preview, because what it binds is what every row is
        // about. A layer paints on a mesh; the pane that has none can draw no islands, build no
        // coverage map and refuse no texel — #920 — so the binding is the first thing in the column
        // rather than a setting somewhere else.
        var binding = left.Add("layer-stack-binding");

        binding.SetStyle("display", "flex");
        binding.SetStyle("flex-direction", "row");

        binding.Add("layer-stack-binding-label").Text = "Mesh";

        model = binding.Add<Select>("layer-stack-model");
        meshStatus = binding.Add("layer-stack-binding-status");

        model.SelectionChanged += (_, value) => Bind(value ?? "");

        rows = left.Add("layer-stack-list");

        rows.SetStyle("display", "flex");
        rows.SetStyle("flex-direction", "column");
        rows.SetStyle("flex-grow", "1");

        left.Add("layer-stack-legend").Text = ChannelLegend;

        // ⚠ Under the rows and not under the preview, and the reason is what a diagnostic names. A
        // layer problem names a row that is directly above it and a node diagnostic names a node in
        // the graph those rows explode into; the 280px preview column is where the *picture* is
        // explained. `flex-grow` is deliberately left off so the list of rows keeps the space and
        // this grows only as far as it has messages.
        messages = left.Add("layer-stack-messages");

        messages.SetStyle("display", "none");
        messages.SetStyle("flex-direction", "column");

        var right = root.Add("layer-stack-preview");

        right.SetStyle("display", "flex");
        right.SetStyle("flex-direction", "column");
        right.SetStyle("width", "280px");

        title = right.Add("world-title");
        title.Text = "Result";

        Preview = right.Add<ImageView>();
        Preview.SetStyle("flex-grow", "1");

        status = right.Add("layer-stack-status");

        // ⚠ A third column and not a section of the preview one, and it is last so that the picture
        // keeps its width when the brush is not there. `PaintBrushInspector` builds its own root
        // into this element, which is why this file gains three lines rather than a panel.
        Brush = tool is null ? null : new PaintBrushInspector(root, tool);

        // A sibling of the layout rather than a child of it, because the empty state is shown by
        // hiding that layout — a message inside the thing being hidden is a message nobody sees.
        Empty = host.Add("layer-stack-empty");
        Empty.Text = "No layer stack is open. Select a .vxlayers in the Project panel and run Open Layer Stack.";
        Empty.SetStyle("display", "none");
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

    /// <summary>What is shown when no stack is open.</summary>
    public UiElement Empty { get; }

    /// <summary>The brush's column, or <see langword="null" /> when this host paints nothing.</summary>
    public PaintBrushInspector? Brush { get; }

    /// <summary>The stack currently shown.</summary>
    public LayerStackDocument? Document { get; private set; }

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
    /// </remarks>
    public IReadOnlyList<string> Messages { get; private set; } = [];

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
        Document = document;
        shown = picture;

        Watch(document);

        Empty.SetStyle("display", document is null ? "flex" : "none");
        root.SetStyle("display", document is null ? "none" : "flex");

        foreach (var child in messages.Children.ToArray()) {
            child.Remove();
        }

        Messages = picture is null ? [] : Describe(picture);

        foreach (var message in Messages) {
            messages.Add("layer-stack-message").Text = message;
        }

        // Hidden when it is empty rather than left as an empty box, because a heading with nothing
        // under it reads as "nothing was checked" and the ordinary case is a stack with nothing wrong.
        messages.SetStyle("display", Messages.Count == 0 ? "none" : "flex");

        if (document is null) {
            Clear();

            title.Text = "Result";
            status.Text = "";

            Preview.Image = 0;
            Preview.ImageWidth = 0;
            Preview.ImageHeight = 0;

            return;
        }

        var wanted = Shape(document);

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
            || !string.Equals(boundModel, document.Document.Model, StringComparison.Ordinal)) {
            Rebind(document);

            bound = document;
            boundModel = document.Document.Model;
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
        Preview.Fit();
    }

    /// <summary>The rows a stack's first texture set makes, topmost first.</summary>
    /// <param name="document">The stack.</param>
    /// <returns>One line per layer, a group's children indented under it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A disabled layer is listed and marked rather than hidden.</b> A row that vanished
    ///         when it was switched off would leave an artist with no way to switch it back on, which
    ///         is the same defect as a layer that never appears.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And a group's children are listed, which they were not before this panel could
    ///         reorder.</b> A list that stopped at the top level was honest while nothing could be
    ///         moved; it stops being honest the moment there is an <em>up</em> button, because a
    ///         layer inside a group is then a layer an artist cannot reach at all —
    ///         <c>LayerStackEdit</c> reorders inside whichever list a layer is really in, and this is
    ///         the half that lets somebody name one.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Describe(LayerStackDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        List<string> lines = [];

        if (document.Document.Sets.Count == 0) {
            return lines;
        }

        Walk(document.Document.Sets[0].Layers, 0);

        return lines;

        void Walk(List<LayerAsset> layers, int depth) {
            for (var index = layers.Count - 1; index >= 0; index--) {
                lines.Add(Line(layers[index], depth));
                Walk(layers[index].Children, depth + 1);
            }
        }
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
    static string Shape(LayerStackDocument document) {
        StringBuilder builder = new();

        if (document.Document.Sets.Count == 0) {
            return "";
        }

        var set = document.Document.Sets[0];

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
                    .Append((int)layer.Mask.Source)
                    .Append(':')
                    .Append(layer.Mask.Layers.Count)
                    .Append(':')
                    .Append(layer.Mask.Effects.Count)
                    .Append(';');

                Walk(layer.Children, depth + 1);
            }
        }
    }

    void Clear() {
        foreach (var child in rows.Children.ToArray()) {
            child.Remove();
        }

        bindings.Clear();

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

        if (document.Document.Sets.Count == 0) {
            if (tool is not null) {
                tool.LayerId = "";
            }

            return;
        }

        var set = document.Document.Sets[0];

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

        void Walk(List<LayerAsset> layers, int depth) {
            for (var index = layers.Count - 1; index >= 0; index--) {
                var layer = layers[index];

                if (ambiguous.Contains(layer.Id)) {
                    AmbiguousRow(layer, depth);
                } else {
                    LayerRow(document, set, layer, depth);
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

        row.SetStyle("display", "flex");
        row.SetStyle("flex-direction", "row");
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

    void LayerRow(LayerStackDocument document, TextureSetAsset set, LayerAsset layer, int depth) {
        LayerPath path = new(set.Name, layer.Id);

        var row = rows.Add("layer-stack-row");

        row.SetStyle("display", "flex");
        row.SetStyle("flex-direction", "row");
        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        // ⚠ First on the row and a button rather than a click on the row itself. Every other control
        // here marks its own pointer events handled, so a row-wide handler would have to be on the
        // capture leg and would then swallow the press that was aimed at a tick box — the trap
        // `PaintUvView`'s own handler documents, in the direction that breaks the rest of the panel.
        var select = row.Add<Button>("layer-stack-select");

        select.Clicked += _ => Choose(path);

        var up = row.Add<Button>("layer-stack-move-up");
        var down = row.Add<Button>("layer-stack-move-down");

        up.Label = "Move up";
        down.Label = "Move down";

        // ⚠ Up is +1 in the file's order. `TextureSetAsset.Layers` is bottom first and this panel
        // draws it topmost first, so the button an artist reads as "over the one above it" is the one
        // that moves the layer *later* in the composite. Getting this backwards is invisible on a
        // one-layer stack and silent on two identical ones, which is why the test that covers it
        // compares compiled plans.
        up.Clicked += _ => Move(document, path, +1, "Move Layer Up");
        down.Clicked += _ => Move(document, path, -1, "Move Layer Down");

        var enabled = row.Add<CheckBox>("layer-stack-enabled");

        enabled.Label = "Enabled";
        enabled.CheckedChanged += (_, value) => Set(
            document,
            path,
            current => current with { Enabled = value },
            value ? "Show Layer" : "Hide Layer"
        );

        var name = row.Add("layer-stack-row-name");

        var blend = row.Add<Select>("layer-stack-blend");

        foreach (var mode in Enum.GetValues<LayerBlendMode>()) {
            blend.AddOption(mode.ToString());
        }

        blend.SelectionChanged += (_, value) => {
            if (Enum.TryParse<LayerBlendMode>(value, out var mode)) {
                Set(document, path, current => current with { Blend = mode }, "Set Blend Mode");
            }
        };

        var opacity = row.Add<Slider>("layer-stack-opacity");

        opacity.Minimum = 0f;
        opacity.Maximum = 1f;

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

        var channels = row.Add("layer-stack-channels");

        channels.SetStyle("display", "flex");
        channels.SetStyle("flex-direction", "row");

        List<CheckBox> ticks = [];

        foreach (var channel in set.Channels) {
            var usage = channel.Usage;
            var tick = channels.Add<CheckBox>("layer-stack-channel");

            tick.Label = usage;

            tick.CheckedChanged += (_, value) => {
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

            ticks.Add(tick);
        }

        bindings.Add(() => {
            if (LayerStackEdit.Find(document.Document, path) is not { } current) {
                return;
            }

            var chosen = Selected == path;

            // The marker is in the row's own text rather than a style, so what the panel says about
            // which layer the brush is aimed at is something a test can read.
            name.Text = (chosen ? "● " : "") + Line(current, depth);
            select.Label = chosen ? "Selected" : "Select";
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
                // the file is kept out of the panel. Clearing it would leave `Channels` empty — and
                // empty means *all*, so the gesture an artist reads as "and now it writes nothing"
                // would make the layer write everything. A layer that should write nothing is one
                // that is switched off, which is the tick box two elements to the left.
                ticks[index].Disabled = writes && written == 1;
            }
        });
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

            MaskRow(
                document,
                path,
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
        }

        for (var index = mask.Layers.Count - 1; index >= 0; index--) {
            var position = index;

            MaskRow(
                document,
                path,
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
        }

        if (mask.Source == LayerMaskSource.None) {
            return;
        }

        // ⚠ The base has no `Enabled` of its own and therefore no tick, which is a fact about
        // `MaskAsset` rather than an omission here: its source, its value and its asset are the
        // mask's own members, kept flat because every `.vxlayers` that exists names a mask that way.
        // Switching a base off is done by setting its source to None, which wants an editor for the
        // source — #882.
        var row = rows.Add("layer-stack-mask-row");

        row.SetStyle("display", "flex");
        row.SetStyle("flex-direction", "row");
        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        var name = row.Add("layer-stack-mask-name");

        bindings.Add(() => name.Text = Describe(document, path));
    }

    void MaskRow(
        LayerStackDocument document,
        LayerPath path,
        int depth,
        Func<string> describe,
        Func<bool> enabled,
        Action<bool> toggle
    ) {
        var row = rows.Add("layer-stack-mask-row");

        row.SetStyle("display", "flex");
        row.SetStyle("flex-direction", "row");
        row.SetStyle("padding-left", (depth * 12).ToString(CultureInfo.InvariantCulture) + "px");

        var tick = row.Add<CheckBox>("layer-stack-mask-enabled");

        tick.Label = "Enabled";
        tick.CheckedChanged += (_, value) => toggle(value);

        var name = row.Add("layer-stack-mask-name");

        bindings.Add(() => {
            name.Text = describe();
            tick.IsChecked = enabled();
        });
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

    /// <summary>Puts the project's models in the picker and the stack's own binding on it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The options are re-read per stack rather than once, because the project's models
    ///         are not fixed.</b> Importing a model is an ordinary thing to do while a stack is open,
    ///         and a picker filled when the panel was built would not have the mesh the artist just
    ///         added — which reads as the import having failed.
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
