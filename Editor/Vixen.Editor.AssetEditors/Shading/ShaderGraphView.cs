// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.ShaderGraph;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Shading;

/// <summary>A shader graph, open for editing: the canvas, the generated Raven, and what it said.</summary>
/// <remarks>
///     <para>
///         Doc 11's row for this asset is a node graph with "show generated code", and the three
///         columns here are that: the canvas, which is <see cref="NodeGraphView" />'s and is already
///         undoable in every gesture; the emitted shader, in a read-only
///         <see cref="CodeEditor" /> with the same Raven highlighting the shader editor uses; and the
///         selected node's settings with the compile's complaints under them.
///     </para>
///     <para>
///         ⚠ <b>The generated source is read-only and hidden until asked for.</b> Editing it would be
///         editing an artefact — the next compile overwrites it, so a panel that let you type into it
///         would be one that quietly threw work away. Hidden by default because a graph is what the
///         author is looking at; the toggle is what doc 07 calls "show generated code", and it is
///         beside the button that produces it rather than in a menu.
///     </para>
///     <para>
///         ⚠ <b>Two lists of complaints, drawn as one.</b> The graph compiler's name a node, Raven's
///         name a line of the generated text, and an author wants to know about both without being
///         asked to look in two places. Which kind each is stays visible in the stage column —
///         <c>graph</c> against one and <c>raven</c> against the other — because the action they call
///         for is different: one is a wire, the other is a bug in this editor.
///     </para>
///     <para>
///         ⚠ <b>A property node's name is edited here rather than in the node inspector.</b> The
///         inspector draws <i>ports</i>, and a property's name is not one — it names a binding, so it
///         is a graph text. The row writes through <c>SetPortTextCommand</c> for the reason every
///         other field in this assembly does: typing a name is one undo entry, not one per keystroke.
///     </para>
/// </remarks>
public sealed class ShaderGraphView : Control {
    ShaderGraphDocument? document;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "shadergraph-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The canvas.</summary>
    public NodeGraphView GraphView { get; private set; } = null!;

    /// <summary>The pane the emitted shader is shown in.</summary>
    public UiElement Pane { get; private set; } = null!;

    /// <summary>The emitted shader, read-only.</summary>
    public CodeEditor Generated { get; private set; } = null!;

    /// <summary>The column beside the canvas.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The button that compiles the graph.</summary>
    public Button Build { get; private set; } = null!;

    /// <summary>The toggle that shows the generated Raven.</summary>
    public ToggleButton ShowCode { get; private set; } = null!;

    /// <summary>The selected node's settings.</summary>
    public NodeInspector Settings { get; private set; } = null!;

    /// <summary>Where the selected property node's name is edited.</summary>
    public UiElement PropertyRow { get; private set; } = null!;

    /// <summary>The box that renames it, or <see langword="null" /> when nothing named is selected.</summary>
    public TextBox? PropertyName { get; private set; }

    /// <summary>What the emitted shader declares.</summary>
    public UiElement Properties { get; private set; } = null!;

    /// <summary>What compiling said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        GraphView = Part<NodeGraphView>();

        Pane = Part("shadergraph-source");
        Pane.AddClass("hidden");

        Generated = Pane.Add<CodeEditor>();

        // The same tokenizer the shader editor uses, so generated Raven and hand-written Raven are
        // highlighted by one list of keywords rather than by two that drift.
        Generated.Tokenizer = CStyleTokenizer.Raven;
        Generated.ReadOnly = true;

        Side = Part("shadergraph-side");

        var transport = Side.Add("shadergraph-transport");

        Build = transport.Add<Button>();
        Build.Label = "Compile";

        ShowCode = transport.Add<ToggleButton>();
        ShowCode.Label = "Generated code";

        Settings = Side.Add<NodeInspector>();
        Settings.EmptyMessage = "Select a node to edit its values.";

        PropertyRow = Side.Add("shadergraph-property");
        Properties = Side.Add("analysis-list");
        Diagnostics = Side.Add("analysis-list");

        ShowCode.CheckedChanged += (_, on) => {
            if (on) {
                Pane.RemoveClass("hidden");
            } else {
                Pane.AddClass("hidden");
            }
        };

        AddHandler<ClickEvent>(static (element, args) => ((ShaderGraphView) element).Chosen(args));
    }

    /// <summary>Shows a graph.</summary>
    /// <param name="shader">The document.</param>
    public void Show(ShaderGraphDocument shader) {
        ArgumentNullException.ThrowIfNull(shader);

        if (document is { } previous) {
            previous.Compiled -= Report;
        }

        document = shader;

        GraphView.Registry = shader.Registry;
        GraphView.EditedDocument = shader;
        GraphView.Graph = shader.Graph;

        Settings.View = GraphView;
        Settings.EditedDocument = shader;

        // ⚠ The canvas's selection handler is subscribed once per view and the document's per
        // document, because a view outlives the file it was first shown — the same split
        // `CompositorView` makes and for the same reason.
        if (!listening) {
            listening = true;

            GraphView.SelectionChanged += _ => {
                Settings.Rebuild();
                Restate();
            };
        }

        shader.Compiled += Report;

        Settings.Rebuild();
        Restate();

        // ⚠ Compiled on open rather than waiting to be asked, which is the opposite of the rule
        // `Compile` states for an *edit*. Opening is not an edit: an author who has just double
        // clicked a graph wants to know whether it compiles, and a panel that said nothing until a
        // button was found reads as a graph with nothing wrong with it.
        Compile();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>A closed panel has to let go of the document, and this is the only place it can.</b> A
    ///     panel's factory runs again on every reopen, so the view that was closed is still subscribed
    ///     to a document that outlives it — and the next compile then calls <see cref="Report" /> on
    ///     elements that have left the tree, which is an exception rather than a stale picture. The
    ///     document is not the view's to own; closing the tab is what ends the subscription.
    /// </remarks>
    protected override void OnRemoved() {
        base.OnRemoved();

        if (document is { } shader) {
            shader.Compiled -= Report;
            document = null;
        }
    }

    /// <summary>Compiles the graph, shows the Raven it emitted, and lists what was said.</summary>
    /// <returns>The number of complaints, of both kinds.</returns>
    public int Compile() {
        if (document is not { } shader) {
            return 0;
        }

        shader.Compile();

        return shader.Diagnostics.Count + shader.SourceDiagnostics.Count;
    }

    /// <summary>Rebuilds the row that renames whatever property node is selected.</summary>
    public void Restate() {
        while (PropertyRow.Children.Count > 0) {
            PropertyRow.Children[^1].Remove();
        }

        PropertyName = null;

        if (document is not { } shader
            || GraphView.Selection.Count != 1
            || !shader.Graph.TryGet(GraphView.Selection.First(), out var node)
            || !shader.Registry.TryGet(node.Type, out var definition)
            || definition.Create() is not IShaderPropertyNode named) {
            return;
        }

        var row = PropertyRow.Add("fact-row");
        row.Add("fact-name").Text = "Property";

        var box = row.Add("fact-value").Add<TextBox>();

        box.Value = node.TextOf(ShaderProperties.Key);
        box.Placeholder = named.DefaultProperty;

        box.ValueChanged += (_, value) => shader.Stack.Execute(
            new SetPortTextCommand(shader.Graph, node.Id, ShaderProperties.Key, value ?? string.Empty, shader)
        );

        PropertyRow.Add("node-inspector-summary").Text =
            $"Declared as {named.PropertyType}. Two nodes under one name are one binding.";

        PropertyName = box;
    }

    void Report(ShaderGraphDocument shader) {
        Generated.Source = shader.Source?.Source ?? string.Empty;
        Generated.SetDiagnostics([.. shader.SourceDiagnostics]);

        while (Properties.Children.Count > 0) {
            Properties.Children[^1].Remove();
        }

        foreach (var property in shader.Source?.Properties ?? []) {
            Line(Properties, property.Type, property.Name);
        }

        while (Diagnostics.Children.Count > 0) {
            Diagnostics.Children[^1].Remove();
        }

        foreach (var diagnostic in shader.LoadDiagnostics) {
            Line(Diagnostics, "file", $"{diagnostic.Id}: {diagnostic.Message}");
        }

        foreach (var diagnostic in shader.Diagnostics) {
            Line(Diagnostics, "graph", $"{diagnostic.Id}: {diagnostic.Message}");
        }

        foreach (var diagnostic in shader.SourceDiagnostics) {
            // The line number, because that is where it is in the pane beside this — and it is the
            // only thing an author can act on until the emitter records which node wrote which span.
            Line(Diagnostics, "raven", $"line {diagnostic.Line + 1}: {diagnostic.Message}");
        }

        // Said on success too, for the reason the compositor's list gives: a list that empties itself
        // when everything is fine cannot be told apart from one that never ran.
        if (shader.Source is { } source && shader.SourceDiagnostics.Count == 0) {
            Line(
                Diagnostics,
                "shader",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{source.Name} compiles: {shader.Graph.Nodes.Count} node(s), "
                    + $"{source.Properties.Length} uniform(s), {Lines(source.Source)} lines of Raven."
                )
            );
        }
    }

    static int Lines(string source) {
        var count = 1;

        foreach (var character in source) {
            if (character == '\n') {
                count++;
            }
        }

        return count;
    }

    static void Line(UiElement list, string stage, string message) {
        var row = list.Add("analysis-row");

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

/// <summary>Opens a shader graph.</summary>
/// <remarks>
///     ⚠ <b>In this assembly rather than in <c>Vixen.Editor.ShaderGraph</c></b>, which is where doc
///     20's B5 files the row. That assembly is the node library and the compiler, and it deliberately
///     knows nothing about a project, a document or a panel — the same split
///     <c>VfxEditorFactory</c> states, and the reason the graph compiler can be tested with no editor
///     in the way.
/// </remarks>
public sealed class ShaderGraphEditorFactory : IAssetEditorFactory {
    readonly NodeTypeRegistry registry = ShaderNodeLibrary.Create();

    /// <inheritdoc />
    public string Name => "Shader Graph";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [ShaderGraphDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        // One registry for every graph this editor opens, for `ShaderNodeLibrary`'s reason.
        return new ShaderGraphDocument(request.Project, request.Asset, request.Path, registry);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ A node canvas pans and zooms in a space of its own and converts a pointer with
        // `Surface.AbsoluteLeft`, so a panel that also scrolled would be a second, invisible transform
        // between the cursor and the graph. The generated-source pane beside it is a `CodeEditor`,
        // which virtualises its own scrolling from its own viewport height.
        DockPanel.Fills(panel);

        var view = panel.Add<ShaderGraphView>();
        view.Show((ShaderGraphDocument) document);

        return view;
    }
}
