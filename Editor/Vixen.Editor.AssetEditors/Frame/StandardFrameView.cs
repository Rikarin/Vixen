// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.Core;
using Vixen.Editor.Inspector;
using Vixen.Editor.SceneView;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.HotReload;

namespace Vixen.Editor.AssetEditors.Frame;

/// <summary>Doc 39's editor surface: the knobs, the look, the two resolved stacks, and Explode.</summary>
/// <remarks>
///     <para>
///         <b>Four things that are each a view over something that already exists.</b> The knobs and
///         the look are inspectors over mirrors of the node's own members; the quality table is
///         <see cref="ResolvedQualityTable" /> walking the waterfall's own schema; the volume panel
///         is <see cref="ResolvedVolumes" /> reading the engine's fold. Nothing here computes a
///         rendering fact of its own, which is the property that keeps the panel from becoming a
///         second opinion about what the frame is.
///     </para>
///     <para>
///         ⚠ <b>Both stacks say where a number came from, and that is the whole reason they are
///         panels rather than readouts.</b> The quality waterfall folds per parameter across three
///         files and the volume fold across four layers, so every number on this screen has a
///         provenance that is not visible in the number. A table of decided values would answer
///         "what is it" while the question somebody opens this panel with is always "why is it
///         that", and sending them to the wrong file to change it is worse than showing nothing.
///     </para>
///     <para>
///         ⚠ <b>Every write re-expands and the facts move with it.</b> The document rebuilds the
///         expansion on each edit — see <see cref="StandardFrameDocument.Changed" /> — so turning
///         shadows off takes a stage and two targets out of the count under the form, and a
///         guardrail refusal arrives on the edit that caused it rather than at the next launch.
///     </para>
///     <para>
///         ⚠ <b>A view is rebuilt on every reopen, so nothing durable lives here.</b> The same rule
///         <c>CompositorView</c> states, and the same consequence: the subscription to the document
///         is dropped in <see cref="OnRemoved" />, because a view still listening to a document it
///         has left writes into elements that are no longer in the tree.
///     </para>
/// </remarks>
public sealed class StandardFrameView : Control {
    readonly ResolvedVolumes volumes = new();

    StandardFrameDocument? document;
    bool overriddenOnly;

    /// <summary>Whether the panel is the thing that caused the change it is being told about.</summary>
    /// <remarks>
    ///     ⚠ <b>Without it, editing a knob rebuilds the form the knob is in.</b> A write raises the
    ///     document's <see cref="StandardFrameDocument.Changed" />, and the full restate re-inspects
    ///     both forms — which throws away the row being typed into, and with it the caret. The
    ///     derived sections still rebuild; only the two inspectors are left alone.
    /// </remarks>
    bool applying;

    /// <inheritdoc />
    protected override string TagName => "frame-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Where custom inspectors are found, so the markup forms are used rather than the rows.</summary>
    /// <remarks>
    ///     ⚠ <b>Handed in rather than read off <c>EditorRegistry.Default</c>.</b> The static is a
    ///     different session's in a test run — the reason <c>TerrainModulePanels</c> gives — and a
    ///     panel that found somebody else's contribution for its own settings type would draw
    ///     somebody else's form. Null is not a failure: the generated rows are drawn instead, in
    ///     declaration order, and everything still edits.
    /// </remarks>
    public IEditorRegistry? Extensions { get; set; }

    /// <summary>The scene whose volumes the stack panel folds, or null for an editor without one.</summary>
    public IActiveScene? Scene { get; set; }

    /// <summary>The view the editor is looking through, whose position decides which volumes reach.</summary>
    public IActiveView? Eye { get; set; }

    /// <summary>The knobs.</summary>
    public InspectorView Knobs { get; private set; } = null!;

    /// <summary>The look profile the document carries inline.</summary>
    public InspectorView Look { get; private set; } = null!;

    /// <summary>The button that replaces the node with the graph it stands for.</summary>
    public Button Explode { get; private set; } = null!;

    /// <summary>What the expansion produced, and what it complained about.</summary>
    public UiElement Facts { get; private set; } = null!;

    /// <summary>The resolved quality stack.</summary>
    public UiElement Quality { get; private set; } = null!;

    /// <summary>The resolved volume stack.</summary>
    public UiElement Stack { get; private set; } = null!;

    /// <summary>What the fold said about itself — the camera, the counts, and what did not reach.</summary>
    public UiElement Summary { get; private set; } = null!;

    /// <summary>What the panel is saying about the document as a whole.</summary>
    public UiElement Banner { get; private set; } = null!;

    /// <summary>How many quality rows are showing, which is what a test counts.</summary>
    public int QualityRows { get; private set; }

    /// <summary>And how many volume-parameter rows.</summary>
    public int StackRows { get; private set; }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Banner = Add("frame-banner");

        var scroll = Add<ScrollView>();
        var body = scroll.Content.Add("frame-sections");

        Title(body, "The frame");

        Knobs = body.Add<InspectorView>();
        Knobs.EditedDocument = null;

        Title(body, "Look profile");

        Look = body.Add<InspectorView>();
        Look.EditedDocument = null;

        var verbs = body.Add("verb-row");

        Explode = verbs.Add<Button>();
        Explode.Label = "Explode to a full document";
        Explode.Clicked += _ => Eject();

        Title(body, "What it expands to");

        Facts = body.Add("analysis-list");

        Title(body, "Resolved quality");

        var filter = body.Add<CheckBox>();

        filter.Label = "Only what is overridden";
        filter.CheckedChanged += (_, value) => {
            overriddenOnly = value;
            RestateQuality();
        };

        Quality = Table(body);

        Title(body, "Volumes reaching the camera");

        var refresh = body.Add<Button>();

        refresh.Label = "Fold again";
        refresh.Clicked += _ => RestateVolumes();

        Summary = body.Add("analysis-list");
        Stack = Table(body);
    }

    /// <summary>A list of provenance rows, in the panel's own scroll region.</summary>
    /// <remarks>
    ///     ⚠ <b>Not a <see cref="ScrollView" /> of its own, and that was measured rather than
    ///     assumed.</b> A nested scroll region's content is <c>align-self: flex-start</c> with
    ///     <c>min-width: 100%</c>, so its rows are sized against a width that is its own content's
    ///     rather than the panel's — and the fixed-width provenance column, which is the entire
    ///     point of these tables, resolved to nothing and drew nothing. On device, three times, in
    ///     three arrangements. The panel scrolls as one thing instead, and "Only what is overridden"
    ///     is the control that makes the long table short.
    /// </remarks>
    static UiElement Table(UiElement parent) => parent.Add("frame-knobs");

    /// <summary>Shows a frame document.</summary>
    /// <param name="frame">The document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame" /> is null.</exception>
    public void Show(StandardFrameDocument frame) {
        ArgumentNullException.ThrowIfNull(frame);

        if (document is { } previous) {
            previous.Changed -= Restate;
        }

        document = frame;

        Knobs.Extensions = Extensions ?? Knobs.Extensions;
        Look.Extensions = Extensions ?? Look.Extensions;

        frame.Changed += Restate;

        Restate(frame);
    }

    /// <inheritdoc />
    protected override void OnRemoved() {
        base.OnRemoved();

        if (document is { } frame) {
            frame.Changed -= Restate;
            document = null;
        }

    }

    /// <summary>Rebuilds everything the document decides.</summary>
    /// <param name="frame">The document.</param>
    public void Restate(StandardFrameDocument frame) {
        ArgumentNullException.ThrowIfNull(frame);

        document = frame;

        Clear(Banner);

        // ⚠ The banner is the transition doc 39 asks for. After an explode the file is a full
        // document and the form over it is gone; a panel that kept drawing knobs would be a form
        // whose every write was discarded, which is the silent half of "one-way".
        Banner.Add("text").Text = frame.CanEdit
            ? $"!StandardFrame — {(frame.TierIsHosts ? "no tier written, so the host decides" : $"tier {frame.Tier}")}"
            : "A hand-authored document. There is no frame node to turn, so the knobs are hidden and "
            + "the panel does not write this file.";

        if (!applying) {
            Knobs.Inspect(frame.CanEdit ? [frame.Settings] : []);
            Look.Inspect(frame.CanEdit ? [frame.Look] : []);

            Watch(Knobs);
            Watch(Look);
        }

        Explode.Disabled = !frame.CanExplode;

        RestateFacts(frame);
        RestateQuality();
        RestateVolumes();
    }

    /// <summary>Subscribes to every row of a form, so a write reaches the document.</summary>
    /// <remarks>
    ///     ⚠ <b><c>InspectorView.ValueChanged</c> does not fire for a custom inspector, which is the
    ///     shape this panel is.</b> The event is raised from the generated-row loop, and a
    ///     <c>CustomInspector</c> returns before that loop runs — so a markup form's writes are
    ///     silent to whoever is holding the view. The edit target's own properties do raise
    ///     <c>Changed</c>, and they are the same objects the markup bound to, so this is the same
    ///     notification one layer down rather than a second mechanism.
    /// </remarks>
    void Watch(InspectorView view) {
        if (view.Edited is not { } edited) {
            return;
        }

        // ⚠ A fresh `InspectorTarget` is built by every `Inspect`, so these are new properties with
        // no subscribers rather than the same ones subscribed twice.
        foreach (var property in edited.Properties()) {
            property.Changed += _ => Applied();
        }
    }

    void Applied() {
        if (applying || document is not { } frame) {
            return;
        }

        applying = true;

        try {
            // The document raises `Changed`, which is what restates the panel — so the write path
            // and the reload path are the same path and cannot drift.
            frame.Apply();
        } finally {
            applying = false;
        }
    }

    void Eject() {
        if (document is not { } frame || !frame.CanExplode) {
            return;
        }

        try {
            var kept = frame.Explode();

            Clear(Facts);

            Line(
                Facts,
                "exploded",
                $"The knobs are gone and this file is now hand-authored. What was there is beside it, "
                + $"as {Path.GetFileName(kept)}."
            );
        } catch (InvalidOperationException refusal) {
            Clear(Facts);
            Line(Facts, "refused", refusal.Message, "error");
        }
    }

    void RestateFacts(StandardFrameDocument frame) {
        Clear(Facts);

        var expanded = frame.Expanded;

        // Said on success too, for the reason the compositor's own analysis gives: a list that
        // empties itself when everything is fine is one nobody can tell apart from one that never ran.
        Line(
            Facts,
            "frame",
            string.Create(
                CultureInfo.InvariantCulture,
                $"{expanded.Stages.Length} stages, {expanded.Resources.Length} targets, "
                + $"{expanded.Buffers.Length} buffers."
            )
        );

        Line(
            Facts,
            "quality",
            frame.Preset is null
                ? $"Engine defaults for {frame.Tier}. No {StandardFrameDocument.PresetFile} beside the frame."
                : $"{StandardFrameDocument.PresetFile} over the engine table, for {frame.Tier}."
        );

        foreach (var complaint in frame.Diagnostics) {
            Line(Facts, "refused", complaint, "error");
        }
    }

    void RestateQuality() {
        Clear(Quality);
        QualityRows = 0;

        if (document is not { } frame) {
            return;
        }

        var group = string.Empty;

        foreach (var knob in ResolvedQualityTable.Resolve(frame.Tier, frame.Preset, frame.Node?.Preset)) {
            if (overriddenOnly && !knob.Overridden) {
                continue;
            }

            if (!string.Equals(group, knob.Group, StringComparison.Ordinal)) {
                group = knob.Group;
                Quality.Add("frame-group").Text = group;
            }

            var row = Quality.Add("fact-row");

            row.Add("fact-name").Text = knob.Name;

            // ⚠ The provenance is *in* the value cell, not in a column of its own, and that is a
            // finding rather than a preference. A third cell with a fixed width sits at the right
            // edge of a docked panel, where a long member name, a padded scroll region or a
            // scrollbar each push it out of view — measured three ways on device, invisible every
            // time. A cell that always draws is worth more than a column that sometimes aligns.
            //
            // ⚠ Short names, because the cell is narrow: `RenderQuality.vxpreset` clipped to
            // `RenderQuality.vxpres…` says less than `vxpreset`. The file's full name is on the
            // line above the table, once.
            row.Add("fact-value").Text = $"{knob.Value}   {knob.Layer switch {
                QualityLayer.Document => "document",
                QualityLayer.Project => "vxpreset",
                _ => "engine"
            }}";

            if (knob.Overridden) {
                // Engine values are the rule and the other two are the exceptions, so only the
                // exceptions are coloured — a table where every row is highlighted highlights nothing.
                row.AddClass("overridden");
            }

            QualityRows++;
        }

        if (QualityRows == 0) {
            Quality.Add("text").Text = "Nothing above the engine table states a value for this tier.";
        }
    }

    void RestateVolumes() {
        Clear(Stack);
        Clear(Summary);

        StackRows = 0;

        if (document is not { } frame) {
            return;
        }

        if (Scene?.Current is not { } scene) {
            Summary.Add("text").Text = "No scene is open, so there is nothing to fold.";
            return;
        }

        // ⚠ The document's own look is laid down as the base layer, because that is what the host
        // does: `PostEffectFactory` deposits it on the builder and `AppGraphics` hands it to the
        // fold. A panel that folded volumes without it would show a stack the game never has.
        volumes.Look = frame.Look.ToSettings();
        volumes.Camera = Eye?.Current?.Position ?? Vector3.Zero;

        var report = volumes.Fold(scene.World);

        // ⚠ Above the table rather than in it, and not only so it stays put while the parameters
        // scroll: a sentence is as wide as it is, and a scroll region sized to its content is a
        // region whose columns start off the right edge — which is where the provenance column went
        // the first time these four lines shared the table's box.
        Line(Summary, "camera", Where(volumes.Camera));
        Line(Summary, "fold", report.Summary);

        if (report.HasLook) {
            Line(Summary, "look", "The document's look profile is the base layer, at full weight.");
        }

        if (report.Volumes > report.Contributing) {
            // ⚠ Two short lines rather than one long one, and the reason is the panel's width: a
            // sentence in this column wraps against the scroll content's width rather than the
            // dock's, so a line past about sixty characters runs off the right edge mid-word. The
            // cause list is the useful half and must not be the half that is cut.
            Line(Summary, "note", $"{report.Volumes - report.Contributing} placed and not reaching.", "warning");
            Line(Summary, "why", "Out of range, zero weight, zero extents, or says nothing.", "warning");
        }

        foreach (var parameter in report.Parameters) {
            var row = Stack.Add("fact-row");

            row.Add("fact-name").Text = parameter.Parameter;

            // The same three facts the quality table shows, in the same cell and for the same
            // reason: what it resolved to, at what weight, and which layer had the last word.
            row.Add("fact-value").Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{parameter.Value} ×{parameter.Weight:0.##}   {Short(parameter.Winner)}"
                + $"{(parameter.IsContested ? $" of {parameter.Layers.Count}" : string.Empty)}"
            );

            if (parameter.IsContested) {
                // ⚠ Contested is the interesting state, not "came from a volume". A parameter one
                // layer claims is doing what its author asked; a parameter three layers claim is
                // where somebody's edit is being quietly out-voted.
                row.AddClass("overridden");
            }

            StackRows++;
        }
    }

    /// <summary>A layer name that fits the column.</summary>
    /// <remarks>
    ///     ⚠ <b>Shortened here and not in <see cref="ResolvedVolumes" />.</b> The fold's own spelling
    ///     is <c>volume(priority 3)</c> — <c>PostProcessVolumeSystem.Contributions</c> writes it, a
    ///     log line quotes it, and a test asserts it — so the data keeps it and only the cell is
    ///     narrowed. A clipped <c>volume(prior…</c> tells a reader nothing; the priority is the part
    ///     that distinguishes one volume from the next.
    /// </remarks>
    static string Short(string layer) => layer.StartsWith("volume(priority ", StringComparison.Ordinal)
        ? $"priority {layer[16..].TrimEnd(')')}"
        : layer;

    static string Where(Vector3 point) => string.Create(
        CultureInfo.InvariantCulture,
        $"{point.X:0.##}, {point.Y:0.##}, {point.Z:0.##}"
    );

    static void Title(UiElement parent, string text) => parent.Add("world-title").Text = text;

    static void Line(UiElement into, string stage, string message, string? kind = null) {
        var row = into.Add("analysis-row");

        if (kind is not null) {
            row.AddClass(kind);
        }

        row.Add("analysis-stage").Text = stage;
        row.Add("analysis-message").Text = message;
    }

    static void Clear(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }
}

/// <summary>Opens a frame document as knobs.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It claims <c>.vxcompositor</c>, which nothing claimed before it.</b>
///         <c>CompositorEditorFactory</c> claims <c>.vxcomp</c> — a node graph that <em>compiles
///         to</em> a frame — and the frame document itself opened in nothing at all: double-clicking
///         the file a project actually ships did nothing. So this is the editor for the format,
///         with the knobs as its main view and the resolved stacks shown for a hand-authored
///         document too.
///     </para>
///     <para>
///         The four last-mile services are properties rather than constructor arguments because the
///         registry is built before the modules are activated — see <c>AssetEditorsModule</c>, whose
///         whole job is exactly this kind of binding.
///     </para>
/// </remarks>
public sealed class StandardFrameEditorFactory : IAssetEditorFactory {
    /// <summary>What this editor is called, which is how the module finds it to bind it.</summary>
    public const string EditorName = "Frame";

    /// <inheritdoc />
    public string Name => EditorName;

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [StandardFrameDocument.Extension];

    /// <summary>Where the markup inspectors are registered, or null for the generated rows.</summary>
    public IEditorRegistry? Contributions { get; set; }

    /// <summary>The scene whose volumes the stack panel folds.</summary>
    public IActiveScene? Scene { get; set; }

    /// <summary>The view the editor is looking through.</summary>
    public IActiveView? Eye { get; set; }

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new StandardFrameDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        // ⚠ This view owns the scroller and keeps its banner outside it, and it is also the view whose
        // remarks record what nesting one scroll region in another did to it: the inner content became
        // `align-self: flex-start` against its own width, and the fixed-width provenance column
        // resolved to nothing. A panel that scrolled would rebuild that arrangement from the outside.
        DockPanel.Fills(panel);

        var view = panel.Add<StandardFrameView>();

        view.Extensions = Contributions;
        view.Scene = Scene;
        view.Eye = Eye;

        view.Show((StandardFrameDocument) document);

        return view;
    }

    /// <summary>The two markup forms, contributed to a registry.</summary>
    /// <param name="registry">Where they go.</param>
    /// <param name="reload">The document's reload host, or null for an editor without one.</param>
    /// <returns>What removes them again.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="registry" /> is null.</exception>
    /// <remarks>
    ///     Doc 36 § P4's shape, unchanged from the terrain module's: a <c>.vxml</c> component
    ///     mounted through the reload host, so changing the markup changes the panel a second later
    ///     rather than at the next launch.
    /// </remarks>
    public static IDisposable[] Contribute(IEditorRegistry registry, HotReloadHost? reload) {
        ArgumentNullException.ThrowIfNull(registry);

        return [
            registry.Add(
                new CustomInspector(
                    typeof(StandardFrameSettings),
                    MarkupInspector.Of<StandardFrameInspector>(reload)
                )
            ),
            registry.Add(
                new CustomInspector(typeof(LookSettings), MarkupInspector.Of<LookInspector>(reload))
            )
        ];
    }
}
