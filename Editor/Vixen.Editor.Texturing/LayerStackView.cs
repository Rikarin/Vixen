// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Texturing.Layers;
using Vixen.Ui;
using Vixen.Ui.Controls.Advanced;

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
///         ⚠ <b>Read-only, and saying so is the point.</b> Every row here is drawn from the document
///         and nothing writes back: this shows that a stack opens, compiles and bakes, which is the
///         claim #806 makes and the only claim a panel can settle. Editing — reorder, blend mode,
///         the per-channel enables — is doc 48 § D10's own panel and wants an undoable model, which
///         is a different piece of work and is filed as
///         <a href="https://github.com/Rikarin/Vixen/issues/819">#819</a>. A panel that let an
///         artist drag a row and quietly dropped it on save would be worse than one that does not
///         offer the drag.
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
///         or in the compiler — which is what that member's own remarks ask for.
///     </para>
///     <para>
///         ⚠ <b>Built in C# rather than <c>.vxml</c>, and that is a debt.</b>
///         <c>TextureGraphView</c>'s reason unchanged: doc 36 § P4 makes markup the authoring path,
///         and porting is worth doing when the panel grows a form to edit with. Both directions of
///         the flex are set explicitly, because <c>flex-direction</c> is <c>row</c> by CSS default
///         and <c>flex-grow</c> is not — a container that set neither is full width and no height,
///         which is the shape of "the panel is blank".
///     </para>
/// </remarks>
sealed class LayerStackView {
    readonly UiElement messages;
    readonly UiElement root;
    readonly UiElement rows;
    readonly UiElement status;
    readonly UiElement title;

    /// <summary>Builds the view into a host element.</summary>
    /// <param name="host">Where it goes. A <c>DockPanel</c>, or anything inside one.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host" /> is null.</exception>
    public LayerStackView(UiElement host) {
        ArgumentNullException.ThrowIfNull(host);

        DockPanel.Fills(host);

        root = host.Add("layer-stack");

        root.SetStyle("display", "flex");
        root.SetStyle("flex-direction", "row");
        root.SetStyle("flex-grow", "1");

        var left = root.Add("layer-stack-rows");

        left.SetStyle("display", "flex");
        left.SetStyle("flex-direction", "column");
        left.SetStyle("flex-grow", "1");

        rows = left.Add("layer-stack-list");

        rows.SetStyle("display", "flex");
        rows.SetStyle("flex-direction", "column");
        rows.SetStyle("flex-grow", "1");

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

        // A sibling of the layout rather than a child of it, because the empty state is shown by
        // hiding that layout — a message inside the thing being hidden is a message nobody sees.
        Empty = host.Add("layer-stack-empty");
        Empty.Text = "No layer stack is open. Select a .vxlayers in the Project panel and run Open Layer Stack.";
        Empty.SetStyle("display", "none");
    }

    /// <summary>Everything this view built, for a caller that has to hand a root back.</summary>
    public UiElement Root => root;

    /// <summary>The pane the baked map is shown in.</summary>
    public ImageView Preview { get; }

    /// <summary>What is shown when no stack is open.</summary>
    public UiElement Empty { get; }

    /// <summary>The stack currently shown.</summary>
    public LayerStackDocument? Document { get; private set; }

    /// <summary>What the status line under the preview says.</summary>
    public string Status => status.Text ?? string.Empty;

    /// <summary>The rows, topmost first — what a test reads instead of walking the tree.</summary>
    public IReadOnlyList<string> Rows { get; private set; } = [];

    /// <summary>Everything the compile had to say, in the order it said it.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the surface <a href="https://github.com/Rikarin/Vixen/issues/830">#830</a>
    ///     found missing, and the argument it was missing from is <c>TG0022</c>'s.</b> The terminus
    ///     rescale was chosen over a silent one because "it is said" — and nothing said it: no
    ///     production type rendered a <c>NodeDiagnostic</c>, and the one consumer that read a list of
    ///     them kept the errors. So a warning reached an author only in the sense that a value
    ///     existed in a record. A diagnostic an author cannot see is not a diagnostic.
    /// </remarks>
    public IReadOnlyList<string> Messages { get; private set; } = [];

    /// <summary>Puts a stack in the panel, or takes the last one out.</summary>
    /// <param name="document">The stack, or <see langword="null" /> for none.</param>
    /// <param name="picture">What evaluating it produced, or <see langword="null" /> for nothing.</param>
    /// <remarks>
    ///     ⚠ <b>Null is an ordinary state.</b> A panel's factory runs when the panel is opened, which
    ///     for a restored layout is before anybody has opened a stack — so a view that demanded one
    ///     would be a panel the editor could not show at start-up.
    /// </remarks>
    public void Show(LayerStackDocument? document, LayerStackPicture? picture = null) {
        Document = document;

        Empty.SetStyle("display", document is null ? "flex" : "none");
        root.SetStyle("display", document is null ? "none" : "flex");

        // ⚠ Backwards, and over a copy, because `Children` is the live list: removing forwards
        // renumbers what is left under the loop and drops every second row — which looks like a
        // half-loaded stack rather than a bug in a panel.
        foreach (var child in rows.Children.ToArray()) {
            child.Remove();
        }

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
            Rows = [];
            title.Text = "Result";
            status.Text = "";

            Preview.Image = 0;
            Preview.ImageWidth = 0;
            Preview.ImageHeight = 0;

            return;
        }

        Rows = Describe(document);

        foreach (var row in Rows) {
            rows.Add("layer-stack-row").Text = row;
        }

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
    /// <returns>One line per layer.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A disabled layer is listed and marked rather than hidden.</b> A row that vanished
    ///     when it was switched off would leave an artist with no way to switch it back on, which is
    ///     the same defect as a layer that never appears.
    /// </remarks>
    public static IReadOnlyList<string> Describe(LayerStackDocument document) {
        ArgumentNullException.ThrowIfNull(document);

        if (document.Document.Sets.Count == 0) {
            return [];
        }

        var layers = document.Document.Sets[0].Layers;
        var lines = new List<string>(layers.Count);

        for (var index = layers.Count - 1; index >= 0; index--) {
            var layer = layers[index];
            var name = layer.Name.Length > 0 ? layer.Name : layer.Id;

            lines.Add(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{name} — {layer.Kind}, {layer.Blend}, {layer.Opacity:0.##}{(layer.Enabled ? "" : ", off")}"
                )
            );
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
    ///         nodes, so it turns one mistyped setting into seven lines. What a reader can act on is
    ///         the <em>layer</em>, and nothing on a diagnostic carries one; see
    ///         <a href="https://github.com/Rikarin/Vixen/issues/880">#880</a> for the map that would.
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
            var line = $"{Severity(diagnostic.Severity)} — {diagnostic.Id}: {diagnostic.Message}";

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
}
