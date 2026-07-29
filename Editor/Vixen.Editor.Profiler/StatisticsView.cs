// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Profiler;

/// <summary>What is in the scene, against what somebody said should be.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every row carries its budget as a bar rather than only as a number.</b> "48 233
///         entities" says nothing on its own; "48 233 of 100 000" is a fact somebody can act on, and
///         a bar is how a person reads that at a glance while they are doing something else.
///     </para>
///     <para>
///         ⚠ <b>Warnings are listed rather than only coloured.</b> A row tinted amber is invisible to
///         anyone who is not looking at that row, and the whole value of a statistics panel is that
///         it is noticed by somebody who opened it for another reason.
///     </para>
/// </remarks>
public sealed partial class StatisticsView : Control {
    readonly List<UiElement> rows = [];
    readonly List<UiElement> warnings = [];

    /// <inheritdoc />
    protected override string TagName => "statistics-view";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The strip along the top.</summary>
    public UiElement Toolbar { get; private set; } = null!;

    /// <summary>What takes a fresh count.</summary>
    public Button Refresh { get; private set; } = null!;

    /// <summary>Where the counted things go.</summary>
    public UiElement Body { get; private set; } = null!;

    /// <summary>Where the warnings go.</summary>
    public UiElement Warnings { get; private set; } = null!;

    /// <summary>What produces a fresh count, or <see langword="null" /> when nothing can.</summary>
    /// <remarks>
    ///     A delegate rather than a world, because which world is the editor's arbitration — the
    ///     scene being edited, a prefab open in isolation, or a running game's — and a panel holding
    ///     one would be showing whichever was assigned last.
    /// </remarks>
    public Func<SceneStatistics>? Source { get; set; }

    /// <summary>The count on screen.</summary>
    public SceneStatistics? Statistics { get; private set; }

    /// <summary>Takes a fresh count, if there is anything to count.</summary>
    public void Take() {
        if (Source?.Invoke() is { } counted) {
            Show(counted);
        }
    }

    /// <summary>Shows a count somebody else took.</summary>
    /// <param name="statistics">The count.</param>
    /// <exception cref="ArgumentNullException"><paramref name="statistics" /> is null.</exception>
    public void Show(SceneStatistics statistics) {
        ArgumentNullException.ThrowIfNull(statistics);

        Statistics = statistics;

        for (var index = 0; index < statistics.Rows.Count; index++) {
            var row = statistics.Rows[index];

            while (rows.Count <= index) {
                var built = Body.Add<UiElement>("statistic-row");

                built.Add<UiElement>("statistic-label");
                built.Add<UiElement>("statistic-value");

                var track = built.Add<UiElement>("statistic-track");
                track.Add<UiElement>("statistic-fill");

                built.Add<UiElement>("statistic-detail");
                rows.Add(built);
            }

            var element = rows[index];

            element.RemoveClass("parked");
            element.RemoveClass("near");
            element.RemoveClass("over");

            switch (row.State) {
                case BudgetState.Over:
                    element.AddClass("over");
                    break;

                case BudgetState.Near:
                    element.AddClass("near");
                    break;

                default:
                    break;
            }

            element.Children[0].Text = row.Label;
            element.Children[1].Text = Value(row);

            // A track with no fill in it is what "no budget" looks like, rather than a full bar or
            // an empty one — both of which read as a measurement.
            element.Children[2].SetStyle("display", row.Budget is > 0 ? "flex" : "none");
            element.Children[2].Children[0].SetStyle("width", Percent(row.Fill));
            element.Children[3].Text = row.Detail;
        }

        for (var index = statistics.Rows.Count; index < rows.Count; index++) {
            rows[index].AddClass("parked");
        }

        for (var index = 0; index < statistics.Warnings.Count; index++) {
            while (warnings.Count <= index) {
                warnings.Add(Warnings.Add<UiElement>("statistic-warning"));
            }

            warnings[index].RemoveClass("parked");
            warnings[index].Text = statistics.Warnings[index];
        }

        for (var index = statistics.Warnings.Count; index < warnings.Count; index++) {
            warnings[index].AddClass("parked");
        }

        Warnings.SetStyle("display", statistics.HasWarnings ? "flex" : "none");
    }

    /// <inheritdoc />
    protected override void OnCreated() {
        base.OnCreated();

        Toolbar = Part("statistics-toolbar");

        Refresh = Toolbar.Add<Button>();
        Refresh.Size = ControlSize.Small;
        Refresh.Label = "Refresh";
        Refresh.Clicked += _ => Take();

        Warnings = Part("statistics-warnings");
        Warnings.SetStyle("display", "none");

        Body = Part("statistics-body");
    }

    /// <summary>
    ///     A count against its budget, or the count alone — and a byte-shaped row in binary units.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Byte rows are told apart by their label rather than by a flag on the row.</b> That is
    ///     a smaller lie than the alternative: a <c>StatisticRow</c> with a unit field would mean
    ///     every caller choosing one, for a model where exactly one row in six is bytes.
    /// </remarks>
    static string Value(StatisticRow row) {
        var value = row.Label.Contains("memory", StringComparison.OrdinalIgnoreCase)
            ? MemoryView.Bytes(row.Value)
            : row.Value.ToString("N0", CultureInfo.InvariantCulture);

        if (row.Budget is not { } ceiling || ceiling <= 0) {
            return value;
        }

        var limit = row.Label.Contains("memory", StringComparison.OrdinalIgnoreCase)
            ? MemoryView.Bytes(ceiling)
            : ceiling.ToString("N0", CultureInfo.InvariantCulture);

        return value + " / " + limit;
    }

    static string Percent(float fill) =>
        (fill * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%";
}
