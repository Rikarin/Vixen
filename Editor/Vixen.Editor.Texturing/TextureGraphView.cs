// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.NodeGraph;
using Vixen.Ui;
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
///         ⚠ <b>The preview pane is empty and says why, rather than being absent.</b> Two things
///         stand between this panel and a picture and neither is this panel's to fix — see
///         <see cref="TexturePreviewBlocker" />. A pane that were simply left out would make the two
///         gaps invisible in the one place a person would notice them; a pane that faked a thumbnail
///         would make them invisible for ever. What it shows is what an empty layer at this graph's
///         base resolution looks like — the chequerboard, at the extent a bake would write — which is
///         also the cheapest proof that the control is wired to the document rather than to a
///         constant.
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
    readonly UiElement root;
    readonly UiElement status;
    readonly UiElement title;

    /// <summary>Builds the view into a host element.</summary>
    /// <param name="host">Where it goes. A <c>DockPanel</c>, or anything inside one.</param>
    /// <param name="blocker">What stands between this host and a picture.</param>
    /// <exception cref="ArgumentNullException"><paramref name="host" /> is null.</exception>
    public TextureGraphView(UiElement host, TexturePreviewBlocker blocker) {
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

        Canvas = left.Add<NodeGraphView>();

        var right = root.Add("texture-graph-preview");

        right.SetStyle("display", "flex");
        right.SetStyle("flex-direction", "column");
        right.SetStyle("width", "280px");

        title = right.Add("world-title");
        title.Text = "Result";

        Preview = right.Add<ImageView>();
        Preview.SetStyle("flex-grow", "1");

        status = right.Add("texture-graph-status");
        status.Text = TexturePreview.Describe(blocker);

        // ⚠ A sibling of the layout rather than a child of it, because the empty state is shown by
        // hiding that layout — a message inside the thing being hidden is a message nobody ever sees.
        Empty = host.Add("texture-graph-empty");
        Empty.Text = "No texture graph is open. Select a .vxtexgraph in the Project panel and run Open Texture Graph.";
        Empty.SetStyle("display", "none");
    }

    /// <summary>The canvas the graph is edited on.</summary>
    public NodeGraphView Canvas { get; }

    /// <summary>The pane the baked result would be shown in.</summary>
    public ImageView Preview { get; }

    /// <summary>What is shown when no graph is open.</summary>
    public UiElement Empty { get; }

    /// <summary>The document currently on the canvas.</summary>
    public TextureGraphDocument? Document { get; private set; }

    /// <summary>Puts a graph on the canvas, or takes the last one off.</summary>
    /// <param name="document">The graph, or <see langword="null" /> for none.</param>
    /// <remarks>
    ///     ⚠ <b>Null is an ordinary state and not a failure.</b> A panel's factory runs when the panel
    ///     is opened, which for a restored layout is before anybody has opened a graph — so a view
    ///     that demanded one would be a panel the editor could not show at start-up.
    /// </remarks>
    public void Show(TextureGraphDocument? document) {
        Document = document;

        Empty.SetStyle("display", document is null ? "flex" : "none");
        root.SetStyle("display", document is null ? "none" : "flex");

        if (document is null) {
            title.Text = "Result";

            Preview.Image = 0;
            Preview.ImageWidth = 0;
            Preview.ImageHeight = 0;

            return;
        }

        title.Text = "Result — " + Resolution(document);

        Canvas.Graph = document.Graph;
        Canvas.Registry = document.Registry;

        // The document's own stack, which is what makes every gesture on the canvas one undo entry in
        // the same history as everything else done to this asset.
        Canvas.Stack = document.Stack;
        Canvas.EditedDocument = document;

        // ⚠ The extent a bake would write, and no handle. `ImageView.Image` is a texture the renderer
        // knows; zero draws the chequerboard and nothing else, which is the honest picture of a graph
        // this host cannot evaluate. Setting the extent anyway is what makes the zoom, the fit and the
        // pointer readout mean the texels an author is authoring.
        Preview.Image = 0;
        Preview.ImageWidth = document.BaseWidth;
        Preview.ImageHeight = document.BaseHeight;
        Preview.Fit();
    }

    /// <summary>What the status line under the preview says.</summary>
    /// <remarks>For a test, and for a panel that grows a second thing to say.</remarks>
    public string Status => status.Text ?? string.Empty;

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
