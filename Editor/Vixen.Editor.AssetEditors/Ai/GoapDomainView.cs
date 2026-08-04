// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Editor.Ai;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>A GOAP domain: the tables that are authored, and the graph that is not.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5's GOAP editor. Goals, actions and world keys are <b>tables</b>; conditions
///         and effects are rows on an action. The graph beside them is <b>derived and read-only</b>,
///         and it has no command stack — which <c>NodeGraphView</c> already supports, since <i>"no
///         stack means read-only"</i>.
///     </para>
///     <para>
///         ⚠ <b>This is where "the node editor is mandatory" gets an honest answer.</b> crashkonijn
///         ships a GraphViewer rather than a graph editor, and that is right: the edges are computed
///         from which effects satisfy which conditions, so drawing them by hand would be authoring the
///         same fact twice and the two copies would disagree the first time somebody edited a
///         condition.
///     </para>
/// </remarks>
public sealed class GoapDomainView : Control {
    readonly GoapGraphProjection projection = new();

    GoapDomainDocument? document;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "goap-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The derived graph. ⚠ Read-only, and deliberately without a command stack.</summary>
    public NodeCanvas Canvas { get; private set; } = null!;

    /// <summary>The goals.</summary>
    public UiElement Goals { get; private set; } = null!;

    /// <summary>The actions.</summary>
    public UiElement Actions { get; private set; } = null!;

    /// <summary>The world keys.</summary>
    public UiElement Keys { get; private set; } = null!;

    /// <summary>What the last compile said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <summary>The button that compiles the domain and rebuilds the graph.</summary>
    public Button Build { get; private set; } = null!;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var body = Add("goap-body");

        Canvas = body.Add<NodeCanvas>();

        // Left to right: goals, then what serves them, then what serves those — the direction the
        // search walks, so a domain that plans four deep looks four deep.
        Canvas.Orientation = GraphOrientation.Horizontal;

        var side = body.Add("goap-side");
        var toolbar = side.Add("goap-toolbar");

        Build = toolbar.Add<Button>();
        Build.Text = "Compile";

        side.Add("panel-title").Text = "Goals";
        Goals = side.Add("goap-goals");

        side.Add("panel-title").Text = "Actions";
        Actions = side.Add("goap-actions");

        side.Add("panel-title").Text = "World keys";
        Keys = side.Add("goap-keys");

        side.Add("panel-title").Text = "Diagnostics";
        Diagnostics = side.Add("goap-diagnostics");
    }

    /// <summary>Shows a document.</summary>
    /// <param name="value">The domain.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public void Show(GoapDomainDocument value) {
        ArgumentNullException.ThrowIfNull(value);

        document = value;

        if (!listening) {
            document.Changed += _ => Refresh();
            listening = true;
        }

        Refresh();
        Compile();
    }

    /// <summary>Compiles the domain, lists what it said and redraws the derived graph.</summary>
    /// <param name="plan">A plan to highlight on it, or null.</param>
    /// <returns>The domain, or null.</returns>
    public GoapDomain? Compile(GoapPlan? plan = null) {
        if (document is null) {
            return null;
        }

        var domain = document.Compile();

        Empty(Diagnostics);

        foreach (var diagnostic in document.Diagnostics) {
            var row = Diagnostics.Add("analysis-row");

            row.Add("analysis-stage").Text = diagnostic.Node.IsSome ? diagnostic.Node.ToString() : "domain";
            row.Add("analysis-message").Text = diagnostic.Message;
        }

        if (domain is not null) {
            Canvas.Graph = projection.Project(domain, plan);

            if (document.Diagnostics.Count == 0) {
                var row = Diagnostics.Add("analysis-row");

                row.Add("analysis-stage").Text = "domain";
                row.Add("analysis-message").Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{domain.Name} compiles: {domain.Count} action(s), {domain.Goals.Length} goal(s), "
                    + $"{domain.Keys.Count} world key(s), {domain.EdgeCount} derived edge(s)."
                );
            }
        }

        return domain;
    }

    /// <summary>Rebuilds the three tables from the document.</summary>
    public void Refresh() {
        if (document is null) {
            return;
        }

        Empty(Goals);
        Empty(Actions);
        Empty(Keys);

        foreach (var goal in document.Content.Goals) {
            var row = Goals.Add("goap-row");

            row.Add("goap-name").Text = goal.Name;
            row.Add("goap-priority").Text = goal.Priority.ToString(CultureInfo.InvariantCulture);
            row.Add("goap-detail").Text = string.Join(", ", goal.Conditions.Select(Describe));
        }

        foreach (var action in document.Content.Actions) {
            var row = Actions.Add("goap-row");

            row.Add("goap-name").Text = action.Name;
            row.Add("goap-task").Text = action.Task;
            row.Add("goap-cost").Text = action.Cost.ToString("0.##", CultureInfo.InvariantCulture);
            row.Add("goap-detail").Text = string.Join(", ", action.Conditions.Select(Describe));
            row.Add("goap-effects").Text = string.Join(
                ", ",
                action.Effects.Select(effect => $"{(effect.Increases ? "+" : "−")} {effect.Key}")
            );
        }

        foreach (var key in document.Content.Keys) {
            var row = Keys.Add("goap-row");

            row.Add("goap-name").Text = key.Name;
            row.Add("goap-source").Text = key.Source.ToString();
            row.Add("goap-detail").Text = key.Source == GoapSourceKind.Constant
                ? key.Value.ToString(CultureInfo.InvariantCulture)
                : key.From;
        }
    }

    /// <summary>One condition, as a person reads it.</summary>
    /// <param name="condition">The condition.</param>
    /// <returns>Its text.</returns>
    public static string Describe(GoapConditionContent condition) {
        ArgumentNullException.ThrowIfNull(condition);

        var symbol = condition.Comparison switch {
            GoapComparison.Less => "<",
            GoapComparison.LessOrEqual => "≤",
            GoapComparison.Greater => ">",
            _ => "≥"
        };

        return $"{condition.Key} {symbol} {condition.Value.ToString(CultureInfo.InvariantCulture)}";
    }

    static void Empty(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }
}

/// <summary>Opens a <c>.vxgoap</c>.</summary>
public sealed class GoapDomainEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "GOAP Domain";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [GoapDomainDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new GoapDomainDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<GoapDomainView>();

        view.Show((GoapDomainDocument) document);

        return view;
    }
}
