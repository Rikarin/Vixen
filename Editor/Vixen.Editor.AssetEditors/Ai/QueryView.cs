// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Ai;

/// <summary>
///     An environment query, open for editing: the generators, the tests in order, a curve, and the
///     points a preview run produced.
/// </summary>
/// <remarks>
///     <para>
///         doc 37 § Part 5's environment-query editor. A list — generators, then tests in order — with
///         each test's purpose, curve and clamping inline, and a preview of the generated points
///         green through red by score with the filtered ones crossed out.
///     </para>
///     <para>
///         The panel is <c>QueryView.vxml</c>; this file is the accessibility modifier, the four
///         records its lists key on, and the elements that exist only so that markup can write an
///         intrinsic tag's own <c>Text</c>. Same arrangement as <c>CompiledSceneView</c>.
///     </para>
///     <para>
///         ⚠ <b>The test list is ordered and the order is editable, which is the one gesture this
///         editor has that the utility table does not.</b> A filtering test rejects a point and
///         everything below it is skipped, so moving a trace below a distance filter is the
///         difference between four hundred raycasts and forty — a real cost, made by a real gesture,
///         with the count of surviving points beside it saying what it bought.
///     </para>
///     <para>
///         ⚠ <b>The curve control is the utility editor's.</b> doc 37 § D14: a test's scoring equation
///         <i>is</i> a consideration's response curve, so the same
///         <see cref="Vixen.Ui.Controls.Advanced.CurveEditor" /> draws both and an author who has
///         tuned one has tuned the other. It stays behind a <c>ref</c> — the panel ledger's shape 1,
///         and the ledger said so before the port started.
///     </para>
/// </remarks>
public sealed partial class QueryView;

/// <summary>One generator's row, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the document's generator list.</param>
/// <param name="Kind">Grid, circle, donut, cone or the agent's own feet.</param>
/// <param name="Detail">What it makes, as a person reads it.</param>
/// <remarks>
///     ⚠ <b>The whole record is the key</b>, which is the immutable-data half of the <c>@for</c>
///     rule: nothing here is signal-backed, so a changed extent has to be a changed identity or the
///     row would go on showing the first one. The slot is in it because two generators of the same
///     kind and the same size are an ordinary thing to author, and <c>BuildContext.For</c> cannot
///     reconcile two equal keys in one loop.
/// </remarks>
internal readonly record struct QueryGeneratorRow(int Slot, string Kind, string Detail);

/// <summary>One test's row.</summary>
/// <param name="Slot">Where it is in the order the tests run.</param>
/// <param name="Selected">Whether it is the one whose curve is drawn.</param>
/// <param name="Order">That slot, as the row prints it.</param>
/// <param name="Kind">Which test, or the registered name it was authored under.</param>
/// <param name="Purpose">Filter, score, or both.</param>
/// <param name="Detail">Its range and curve, as a person reads it.</param>
/// <remarks>
///     ⚠ <b><see cref="Selected" /> is in the key, and leaving it out is the failure wave 4
///     recorded.</b> A selected test is a <i>different tag</i> — <c>query-row-selected</c> — and a
///     tag is not a class and cannot be bound, so the choice is an <c>@if</c> inside the loop body.
///     An <c>@if</c> inside a surviving region is not re-evaluated, so a key that ignored the flag
///     would leave the first selection highlighted for ever while the list looked entirely correct.
///     With the flag in the key, moving the selection changes exactly two keys and rebuilds exactly
///     two rows.
/// </remarks>
internal readonly record struct QueryTestRow(
    int Slot,
    bool Selected,
    string Order,
    string Kind,
    string Purpose,
    string Detail
);

/// <summary>One previewed point's row.</summary>
/// <param name="Slot">Where it is in the run's own order.</param>
/// <param name="Filtered">Whether a filtering test rejected it.</param>
/// <param name="Best">Whether it is the one the query would return.</param>
/// <param name="Position">Where it is, in metres.</param>
/// <param name="Score">What it scored, or that it was filtered.</param>
/// <param name="Factors">What each test contributed, or empty when the run kept no detail.</param>
/// <remarks>
///     ⚠ <b>The slot is load-bearing here and not a precaution.</b> A grid preview is eighty-one
///     points and a filtered one prints "filtered" with the same factors as its neighbour, so equal
///     rows are the ordinary case rather than the hypothetical one.
/// </remarks>
internal readonly record struct QueryPointRow(
    int Slot,
    bool Filtered,
    bool Best,
    string Position,
    string Score,
    string Factors
);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5 and its sanctioned escape; <see cref="FactName" /> carries the
///     full argument. <c>PanelTitle</c> is not here but in <c>Captions.cs</c>, because five panels in
///     this assembly write one.
/// </remarks>
internal sealed class QueryOrder : UiElement {
    /// <inheritdoc />
    protected override string TagName => "query-order";
}

/// <inheritdoc cref="QueryOrder" />
internal sealed class QueryKind : UiElement {
    /// <inheritdoc />
    protected override string TagName => "query-kind";
}

/// <inheritdoc cref="QueryOrder" />
internal sealed class QueryPurpose : UiElement {
    /// <inheritdoc />
    protected override string TagName => "query-purpose";
}

/// <summary>
///     A <c>query-detail</c> with its own text — the same tag the right-hand pane is, which is what
///     the hand-written panel wrote too.
/// </summary>
/// <inheritdoc cref="QueryOrder" />
internal sealed class QueryDetail : UiElement {
    /// <inheritdoc />
    protected override string TagName => "query-detail";
}

/// <inheritdoc cref="QueryOrder" />
internal sealed class QueryPosition : UiElement {
    /// <inheritdoc />
    protected override string TagName => "query-position";
}

/// <inheritdoc cref="QueryOrder" />
internal sealed class QueryScore : UiElement {
    /// <inheritdoc />
    protected override string TagName => "query-score";
}

/// <inheritdoc cref="QueryOrder" />
internal sealed class QueryFactors : UiElement {
    /// <inheritdoc />
    protected override string TagName => "query-factors";
}

/// <summary>Opens a <c>.vxquery</c>.</summary>
public sealed class QueryEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Environment Query";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [QueryDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new QueryDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<QueryView>();

        view.Show((QueryDocument)document);

        return view;
    }
}
