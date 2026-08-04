// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ai;
using Vixen.Ai.Diagnostics;
using Vixen.Editor.Ai;
using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>A utility set, open for editing: a table of actions, a table of considerations, and a curve.</summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5's utility editor, and the shape is the argument. <b>A utility set has no
///         edges</b>: drawing it on a canvas would be a canvas whose wires all run from a column of
///         inputs to a column of actions and carry nothing. So it is a two-pane table — actions on the
///         left with their scores as bars, the selected action's considerations on the right — and
///         under it the selected consideration's response curve.
///     </para>
///     <para>
///         ⚠ <b>The current input is marked on the curve, and that detail is the whole tool.</b> "Why
///         is this scoring 0.2" is answered by seeing where on the curve the agent is sitting, and it
///         has to be answerable while the game is not running — so the readings are a table an author
///         types into and the panel does the arithmetic the runtime would. In play mode the same
///         marker follows the selected agent.
///     </para>
///     <para>
///         ⚠ <b>The score bars are the compensated score, not the raw product.</b> A panel that showed
///         the product would show an action getting worse every time somebody added an axis to tune
///         it, which is exactly the confusion the geometric mean exists to remove.
///     </para>
/// </remarks>
public sealed class UtilitySetView : Control {
    readonly Dictionary<string, float> readings = new(StringComparer.Ordinal);

    AgentDebugModel? live;
    UtilitySetDocument? document;
    UtilityActionContent? action;
    UtilityConsiderationContent? consideration;
    bool listening;

    /// <inheritdoc />
    protected override string TagName => "utilityset-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The actions, with their scores as bars.</summary>
    public UiElement Actions { get; private set; } = null!;

    /// <summary>The selected action's considerations.</summary>
    public UiElement Considerations { get; private set; } = null!;

    /// <summary>What each input is reading, which is what the scores are computed from.</summary>
    public UiElement Readings { get; private set; } = null!;

    /// <summary>The selected consideration's response curve.</summary>
    public CurveEditor Curve { get; private set; } = null!;

    /// <summary>What the last compile said.</summary>
    public UiElement Diagnostics { get; private set; } = null!;

    /// <summary>The button that compiles the set.</summary>
    public Button Build { get; private set; } = null!;

    /// <summary>The action that is selected, or null.</summary>
    public UtilityActionContent? Selected => action;

    /// <summary>The consideration that is selected, or null.</summary>
    public UtilityConsiderationContent? SelectedConsideration => consideration;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        var body = Add("utilityset-body");
        var left = body.Add("utilityset-actions");

        left.Add("panel-title").Text = "Actions";
        Actions = left.Add("utilityset-action-list");

        var toolbar = left.Add("utilityset-toolbar");

        Build = toolbar.Add<Button>();
        Build.Label = "Compile";

        var right = body.Add("utilityset-considerations");

        right.Add("panel-title").Text = "Considerations";
        Considerations = right.Add("utilityset-consideration-list");

        right.Add("panel-title").Text = "Readings";
        Readings = right.Add("utilityset-readings");

        var bottom = Add("utilityset-curve");

        bottom.Add("panel-title").Text = "Response";
        Curve = bottom.Add<CurveEditor>();

        Add("panel-title").Text = "Diagnostics";
        Diagnostics = Add("utilityset-diagnostics");
    }

    /// <summary>Shows a document.</summary>
    /// <param name="value">The set.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value" /> is null.</exception>
    public void Show(UtilitySetDocument value) {
        ArgumentNullException.ThrowIfNull(value);

        document = value;

        if (!listening) {
            document.Changed += _ => Refresh();
            listening = true;
        }

        action = document.Model.Content.Actions.Count > 0 ? document.Model.Content.Actions[0] : null;
        consideration = action?.Considerations.FirstOrDefault();

        Refresh();
        Compile();
    }

    /// <summary>Selects an action, and the consideration inside it.</summary>
    /// <param name="value">The action, or null.</param>
    /// <param name="axis">Which of its considerations, or null for the first.</param>
    public void Select(UtilityActionContent? value, UtilityConsiderationContent? axis = null) {
        action = value;
        consideration = axis ?? value?.Considerations.FirstOrDefault();

        Refresh();
    }

    /// <summary>Says what an input is reading, which is what the preview scores from.</summary>
    /// <param name="name">The key or registered input's name.</param>
    /// <param name="value">What it reads, in <c>[0,1]</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="name" /> is empty.</exception>
    public void Reading(string name, float value) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        readings[name] = Math.Clamp(value, 0f, 1f);
        Refresh();
    }

    /// <summary>What one input is reading.</summary>
    /// <param name="name">The key or registered input's name.</param>
    /// <returns>The reading, or zero.</returns>
    public float ReadingOf(string name) => readings.GetValueOrDefault(name ?? string.Empty);

    /// <summary>Follows a running agent, so the bars and the readings are its own.</summary>
    /// <param name="value">The debugger's model, or null to go back to the typed readings.</param>
    /// <remarks>
    ///     <para>
    ///         doc 37 § Part 5: <i>"In play mode the bars and the marker are live for the selected
    ///         agent."</i> The authoring half stays exactly as it was — an author types what each
    ///         input reads and the panel does the arithmetic the runtime would — and following an
    ///         agent replaces those numbers with the agent's own.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the chosen action's considerations are live, and that is the snapshot's shape
    ///         rather than an oversight.</b> A capture records every candidate's <i>score</i> and only
    ///         the winner's factors, because scoring every action's every axis for the panel would be
    ///         the cost the decision interval exists to avoid. The bars are all live; the readings
    ///         table is the winner's.
    ///     </para>
    /// </remarks>
    public void Follow(AgentDebugModel? value) {
        live = value;

        Refresh();
    }

    /// <summary>What a named action is scoring on the followed agent, if one is being followed.</summary>
    /// <param name="name">The action's name.</param>
    /// <returns>Its score, or null.</returns>
    public float? LiveScore(string name) {
        if (live is null || live.Snapshot.Planner != AiPlanner.Utility) {
            return null;
        }

        foreach (var row in live.Snapshot.Section(AiDebugSection.Doing)) {
            if (string.Equals(row.Name, name, StringComparison.Ordinal)) {
                return row.Number;
            }
        }

        return null;
    }

    /// <summary>Compiles the set and lists what it said.</summary>
    /// <returns>The set, or null.</returns>
    public UtilitySet? Compile() {
        if (document is null) {
            return null;
        }

        var set = document.Compile();

        Empty(Diagnostics);

        foreach (var diagnostic in document.Diagnostics) {
            var row = Diagnostics.Add("analysis-row");

            row.Add("analysis-stage").Text = diagnostic.Node.IsSome ? diagnostic.Node.ToString() : "set";
            row.Add("analysis-message").Text = diagnostic.Message;
        }

        // Said on success too: a list that empties itself when everything is fine cannot be told apart
        // from one that never ran.
        if (set is not null && document.Diagnostics.Count == 0) {
            var row = Diagnostics.Add("analysis-row");

            row.Add("analysis-stage").Text = "set";
            row.Add("analysis-message").Text = string.Create(
                CultureInfo.InvariantCulture,
                $"{set.Name} compiles: {set.Count} action(s), commitment {set.CommitmentBonus:0.##}, "
                + $"deciding every {set.DecisionInterval:0.##} s."
            );
        }

        return set;
    }

    /// <summary>Rebuilds every panel from the document.</summary>
    public void Refresh() {
        if (document is null) {
            return;
        }

        Absorb();
        RefreshActions();
        RefreshConsiderations();
        RefreshReadings();
        RefreshCurve();
    }

    /// <summary>
    ///     Takes the followed agent's own readings, so the curve preview says where <i>it</i> is
    ///     sitting on the shape rather than where an author guessed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The row's number is the reading and its text is the curved score</b> — see
    ///     <c>AiSnapshots</c>. Writing the curved value in here would put the marker at the answer
    ///     rather than at the question, which is the one thing this panel exists to show.
    /// </remarks>
    void Absorb() {
        if (live is null || live.Snapshot.Planner != AiPlanner.Utility) {
            return;
        }

        var chosen = document!.Model.Content.Actions
            .FirstOrDefault(candidate => string.Equals(candidate.Name, live.Snapshot.Action.ToString(), StringComparison.Ordinal));

        if (chosen is null) {
            return;
        }

        var factors = live.Snapshot.Section(AiDebugSection.Why).ToList();

        for (var index = 0; index < chosen.Considerations.Count && index < factors.Count; index++) {
            var name = UtilitySetModel.Reads(chosen.Considerations[index]);

            if (name.Length > 0) {
                readings[name] = Math.Clamp(factors[index].Number, 0f, 1f);
            }
        }
    }

    void RefreshActions() {
        Empty(Actions);

        foreach (var candidate in document!.Model.Content.Actions) {
            var (previewed, _) = UtilitySetModel.Preview(candidate, readings);
            var score = LiveScore(candidate.Name) ?? previewed;
            var row = Actions.Add(candidate == action ? "utilityset-action selected" : "utilityset-action");

            row.Add("utilityset-name").Text = candidate.Name;
            row.Add("utilityset-task").Text = candidate.Task;

            // The bar is what a two-pane table is for: a column of numbers is a column of numbers, and
            // a column of bars is an answer to "which of these is winning".
            //
            // ⚠ Cells rather than one element of a computed width. A style is computed from the sheet
            // and is not a place a panel writes into, and twenty cells is a bar an author can read a
            // fifth of a point off — which is the precision the numbers beside it are for anyway.
            var bar = row.Add("utilityset-bar");

            for (var cell = 0; cell < Cells(score); cell++) {
                bar.Add("utilityset-cell");
            }

            row.Add("utilityset-score").Text = score.ToString("0.000", CultureInfo.InvariantCulture);
        }
    }

    void RefreshConsiderations() {
        Empty(Considerations);

        if (action is null) {
            return;
        }

        var (_, detail) = UtilitySetModel.Preview(action, readings);

        for (var index = 0; index < action.Considerations.Count; index++) {
            var axis = action.Considerations[index];
            var row = Considerations.Add(
                axis == consideration ? "utilityset-consideration selected" : "utilityset-consideration"
            );

            row.Add("utilityset-name").Text = axis.Name;
            row.Add("utilityset-reads").Text = UtilitySetModel.Reads(axis);
            row.Add("utilityset-curve-kind").Text = axis.Curve.ToString();

            // ⚠ A zero is called out rather than merely being a small number, because a zero is a veto
            // and a veto is the answer to "why is this action never chosen".
            row.Add(detail[index] <= 0f ? "utilityset-veto" : "utilityset-score").Text =
                detail[index].ToString("0.000", CultureInfo.InvariantCulture);
        }
    }

    void RefreshReadings() {
        Empty(Readings);

        if (action is null) {
            return;
        }

        foreach (var name in action.Considerations.Select(UtilitySetModel.Reads).Distinct(StringComparer.Ordinal)) {
            if (name.Length == 0) {
                continue;
            }

            var row = Readings.Add("utilityset-reading");

            row.Add("utilityset-name").Text = name;
            row.Add("utilityset-value").Text = ReadingOf(name).ToString("0.00", CultureInfo.InvariantCulture);
        }
    }

    void RefreshCurve() {
        if (consideration is null) {
            return;
        }

        var shape = consideration.BuildCurve();
        var curve = new AnimationCurve();

        // Sampled at a fixed rate rather than shown as its own keys: five of the six shapes have no
        // keys at all, and one preview that reads every shape the same way is what makes the panel's
        // marker mean the same thing in each of them.
        for (var step = 0; step <= 32; step++) {
            var time = step / 32f;

            curve.Add(time, shape.Evaluate(time));
        }

        Curve.Apply(curve);
    }

    /// <summary>How many of the bar's twenty cells a score fills.</summary>
    /// <param name="score">The score, which may exceed one when a weight does.</param>
    /// <returns>The count.</returns>
    public static int Cells(float score) => (int)MathF.Round(Math.Clamp(score, 0f, 1f) * 20f);

    static void Empty(UiElement element) {
        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }
}

/// <summary>Opens a <c>.vxutility</c>.</summary>
public sealed class UtilitySetEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Utility Set";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [UtilitySetDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new UtilitySetDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<UtilitySetView>();

        view.Show((UtilitySetDocument) document);

        return view;
    }
}
