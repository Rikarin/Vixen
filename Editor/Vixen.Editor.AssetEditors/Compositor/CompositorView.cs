// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Compositor;

/// <summary>A graphics compositor, open for editing: the graph, the selected node, and the frame.</summary>
/// <remarks>
///     <para>
///         Doc 06's "the frame is data the user edits", with a canvas over it. The graph itself is
///         <see cref="NodeGraphView" />'s — every gesture there is already an undoable command, and
///         nothing about a compositor changes that — so what is here is the two things a compositor
///         needs beside it: the settings of whatever is selected, and what compiling says.
///     </para>
///     <para>
///         ⚠ <b>The settings panel is not the inspector, and the reason is where the values live.</b>
///         An <c>InspectorView</c> edits members of an object; a node's settings live on the
///         <i>graph</i>, in <c>Values</c> and <c>Texts</c> keyed by port name, because that is what
///         survives a save and an undo. So the rows are built from
///         <see cref="CompositorNode.Fields" /> and write through <c>SetPortValueCommand</c> and
///         <c>SetPortTextCommand</c> — which is what keeps typing a target name one undo entry
///         rather than one a keystroke.
///     </para>
/// </remarks>
public sealed class CompositorView : Control {
    readonly Dictionary<UiElement, CompositorField> editors = [];

    CompositorDocument? document;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "compositor-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The canvas.</summary>
    public NodeGraphView GraphView { get; private set; } = null!;

    /// <summary>The column beside it.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The selected node's settings.</summary>
    public UiElement Settings { get; private set; } = null!;

    /// <summary>The button that compiles the graph.</summary>
    public Button Build { get; private set; } = null!;

    /// <summary>What compiling said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        GraphView = Part<NodeGraphView>();

        Side = Part("compositor-side");

        Build = Side.Add<Button>();
        Build.Label = "Compile frame";

        Settings = Side.Add("material-parameters");
        Diagnostics = Side.Add("analysis-list");

        AddHandler<ClickEvent>(static (element, args) => ((CompositorView) element).Chosen(args));
    }

    /// <summary>Shows a compositor.</summary>
    /// <param name="compositor">The document.</param>
    public void Show(CompositorDocument compositor) {
        ArgumentNullException.ThrowIfNull(compositor);

        if (document is { } previous) {
            previous.Compiled -= Report;
        }

        document = compositor;

        GraphView.Registry = compositor.Registry;
        GraphView.EditedDocument = compositor;
        GraphView.Graph = compositor.Graph;

        // ⚠ The canvas's selection handler is subscribed once per view; the document's is per
        // document, because a view outlives the file it was first shown.
        if (!listening) {
            listening = true;
            GraphView.SelectionChanged += _ => Restate();
        }

        compositor.Compiled += Report;

        Restate();
        Report(compositor);
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A closed panel has to let go of the document</b>, for the reason
    ///     <c>VfxGraphView.OnRemoved</c> states: a factory runs again on every reopen, and a view
    ///     still subscribed to a document it has left is one whose <see cref="Report" /> writes into
    ///     elements that are no longer in the tree.
    /// </remarks>
    protected override void OnRemoved() {
        base.OnRemoved();

        if (document is { } compositor) {
            compositor.Compiled -= Report;
            document = null;
        }
    }

    /// <summary>Compiles the graph and lists what it said.</summary>
    /// <returns>The number of complaints.</returns>
    public int Compile() {
        document?.Compile();
        return document?.Diagnostics.Count ?? 0;
    }

    /// <summary>Rebuilds the settings panel for whatever is selected.</summary>
    public void Restate() {
        while (Settings.Children.Count > 0) {
            Settings.Children[^1].Remove();
        }

        editors.Clear();

        if (document is not { } compositor || GraphView.Selection.Count != 1) {
            // One at a time. Two nodes of different types have no set of rows that fits both, which
            // is the same answer the inspector gives a mixed selection.
            Settings.Add("text").Text = GraphView.Selection.Count > 1
                ? "Several nodes selected."
                : "Select a node to edit its settings.";

            return;
        }

        var id = GraphView.Selection.First();

        if (!compositor.Graph.TryGet(id, out var node)
            || !compositor.Registry.TryGet(node.Type, out var definition)
            || definition.Create() is not CompositorNode instance) {
            return;
        }

        Settings.Add("text").Text = node.Type;

        foreach (var field in instance.Fields) {
            Row(compositor, node, field);
        }
    }

    /// <summary>How many settings rows are showing.</summary>
    public int RowCount => editors.Count;

    void Row(CompositorDocument compositor, GraphNode node, CompositorField field) {
        var row = Settings.Add("fact-row");
        row.Add("fact-name").Text = field.Label;

        var slot = row.Add("fact-value");

        switch (field.Kind) {
            case CompositorFieldKind.Toggle: {
                var toggle = slot.Add<CheckBox>();
                toggle.IsChecked = Lanes(node, field) != 0f;

                toggle.CheckedChanged += (_, value) => Write(compositor, node, field, value ? 1f : 0f);
                editors[toggle] = field;

                break;
            }

            case CompositorFieldKind.Number: {
                var number = slot.Add<NumericInput>();
                number.Number = Lanes(node, field);

                number.ValueChanged += (_, _) => Write(compositor, node, field, (float) number.Number);
                editors[number] = field;

                break;
            }

            case CompositorFieldKind.Choice: {
                var select = slot.Add<Select>();

                foreach (var option in field.Options ?? []) {
                    select.AddOption(option);
                }

                select.Value = node.TextOf(field.Key) is { Length: > 0 } chosen
                    ? chosen
                    : field.Options is { Length: > 0 } options
                        ? options[0]
                        : null;

                select.SelectionChanged += (_, value) => Write(compositor, node, field, value ?? string.Empty);
                editors[select] = field;

                break;
            }

            default: {
                var text = slot.Add<TextBox>();
                text.Value = node.TextOf(field.Key);

                text.Placeholder = field.Kind == CompositorFieldKind.Names ? "name, name" : null;
                text.ValueChanged += (_, value) => Write(compositor, node, field, value ?? string.Empty);
                editors[text] = field;

                break;
            }
        }

        if (field.Help.Length > 0) {
            // On the label rather than on the editor, so hovering the thing being edited does not
            // cover it with a description of itself — the inspector's rule, restated.
            var tooltip = row.Add<Tooltip>();
            tooltip.Label = field.Help;
            tooltip.Attach(row.Children[0]);
        }
    }

    static float Lanes(GraphNode node, CompositorField field) =>
        node.Values.TryGetValue(field.Key, out var lanes) && lanes.Length > 0 ? lanes[0] : field.Fallback;

    static void Write(CompositorDocument compositor, GraphNode node, CompositorField field, float value) =>
        compositor.Stack.Execute(new SetPortValueCommand(compositor.Graph, node.Id, field.Key, [value], compositor));

    static void Write(CompositorDocument compositor, GraphNode node, CompositorField field, string value) =>
        compositor.Stack.Execute(new SetPortTextCommand(compositor.Graph, node.Id, field.Key, value, compositor));

    void Report(CompositorDocument compositor) {
        while (Diagnostics.Children.Count > 0) {
            Diagnostics.Children[^1].Remove();
        }

        foreach (var diagnostic in compositor.LoadDiagnostics) {
            Line(diagnostic.Id, diagnostic.Message);
        }

        foreach (var diagnostic in compositor.Diagnostics) {
            Line(diagnostic.Id, diagnostic.Message);
        }

        // Said on success too, for the reason the group analysis gives: a list that empties itself
        // when everything is fine is one nobody can tell apart from a list that never ran.
        if (compositor.Frame is { } frame) {
            Line(
                "frame",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{frame.Stages.Length} stages, {frame.Resources.Length} targets, {frame.Buffers.Length} buffers."
                )
            );
        }
    }

    void Line(string stage, string message) {
        var row = Diagnostics.Add("analysis-row");

        row.Add("analysis-stage").Text = stage;
        row.Add("analysis-message").Text = message;
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, Build)) {
                Compile();
                args.Handled = true;

                return;
            }
        }
    }
}

/// <summary>Opens a graphics compositor.</summary>
public sealed class CompositorEditorFactory : IAssetEditorFactory {
    readonly NodeTypeRegistry registry = CompositorGraphCompiler.CreateRegistry();

    /// <inheritdoc />
    public string Name => "Graphics Compositor";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [CompositorDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        // One registry for every compositor this editor opens, because a node library is a property
        // of the build rather than of a file — and a registry per document would mean two open
        // compositors disagreeing about what a node type is.
        return new CompositorDocument(request.Project, request.Asset, request.Path, registry);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<CompositorView>();
        view.Show((CompositorDocument) document);

        return view;
    }
}
