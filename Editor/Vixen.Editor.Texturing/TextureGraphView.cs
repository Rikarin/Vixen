// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Plugin;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Texturing;

/// <summary>A texture graph, open for editing: the canvas, and the picture it would make.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § D1's panel, and the first production caller <see cref="ImageView" /> has
///         had.</b> The canvas is <c>NodeGraphView</c>'s — the same one the shader graph, the VFX
///         graph and the compositor use, undoable in every gesture because it writes through the
///         document's own <c>CommandStack</c> — and the pane beside it is the image view batch 1
///         built for exactly this, which until now nothing in the editor constructed.
///     </para>
///     <para>
///         ⚠ <b>The preview pane draws what the device produced, and the line under it says what
///         that is.</b> Two things used to stand between this panel and a picture and both are
///         closed: a device published to plugins
///         (<a href="https://github.com/Rikarin/Vixen/issues/737">#737</a>), and a caller for the
///         public compiler (<a href="https://github.com/Rikarin/Vixen/issues/792">#792</a>). So the
///         pane shows <em>this graph</em>, compiled and evaluated at the document's own resolution,
///         and for three batches it showed a fixed checkerboard while the line under it named a
///         closed issue as the reason. On a host with no device the pane stays empty and the line
///         says which of the two host states it is in — see <see cref="TexturePreviewBlocker" />;
///         on a graph that does not compile it says which node refused, which is a fact about the
///         document and not about the host.
///     </para>
///     <para>
///         ⚠ <b>Built in C# rather than in <c>.vxml</c>, and that is a debt rather than a
///         preference.</b> Doc 36 § P4 makes markup the authoring path and <c>TerrainBrushInspector</c>
///         is the worked example; this panel is three elements and a status line, and porting it is
///         worth doing when it grows a form. The layout is set inline for the reason a component's
///         host element collapses otherwise: nothing here is styled by a sheet this assembly owns.
///     </para>
/// </remarks>
sealed class TextureGraphView {
    readonly List<(string Label, NodeGraphModel Graph)> opened = [];
    readonly UiElement root;
    readonly UiElement status;
    readonly UiElement title;
    readonly UiElement trail;

    /// <summary>Builds the view into a host element.</summary>
    /// <param name="host">Where it goes. A <c>DockPanel</c>, or anything inside one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host" /> is null.</exception>
    public TextureGraphView(UiElement host) {
        ArgumentNullException.ThrowIfNull(host);

        // ⚠ A node canvas pans and zooms in a space of its own and converts a pointer through its own
        // absolute box, so a panel that also scrolled would put a second, invisible transform between
        // the cursor and the graph. `ShaderGraphEditorFactory.CreateView` says the same thing.
        DockPanel.Fills(host);

        root = host.Add("texture-graph");

        // ⚠ Both, and explicitly. `flex-direction` is `row` by CSS default and `flex-grow` is not, so
        // a container that set neither would be the full width and no height — which is the shape of
        // "the panel is blank" that costs an afternoon every time.
        root.SetStyle("display", "flex");
        root.SetStyle("flex-direction", "row");
        root.SetStyle("flex-grow", "1");

        var left = root.Add("texture-graph-canvas");

        left.SetStyle("display", "flex");
        left.SetStyle("flex-direction", "column");
        left.SetStyle("flex-grow", "1");

        // ⚠ Above the canvas rather than beside it, and it is the whole of the way back out of a
        // published graph. A trail that lived in the preview column would be a way back an author
        // has to look for, on the opposite side of the panel from the thing that took them in.
        trail = left.Add("texture-graph-trail");

        trail.SetStyle("display", "none");
        trail.SetStyle("flex-direction", "row");

        Canvas = left.Add<NodeGraphView>();

        // ⚠ Inline, and it is belt and braces rather than the only thing holding the graph open.
        // `node-graph { flex-grow: 1 }` is in `NodeGraphTheme.vcss`, which the editor host installs
        // as of #917 — but this line predates that and stays, because a plugin's panel should not
        // depend on a host stylesheet it does not own. The other three graph
        // panels get it from `AssetEditorTheme.vcss`'s `<x>-editor > node-graph` rules, and no rule
        // anywhere names this one: the canvas measured 990×0 in a shell with the panel open, which
        // is a graph an author can neither see nor click, let alone double-click a compound in.
        Canvas.SetStyle("flex-grow", "1");

        // ⚠ The event had no subscriber anywhere in the tree —
        // <a href="https://github.com/Rikarin/Vixen/issues/859">#859</a>. A double-click on a
        // compound reached the framework, raised this, and stopped: an author could place
        // `Generators/Dirt`, compile it, bake it, and never see what it was.
        Canvas.SubGraphOpened += (_, type, child) => Descend(type, child);

        // ⚠ The line that makes an edit on the canvas reach the picture, and it is worth nothing
        // until the picture is the graph. While the pane drew a fixed checkerboard there was no
        // difference between redrawing on every edit and redrawing never; now that it compiles the
        // document (#792), a wire an author drags changes the map and this is what says so — which is
        // `LayerStackView.Edited` one panel over, for #819's reason.
        Canvas.GraphChanged += _ => Edited?.Invoke();

        var right = root.Add("texture-graph-preview");

        right.SetStyle("display", "flex");
        right.SetStyle("flex-direction", "column");
        right.SetStyle("width", "280px");

        title = right.Add("world-title");
        title.Text = "Result";

        Preview = right.Add<ImageView>();
        Preview.SetStyle("flex-grow", "1");

        status = right.Add("texture-graph-status");

        // ⚠ A sibling of the layout rather than a child of it, because the empty state is shown by
        // hiding that layout — a message inside the thing being hidden is a message nobody ever sees.
        Empty = host.Add("texture-graph-empty");
        Empty.Text = "No texture graph is open. Select a .vxtexgraph in the Project panel and run Open Texture Graph.";
        Empty.SetStyle("display", "none");
    }

    /// <summary>Everything this view built, for a caller that has to hand a root back.</summary>
    public UiElement Root => root;

    /// <summary>The canvas the graph is edited on.</summary>
    public NodeGraphView Canvas { get; }

    /// <summary>The pane the baked result would be shown in.</summary>
    public ImageView Preview { get; }

    /// <summary>What is shown when no graph is open.</summary>
    public UiElement Empty { get; }

    /// <summary>The document currently on the canvas.</summary>
    public TextureGraphDocument? Document { get; private set; }

    /// <summary>Called when the graph on the canvas changed, for whoever owns the evaluator.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The view cannot redraw its own picture and must not try.</b> Evaluating needs the
    ///         module's <c>TexturePlanEvaluator</c> — there is one of those per session, not per view
    ///         (<a href="https://github.com/Rikarin/Vixen/issues/820">#820</a>) — so what this view
    ///         can do about an edit is say that there was one. A tab built by the asset-editor
    ///         factory leaves this null and simply does not redraw, which is what
    ///         <see cref="TexturePreviewBlocker.AnotherPane" /> tells the reader.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It fires on every change to the model, including a node being dragged to a new
    ///         position.</b> That is a compile and a dispatch for a move that cannot change a texel,
    ///         and it is the layers panel's cost profile unchanged — the alternative is a pane that
    ///         is right about some edits and stale about others, which is worse than slow.
    ///     </para>
    /// </remarks>
    public Action? Edited { get; set; }

    /// <summary>Puts a graph on the canvas for a caller that has drawn nothing, and says why.</summary>
    /// <param name="document">The graph, or <see langword="null" /> for none.</param>
    /// <param name="blocker">What stands between this host — or this view — and a picture.</param>
    /// <remarks>
    ///     What an <c>IAssetEditorFactory</c>'s tab uses: it holds no evaluator, so the only thing it
    ///     has to say is which pane does. See the other overload for why that is a separate question
    ///     from what a compile said.
    /// </remarks>
    public void Show(TextureGraphDocument? document, TexturePreviewBlocker blocker) =>
        Show(document, new TextureGraphPicture(null, TexturePreview.Describe(blocker)));

    /// <summary>Set by a caller that compiled before showing, to say the compile republished.</summary>
    /// <remarks>
    ///     ⚠ <b>Cleared by the next <see cref="Show" />, so it cannot answer twice.</b>
    ///     <c>TextureGraphDocument.Republish</c> is a one-shot: it returns true once per change and
    ///     false afterwards. Anything that compiles — which is what producing a picture means —
    ///     consumes it, so a caller that produces the picture first has to carry the answer across
    ///     rather than let this view ask a question already answered.
    /// </remarks>
    public bool Republished { get; set; }

    /// <summary>Puts a graph on the canvas, with whatever evaluating it produced.</summary>
    /// <param name="document">The graph, or <see langword="null" /> for none.</param>
    /// <param name="picture">What the evaluation produced, or <see langword="null" /> for nothing.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null is an ordinary state and not a failure.</b> A panel's factory runs when the
    ///         panel is opened, which for a restored layout is before anybody has opened a graph — so
    ///         a view that demanded one would be a panel the editor could not show at start-up.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The status line is written on every show, not once in the constructor.</b> It
    ///         used to be set when the view was built, from an answer the module had read at
    ///         activation — and the editor acquires its device <i>after</i> it builds its plugin
    ///         host, so a pane built that way said "no device" for the whole session on a host that
    ///         had one by the time anybody looked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A sentence rather than a blocker, because a graph has a third kind of answer.</b>
    ///         A blocker says what the <em>host</em> cannot do; a graph that does not compile, or one
    ///         whose bitmap names a missing file, is a fact about the document and needs the
    ///         diagnostic in it. That is the difference <c>LayerStackView</c> already had, and the
    ///         overload above is the host-side half kept for a caller that never evaluated —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/816">#816</a>.
    ///     </para>
    /// </remarks>
    public void Show(TextureGraphDocument? document, TextureGraphPicture? picture) {
        // ⚠ Whether this is a *different* graph, not whether there is one. `Show` runs on every
        // refresh — every edit, every evaluation — and a trail rebuilt each time would throw an
        // author out of the compound they were looking inside the moment the preview redrew.
        var arrived = !ReferenceEquals(Document, document);

        Document = document;

        if (arrived) {
            opened.Clear();

            if (document is not null) {
                opened.Add((Name(document), document.Graph));
            }

            Retrail();
        }

        Empty.SetStyle("display", document is null ? "flex" : "none");
        root.SetStyle("display", document is null ? "none" : "flex");
        status.Text = picture?.Status ?? "";

        if (document is null) {
            title.Text = "Result";

            Preview.Image = 0;
            Preview.ImageWidth = 0;
            Preview.ImageHeight = 0;

            return;
        }

        title.Text = "Result — " + Resolution(document);

        // ⚠ Before the registry is read, because a republish replaces it rather than adding to it —
        // #803. A panel that read the old one would offer an author the menu a compound had before
        // they edited it, and go on doing so until the graph was reopened.
        //
        // ⚠ **And `Republished` is what a caller who compiled first must tell us**, because
        // `TextureGraphDocument.Compile` republishes too. A caller that writes
        // `Show(document, preview.Evaluate(document))` has already consumed the stale flag by the
        // time this line runs — C# evaluates the argument first — so asking here answered false on
        // the one path that matters and `Resettle` was unreachable in the real editor.
        var republished = Republished || document.Republish();

        Republished = false;

        // ⚠ The compounds that would not read, and this is the only place the loss is visible —
        // #803. `TextureCompoundLibrary.Publish` reports and skips rather than throwing, so that one
        // bad file in `Assets/Compounds` does not cost an author every other node in the menu; the
        // cost of that decision is a node type silently missing from the search popup, and until
        // this line nothing anywhere read `TextureGraphDocument.CompoundProblems`.
        if (document.CompoundProblems.Length > 0) {
            status.Text = string.Join(
                " · ",
                document.CompoundProblems
                    .Select(problem => $"'{problem.Path}' is not in the menu: {problem.Problem}")
                    .Prepend(status.Text)
            );
        }

        // ⚠ The warnings, and only the warnings, and this is the first production reader a texture
        // diagnostic has had on the graph side — #816. An error is already in the sentence above:
        // `TextureGraphPreview.Refused` builds it out of exactly those, so listing them again would
        // say every failure twice. A warning is the one that did not stop the map and therefore has
        // nowhere else to appear — #830's finding, one panel over.
        if (picture is not null) {
            var cautions = picture.Diagnostics
                .Where(one => one.Severity != NodeSeverity.Error)
                .Select(one => one.Id + ": " + one.Message)
                .ToArray();

            if (cautions.Length > 0) {
                status.Text = string.Join(" · ", cautions.Prepend(status.Text));
            }
        }

        Canvas.Registry = document.Registry;

        // ⚠ What the *canvas* does with a published node type, which is the half of #803 the
        // document's own wire left dark: `NodeGraphView` uses this only to tell a sub-graph node
        // from an ordinary one, so that double-clicking one raises `SubGraphOpened` with the graph
        // it stands for. Without it a compound is a node that looks atomic and cannot be looked
        // inside, on a canvas whose registry offers it.
        Canvas.SubGraphSource = document.SubGraphs;

        // ⚠ A republish builds a whole new library, so every graph the trail is holding belongs to
        // the old one. An author who was looking inside a compound while it was saved elsewhere
        // would go on inspecting a model nothing in the editor refers to any more — a picture of
        // the version they were trying to change.
        if (republished && opened.Count > 1) {
            Resettle(document);
        }

        // ⚠ And only when the author is looking at their own graph. Inside a published one the
        // canvas is showing the library's model, and re-seating it here would undo the descent on
        // the next refresh — which for a panel that refreshes on every edit is immediately.
        if (opened.Count <= 1) {
            Canvas.Graph = document.Graph;

            // The document's own stack, which is what makes every gesture on the canvas one undo
            // entry in the same history as everything else done to this asset.
            Canvas.Stack = document.Stack;
            Canvas.EditedDocument = document;
        }

        // ⚠ The extent comes from the *document* even when there is a picture, and the two agree only
        // because the plan is built at the document's resolution. `ImageView.Image` is a number the
        // renderer resolves; zero draws the chequerboard and nothing else, which is the honest picture
        // of a graph this host cannot evaluate. The extent is what makes the zoom, the fit and the
        // pointer readout mean the texels an author is authoring, so it is set either way.
        Preview.Image = picture?.Image?.Image ?? 0;
        Preview.ImageWidth = document.BaseWidth;
        Preview.ImageHeight = document.BaseHeight;
        Preview.Fit();
    }

    /// <summary>What the status line under the preview says.</summary>
    /// <remarks>For a test, and for a panel that grows a second thing to say.</remarks>
    public string Status => status.Text ?? string.Empty;

    /// <summary>What the canvas is inside, outermost first: the document, then each graph opened.</summary>
    /// <remarks>
    ///     One entry is the ordinary state — the author's own graph — and the trail strip is hidden
    ///     for it. Anything more and the canvas is showing a published graph rather than the document.
    /// </remarks>
    public IReadOnlyList<string> Trail => [.. opened.Select(step => step.Label)];

    /// <summary>What the trail says about the graph it took the author into.</summary>
    public const string ReadOnly =
        "A published graph is shown as it was published. Open its own asset to change it.";

    /// <summary>Puts a published graph on the canvas, as the graph the node stands for.</summary>
    /// <param name="type">Its node-type path, which is what the trail calls it.</param>
    /// <param name="graph">The graph.</param>
    /// <remarks>
    ///     ⚠ <b>The model is the library's own and every graph containing that node type shares
    ///     it</b>, which is why the canvas is put in its read-only state rather than merely being
    ///     asked nicely: an edit here would rewrite a compound for every material in the project,
    ///     with no undo entry and nothing to save it to. <c>NodeGraphView.IsReadOnly</c> is
    ///     "there is no stack", so taking the stack away is the whole mechanism.
    /// </remarks>
    void Descend(string type, NodeGraphModel graph) {
        opened.Add((type, graph));
        Enter(opened.Count - 1);
    }

    /// <summary>Points the trail at a rebuilt library, and truncates it where it no longer reaches.</summary>
    /// <remarks>
    ///     ⚠ <b>Re-resolved by node-type path rather than kept, because the path is the only part of
    ///     a trail step that survives a republish.</b> A crumb's label <em>is</em> that path, which
    ///     is what makes this possible at all; a compound that has been renamed or deleted resolves
    ///     to nothing, and the trail is cut back to the last step that is still true rather than
    ///     left pointing at a graph no library has.
    /// </remarks>
    void Resettle(TextureGraphDocument document) {
        var kept = 1;

        for (var step = 1; step < opened.Count; step++) {
            if (document.SubGraphs?.TryGet(opened[step].Label, out var graph) != true) {
                break;
            }

            opened[step] = (opened[step].Label, graph!);
            kept = step + 1;
        }

        opened.RemoveRange(kept, opened.Count - kept);
        Enter(opened.Count - 1);
    }

    /// <summary>Goes to one step of the trail, dropping everything past it.</summary>
    void Enter(int step) {
        opened.RemoveRange(step + 1, opened.Count - step - 1);

        // ⚠ The document before the graph, in BOTH directions, and the rule is one rule rather than
        // two. Setting `Graph` rebuilds the canvas's port editors, and each asks the canvas whether
        // it has a document to record an edit against — so whichever value is stale at that instant
        // is the one they are built for. Going in, a stale document would put the library's model on
        // a canvas that records edits to it; coming back out, a stale null left the author's own
        // graph read-only until something else rebuilt it. The return leg had it backwards.
        if (step == 0 && Document is { } document) {
            Canvas.EditedDocument = document;
            Canvas.Graph = document.Graph;
        } else {
            Canvas.EditedDocument = null;
            Canvas.Graph = opened[step].Graph;
        }

        Retrail();
    }

    /// <summary>Rewrites the trail strip, which is hidden while there is nothing to go back to.</summary>
    void Retrail() {
        while (trail.Children.Count > 0) {
            trail.Children[^1].Remove();
        }

        trail.SetStyle("display", opened.Count > 1 ? "flex" : "none");

        if (opened.Count <= 1) {
            return;
        }

        for (var step = 0; step < opened.Count; step++) {
            if (step > 0) {
                trail.Add("texture-graph-trail-separator").Text = "›";
            }

            var crumb = trail.Add<Button>();
            var index = step;

            crumb.Label = opened[step].Label;
            crumb.Clicked += _ => Enter(index);
        }

        trail.Add("texture-graph-trail-note").Text = ReadOnly;
    }

    /// <summary>What the trail calls the document itself.</summary>
    static string Name(TextureGraphDocument document) =>
        document.Graph.Name.Length > 0 ? document.Graph.Name : document.Title.Value;

    /// <summary>The resolution readout, as the pane titles it.</summary>
    /// <param name="document">The graph.</param>
    /// <returns>The text.</returns>
    public static string Resolution(TextureGraphDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{document.BaseWidth} × {document.BaseHeight}"
        );
    }
}
