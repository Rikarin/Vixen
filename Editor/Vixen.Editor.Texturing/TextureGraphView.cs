// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.Plugin;
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
///         ⚠ <b>The preview pane draws what the device produced, and the line under it says what
///         that is.</b> Two things used to stand between this panel and a picture; one of them —
///         no device published to plugins — is closed, so the pane now shows a real dispatch at the
///         document's own resolution (<see cref="TextureGraphPreview" />). The other is not, so the
///         line says the picture is the graph's <i>base layer</i> rather than the wired graph. A
///         pane that claimed otherwise would hide the remaining gap in the one place a person would
///         notice it; a pane left empty would hide whether the first half works at all. On a host
///         with no device, the pane stays empty and the same line says which of the two reasons it
///         is — see <see cref="TexturePreviewBlocker" />.
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

    /// <summary>Puts a graph on the canvas, or takes the last one off.</summary>
    /// <param name="document">The graph, or <see langword="null" /> for none.</param>
    /// <param name="blocker">What stands between this host and a picture, if anything.</param>
    /// <param name="result">The evaluated picture, or <see langword="null" /> for none.</param>
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
    /// </remarks>
    public void Show(
        TextureGraphDocument? document,
        TexturePreviewBlocker blocker,
        IEditorImage? result = null
    ) {
        Document = document;

        Empty.SetStyle("display", document is null ? "flex" : "none");
        root.SetStyle("display", document is null ? "none" : "flex");
        status.Text = TexturePreview.Describe(blocker);

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

        // ⚠ The extent comes from the *document* even when there is a picture, and the two agree only
        // because the plan is built at the document's resolution. `ImageView.Image` is a number the
        // renderer resolves; zero draws the chequerboard and nothing else, which is the honest picture
        // of a graph this host cannot evaluate. The extent is what makes the zoom, the fit and the
        // pointer readout mean the texels an author is authoring, so it is set either way.
        Preview.Image = result?.Image ?? 0;
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
