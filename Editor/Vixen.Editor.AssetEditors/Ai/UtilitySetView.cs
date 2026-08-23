// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Ai;
using Vixen.Editor.Core;
using Vixen.Ui;

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
///     <para>
///         The panel is <c>UtilitySetView.vxml</c>; this file is the accessibility modifier, the three
///         records its lists key on, and the elements that exist only so that markup can write an
///         intrinsic tag's own <c>Text</c>. Same arrangement as <see cref="QueryView" />.
///     </para>
/// </remarks>
public sealed partial class UtilitySetView;

/// <summary>One action's row, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the set's own order.</param>
/// <param name="Class">
///     <c>selected</c> when it is the action whose considerations are shown, and empty otherwise.
/// </param>
/// <param name="Name">What the action is called.</param>
/// <param name="Task">What running it does.</param>
/// <param name="Cells">How many of the bar's twenty cells the score fills.</param>
/// <param name="Score">That score, as the row prints it.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The whole record is the key</b>, which is the immutable-data half of the <c>@for</c>
///         rule: nothing behind this panel is signal-backed, so a changed score has to be a changed
///         identity or the row would go on showing the first one. The slot is in it because two
///         actions may legitimately be authored under the same name while one is being renamed, and
///         <c>BuildContext.For</c> cannot reconcile two equal keys in one loop.
///     </para>
///     <para>
///         ⚠ <b><see cref="Class" /> is a class and was written as part of a tag.</b> The
///         hand-written panel called <c>Add("utilityset-action selected")</c>, and
///         <c>UiElement.Add</c>'s first parameter is the <i>tag</i> — so the element's name was that
///         whole string, space included, and <c>selected</c> was never a class. Nothing in the tree
///         styles either spelling, which is why it went unnoticed; the port writes what the C# meant.
///     </para>
/// </remarks>
internal readonly record struct UtilityActionRow(
    int Slot,
    string Class,
    string Name,
    string Task,
    int Cells,
    string Score
);

/// <summary>One consideration's row.</summary>
/// <param name="Slot">Where it is in the order the axes multiply.</param>
/// <param name="Class"><c>selected</c> when it is the axis whose curve is drawn.</param>
/// <param name="Veto">Whether it scored zero, which is what stops the action being chosen at all.</param>
/// <param name="Name">What the axis is called.</param>
/// <param name="Reads">Which input it reads.</param>
/// <param name="Curve">Which response shape it is put through.</param>
/// <param name="Score">What it contributed.</param>
/// <remarks>
///     ⚠ <b><see cref="Veto" /> is in the key and leaving it out is wave 4's failure.</b> A vetoing
///     axis prints its number in a <i>different tag</i> — <c>utilityset-veto</c> rather than
///     <c>utilityset-score</c> — and a tag is not a class and cannot be bound, so the choice is an
///     <c>@if</c> inside the loop body. An <c>@if</c> inside a surviving region is not re-evaluated,
///     so a key that ignored the flag would leave the first veto marked for ever while every number
///     beside it updated correctly.
/// </remarks>
internal readonly record struct UtilityAxisRow(
    int Slot,
    string Class,
    bool Veto,
    string Name,
    string Reads,
    string Curve,
    string Score
);

/// <summary>One input's reading.</summary>
/// <param name="Slot">Where it is in the order the axes first ask for it.</param>
/// <param name="Name">The key or registered input's name.</param>
/// <param name="Value">What it is reading, as the row prints it.</param>
/// <remarks>
///     The slot is in the key for <see cref="UtilityActionRow" />'s reason. The names are already
///     distinct — <c>RefreshReadings</c> runs them through <c>Distinct</c> — so it is a precaution
///     here rather than the ordinary case, and it costs nothing to keep one rule for all three lists.
/// </remarks>
internal readonly record struct UtilityReadingRow(int Slot, string Name, string Value);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5 and its sanctioned escape; <c>FactName</c> in <c>Captions.cs</c>
///     carries the full argument. <c>PanelTitle</c> is there too, because five panels in this
///     assembly write one.
/// </remarks>
internal sealed class UtilityName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "utilityset-name";
}

/// <inheritdoc cref="UtilityName" />
internal sealed class UtilityTask : UiElement {
    /// <inheritdoc />
    protected override string TagName => "utilityset-task";
}

/// <inheritdoc cref="UtilityName" />
internal sealed class UtilityScore : UiElement {
    /// <inheritdoc />
    protected override string TagName => "utilityset-score";
}

/// <summary>The same number as <see cref="UtilityScore" /> under the tag that says it is a veto.</summary>
/// <inheritdoc cref="UtilityName" />
internal sealed class UtilityVeto : UiElement {
    /// <inheritdoc />
    protected override string TagName => "utilityset-veto";
}

/// <inheritdoc cref="UtilityName" />
internal sealed class UtilityReads : UiElement {
    /// <inheritdoc />
    protected override string TagName => "utilityset-reads";
}

/// <inheritdoc cref="UtilityName" />
internal sealed class UtilityCurveKind : UiElement {
    /// <inheritdoc />
    protected override string TagName => "utilityset-curve-kind";
}

/// <inheritdoc cref="UtilityName" />
internal sealed class UtilityValue : UiElement {
    /// <inheritdoc />
    protected override string TagName => "utilityset-value";
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
