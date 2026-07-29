// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Editor.Core;
using Vixen.Editor.NodeGraph;
using Vixen.Editor.VfxGraph;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Vfx;

/// <summary>An effect, open for editing: the graph, the selected block, and the effect running.</summary>
/// <remarks>
///     <para>
///         Doc 11's row for this asset is "node graph + live preview viewport", and the three
///         columns here are that: the canvas, which is <see cref="NodeGraphView" />'s and is already
///         undoable in every gesture; the selected block's numbers, which are
///         <see cref="NodeInspector" />'s; and the effect itself.
///     </para>
///     <para>
///         ⚠ <b>Compiling and previewing are one button, not two.</b> A preview over a stale artefact
///         is the failure mode that wastes an afternoon — the author changes a block, watches the old
///         effect, and concludes the block does nothing. So <see cref="Compile" /> is what produces
///         both, and the preview pane draws whatever the last compile left behind.
///     </para>
/// </remarks>
public sealed class VfxGraphView : Control {
    VfxDocument? document;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "vfx-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The canvas.</summary>
    public NodeGraphView GraphView { get; private set; } = null!;

    /// <summary>The column beside it.</summary>
    public UiElement Side { get; private set; } = null!;

    /// <summary>The selected block's settings.</summary>
    public NodeInspector Settings { get; private set; } = null!;

    /// <summary>The effect, running.</summary>
    public VfxPreviewView Preview { get; private set; } = null!;

    /// <summary>The button that compiles the graph and restarts the preview.</summary>
    public Button Build { get; private set; } = null!;

    /// <summary>The button that starts the effect again.</summary>
    public Button Restart { get; private set; } = null!;

    /// <summary>The one that pauses it.</summary>
    public ToggleButton Play { get; private set; } = null!;

    /// <summary>What the preview has to say about itself.</summary>
    public UiElement Readout { get; private set; } = null!;

    /// <summary>What compiling said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        GraphView = Part<NodeGraphView>();

        Side = Part("vfx-side");

        var transport = Side.Add("vfx-transport");

        Build = transport.Add<Button>();
        Build.Label = "Compile";

        Restart = transport.Add<Button>();
        Restart.Label = "Restart";

        Play = transport.Add<ToggleButton>();
        Play.Label = "Play";
        Play.IsChecked = true;

        Preview = Side.Add<VfxPreviewView>();
        Readout = Side.Add("vfx-readout");

        Settings = Side.Add<NodeInspector>();
        Settings.EmptyMessage = "Select a block to edit its numbers.";

        Diagnostics = Side.Add("analysis-list");

        Play.CheckedChanged += (_, on) => Preview.IsPlaying = on;

        AddHandler<ClickEvent>(static (element, args) => ((VfxGraphView) element).Chosen(args));
    }

    /// <summary>Shows an effect.</summary>
    /// <param name="effect">The document.</param>
    public void Show(VfxDocument effect) {
        ArgumentNullException.ThrowIfNull(effect);

        if (document is { } previous) {
            previous.Compiled -= Report;
        }

        document = effect;

        GraphView.Registry = effect.Registry;
        GraphView.EditedDocument = effect;
        GraphView.Graph = effect.Graph;

        Settings.View = GraphView;
        Settings.EditedDocument = effect;

        // ⚠ The canvas's selection handler is subscribed once per view and the document's per
        // document, because a view outlives the file it was first shown — the same split
        // `CompositorView` makes and for the same reason.
        if (!listening) {
            listening = true;
            GraphView.SelectionChanged += _ => Settings.Rebuild();
        }

        effect.Compiled += Report;

        Settings.Rebuild();

        // ⚠ Compiled on open rather than waiting to be asked, which is the opposite of the rule
        // `Compile` states for an *edit*. Opening is not an edit: an author who has just double
        // clicked an effect wants to see it, and a preview that stayed empty until a button was
        // found reads as an effect that produces nothing.
        Compile();
    }

    /// <summary>Compiles the graph, restarts the preview, and lists what compiling said.</summary>
    /// <returns>The number of complaints.</returns>
    public int Compile() {
        document?.Compile();

        Preview.System = document?.Preview;
        Preview.Restart();

        return document?.Diagnostics.Count ?? 0;
    }

    /// <summary>Steps the preview and brings its readout up to date.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>Pulled by whoever owns the frame, not driven from inside the control.</b> A panel's
    ///     factory runs again on every reopen, so a timer this view started would outlive the view
    ///     and step a simulation nothing is drawing.
    /// </remarks>
    public void Tick(TimeSpan delta) {
        Preview.Step(delta);
        Readout.Text = Preview.Readout();
    }

    void Report(VfxDocument effect) {
        while (Diagnostics.Children.Count > 0) {
            Diagnostics.Children[^1].Remove();
        }

        foreach (var diagnostic in effect.LoadDiagnostics) {
            Line(diagnostic.Id, diagnostic.Message);
        }

        foreach (var diagnostic in effect.Diagnostics) {
            Line(diagnostic.Id, diagnostic.Message);
        }

        // Said on success too, for the reason the compositor's list gives: a list that empties
        // itself when everything is fine cannot be told apart from one that never ran.
        if (effect.Artefact is { } artefact) {
            Line(
                "effect",
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{artefact.Graph.Spawners.Length} spawner(s), {artefact.Graph.Initializers.Length} initializer(s), "
                    + $"{artefact.Graph.Updaters.Length} updater(s), {artefact.Graph.Capacity} particles, "
                    + $"{artefact.Graph.BytesPerParticle} B each."
                )
            );

            Line("shader", $"{artefact.Shader.Name}: {artefact.Shader.Bindings.Count} binding(s).");
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

            if (ReferenceEquals(element, Restart)) {
                Preview.Restart();
                args.Handled = true;

                return;
            }
        }
    }
}

/// <summary>Opens a VFX graph.</summary>
/// <remarks>
///     ⚠ <b>In this assembly rather than in <c>Vixen.Editor.VfxGraph</c>, which is where doc 20's
///     B5 files the row.</b> That assembly is the node library and the compiler, and it deliberately
///     knows nothing about a project, a document or a panel — which is what lets its tests compile a
///     graph with no editor in the way. The document and the view are the same shape every other row
///     of doc 11's asset-editor table has, and this is the assembly that holds them.
/// </remarks>
public sealed class VfxEditorFactory : IAssetEditorFactory {
    readonly NodeTypeRegistry registry = VfxNodeLibrary.Create();

    /// <inheritdoc />
    public string Name => "VFX Graph";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [VfxDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        // One registry for every effect this editor opens, for `VfxNodeLibrary`'s reason.
        return new VfxDocument(request.Project, request.Asset, request.Path, registry);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<VfxGraphView>();
        view.Show((VfxDocument) document);

        return view;
    }
}
