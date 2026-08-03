// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Animation.Constraints;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>Where the report says to look, and what the editor should do about it.</summary>
/// <param name="Cell">The cell.</param>
/// <param name="Case">The configuration it was measured in.</param>
/// <param name="Subject">That configuration rebuilt: the body, the shapes, the props and the ground.</param>
/// <remarks>
///     ⚠ <b>The subject, not only the indices.</b> A drill-down that handed back "row four" would make
///     every consumer re-derive what row four was, and the harness already knows.
/// </remarks>
public sealed record HarnessSelection(HarnessCell Cell, HarnessCase Case, HarnessSubject Subject);

/// <summary>The variation matrix: every configuration against every goal, worst cell first.</summary>
/// <remarks>
///     <para>
///         <b>The report is the deliverable, and the drill-down is what makes it a tool.</b> A grid of
///         numbers saying a clip is wrong somewhere is not actionable. Selecting a cell raises
///         <see cref="CellSelected" /> with the configuration rebuilt and the moment attached, so the
///         application can put that body, that prop and that frame on screen.
///     </para>
///     <para>
///         ⚠ <b>A goal that never resolved is drawn differently from one that missed</b>, rather than
///         as a zero. It is the most common way a variation actually breaks and it sorts to the top.
///     </para>
///     <para>
///         ⚠ <b>This panel does not run anything.</b> A run is seconds of solving and the panel would
///         be the wrong place to own that — the host runs <see cref="VariationHarness" /> where it
///         likes, on a background task or in a build, and hands the report over.
///     </para>
/// </remarks>
public sealed class VariationHarnessView : Control {
    readonly List<(UiElement Row, HarnessCell Cell)> cells = [];

    HarnessPlan? plan;
    VariationReport? report;
    HarnessCell? selected;

    /// <inheritdoc />
    protected override string TagName => "harness-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The bar across the top.</summary>
    public UiElement Bar { get; private set; } = null!;

    /// <summary>What the run was and whether it passed.</summary>
    public UiElement Verdict { get; private set; } = null!;

    /// <summary>Jumps to the worst cell without hunting for it.</summary>
    public Button Worst { get; private set; } = null!;

    /// <summary>The grid.</summary>
    public UiElement Matrix { get; private set; } = null!;

    /// <summary>What the selected cell is.</summary>
    public UiElement Fields { get; private set; } = null!;

    /// <summary>Which cell is selected, or <see langword="null" />.</summary>
    public HarnessCell? Selected => selected;

    /// <summary>Raised when a cell is chosen, with everything needed to show it.</summary>
    public event Action<VariationHarnessView, HarnessSelection>? CellSelected;

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Bar = Part("harness-bar");
        Verdict = Bar.Add("harness-verdict");

        Worst = Bar.Add<Button>();
        Worst.Label = "Worst Cell";

        var body = Part("harness-body");

        Matrix = body.Add("harness-matrix");

        var side = body.Add("harness-side");
        Fields = side.Add("harness-fields");

        AddHandler<ClickEvent>(static (element, args) => ((VariationHarnessView) element).Chosen(args));

        // ⚠ A cell is a plain element and plain elements raise no click. The button above does,
        // because it is a control; a grid of seventy cells is deliberately not seventy controls, so
        // the press is caught here and turned into a selection by hit test.
        AddHandler<PointerEvent>(static (element, args) => ((VariationHarnessView) element).Pressed(args));
    }

    /// <summary>Shows a report.</summary>
    /// <param name="ran">The plan it came from, which is what a drill-down rebuilds against.</param>
    /// <param name="result">The report.</param>
    public void Show(HarnessPlan ran, VariationReport result) {
        ArgumentNullException.ThrowIfNull(ran);
        ArgumentNullException.ThrowIfNull(result);

        plan = ran;
        report = result;
        selected = null;

        Reload();
    }

    /// <summary>Rebuilds the grid from the report.</summary>
    public void Reload() {
        Matrix.Empty();
        cells.Clear();

        if (report is not { } result || plan is not { } ran) {
            Verdict.Text = "No run yet.";
            Restate();

            return;
        }

        var judged = result.Judge(ran.Thresholds);

        Verdict.Text = judged.Passed
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"Passed — {result.Cases.Count} configuration(s) × {result.Goals.Count} goal(s)"
            )
            : string.Create(CultureInfo.InvariantCulture, $"Failed — {judged.Failed.Count} cell(s)");

        Verdict.SetClass("failed", !judged.Passed);

        // The header, so the columns are readable without counting across.
        var header = Matrix.Add("harness-row");
        header.AddClass("header");
        header.Add("harness-label").Text = string.Empty;

        foreach (var goal in result.Goals) {
            header.Add("harness-cell").Text = goal;
        }

        for (var variation = 0; variation < result.Cases.Count; variation++) {
            var row = Matrix.Add("harness-row");

            row.Add("harness-label").Text = result.Cases[variation].Label;

            for (var goal = 0; goal < result.Goals.Count; goal++) {
                var cell = result[variation, goal];
                var element = row.Add("harness-cell");

                element.Text = Say(cell);

                // ⚠ Three states and not two. "Never resolved" is not a large error and not a small
                // one — it is a different thing, and an author who reads it as a number will spend
                // the afternoon adjusting a goal that is not running.
                element.SetClass("missing", !cell.Ran);
                element.SetClass("failed", cell.Ran && cell.Fails(ran.Thresholds));

                cells.Add((element, cell));
            }
        }

        Restate();
    }

    static string Say(in HarnessCell cell) =>
        cell.Ran
            ? string.Create(CultureInfo.InvariantCulture, $"{cell.Residual * 100f:0.#}")
            : "—";

    /// <summary>Selects a cell and tells whoever is listening.</summary>
    /// <param name="cell">The cell.</param>
    public void Select(in HarnessCell cell) {
        if (report is not { } result || plan is not { } ran) {
            return;
        }

        selected = cell;

        var where = result.Cases[cell.Variation];

        Restate();
        CellSelected?.Invoke(this, new(cell, where, VariationHarness.Rebuild(ran, where)));
    }

    void Restate() {
        Fields.Empty();

        foreach (var (element, cell) in cells) {
            element.SetClass("selected", selected is { } chosen && chosen == cell);
        }

        if (report is not { } result || selected is not { } shown) {
            Fields.Add("text").Text = "Select a cell.";
            return;
        }

        Fields.Add("harness-title").Text = shown.Goal;
        Line("Configuration", result.Cases[shown.Variation].Label);

        if (!shown.Ran) {
            Fields.Add("harness-error").Text =
                "This goal never resolved in this configuration. Its frame did not answer — a shape it names is "
                + "missing on this body, or a slot it names is unbound. It is not a goal that is nearly right.";

            return;
        }

        Line("Worst miss", Metres(shown.Residual));
        Line("At", string.Create(CultureInfo.InvariantCulture, $"{shown.At:0.###} of the clip"));
        Line("Sank in", Metres(shown.Penetration));
        Line("Velocity jump", string.Create(CultureInfo.InvariantCulture, $"{shown.Jerk:0.#} m/s²"));

        if (shown.Reached) {
            Fields.Add("harness-error").Text =
                "The chain was straight and still short. This is a reach problem rather than a tuning one: the "
                + "contact is somewhere this body cannot get to, and easing it will not help.";
        }
    }

    static string Metres(float value) => string.Create(CultureInfo.InvariantCulture, $"{value * 100f:0.##} cm");

    void Line(string label, string value) {
        var row = Fields.Add("fact-row");

        row.Add("fact-name").Text = label;
        row.Add("fact-value").Text = value;
    }

    void Chosen(ClickEvent args) {
        for (var element = args.Source; element is not null; element = element.Parent) {
            if (ReferenceEquals(element, Worst)) {
                if (report?.Worst(plan?.Thresholds ?? default) is { } worst) {
                    Select(worst);
                }

                args.Handled = true;
                return;
            }
        }
    }

    void Pressed(PointerEvent args) {
        if (args.Action != PointerAction.Pressed || args.Button != PointerButton.Primary) {
            return;
        }

        foreach (var (candidate, cell) in cells) {
            if (candidate.Bounds.Contains(new Vector2(args.X, args.Y))) {
                Select(cell);
                args.Handled = true;

                return;
            }
        }
    }
}
