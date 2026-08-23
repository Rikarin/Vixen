// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Editor.Core;
using Vixen.Ui;

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
///         The panel is <c>VariationHarnessView.vxml</c>; this file is the accessibility modifier, the
///         key the grid's <c>@for</c> reconciles a cell on, and the handful of elements that exist
///         only so that markup can write an intrinsic tag's own <c>Text</c>. Same arrangement as
///         <c>AudioMixerView</c>.
///     </para>
///     <para>
///         <b>The report is the deliverable, and the drill-down is what makes it a tool.</b> A grid of
///         numbers saying a clip is wrong somewhere is not actionable. Selecting a cell raises
///         <c>CellSelected</c> with the configuration rebuilt and the moment attached, so the
///         application can put that body, that prop and that frame on screen.
///     </para>
///     <para>
///         ⚠ <b>A goal that never resolved is drawn differently from one that missed</b>, rather than
///         as a zero. It is the most common way a variation actually breaks and it sorts to the top.
///     </para>
///     <para>
///         ⚠ <b>The panel runs a plan and does not own the running.</b> Pressing Run asks
///         <see cref="HarnessDocument.TryRun" />, which is synchronous and answers why it could not
///         start rather than throwing — a plan whose clip has not been imported yet is an ordinary
///         state. A host that wants a run on a background task, with progress and a cancellation,
///         calls <see cref="VariationHarness" /> itself and uses <c>Show(HarnessPlan, VariationReport)</c>
///         to show the answer.
///     </para>
/// </remarks>
public sealed partial class VariationHarnessView;

/// <summary>One cell of the grid, as the <c>@for</c> keys it.</summary>
/// <param name="Variation">Its row: an index into the report's configurations.</param>
/// <param name="Goal">Its column: an index into the report's goals.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The two indices and emphatically not the <see cref="HarnessCell" />.</b> A key
///         carrying the measurement would be a different key the moment a re-run moved a residual by
///         a millimetre, and every surviving cell's element would be torn down and rebuilt — the one
///         under the pointer included. A cell's <i>position</i> in the matrix is what makes it the
///         same cell across runs, and everything that changes about it reaches the screen through a
///         binding inside the body, which is what a surviving key leaves running.
///     </para>
///     <para>
///         ⚠ <b>And two indices rather than the goal's name, because a name is not unique.</b> Two
///         constraints in one clip may both be called "right hand"; <c>BuildContext.For</c> cannot
///         reconcile two equal keys in one loop, and the panel this replaces drew both columns.
///     </para>
///     <para>
///         ⚠ <b>Globally unique across the nest, not merely within a row.</b> The inner loop is a
///         fresh <c>For</c> per row and could have keyed on the column alone — but
///         <c>VariationHarnessView.Cells</c> is <i>one</i> <see cref="Vixen.Ui.Composition.ElementRefs{TElement}" />
///         shared by every row, and a handle is filed under the loop's own key. A bare column index
///         would file every row's cells under the same few keys and hand back whichever row was
///         built last.
///     </para>
/// </remarks>
internal readonly record struct HarnessSlot(int Variation, int Goal);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the panel ledger's shape 5, and these are the sanctioned escape from it.</b> An
///         interpolation is <c>BuildContext.Text</c>, which appends a <c>text</c> <i>child</i>; an
///         attribute on a lowercase tag is <c>BuildContext.Attribute</c>, which is a selector
///         attribute and not <see cref="UiElement.Text" />. So <c>row.Add("fact-name").Text = label</c>
///         has no markup spelling — but a <i>capitalised</i> tag is a real property assignment, and
///         <c>Text</c> is a <c>[UiProperty]</c> on every element. A four-line subclass that answers to
///         the tag the stylesheet already names is therefore the whole fix, and it moves nothing:
///         same tag, same position, same own text.
///     </para>
///     <para>
///         ⚠ <b>Seven of them, and every one is a caption rather than a row.</b> A <c>.vxml</c> part
///         is worth a file when it has a shape — <c>FactRow</c> is four elements and two cells that
///         disagree about where the text goes — and these have none.
///     </para>
///     <para>
///         ⚠ <b><see cref="FactName" /> and <see cref="FactValue" /> are this assembly's second pair
///         with those tags</b>, after <c>AudioMixerView</c>'s. They are not hoisted into something
///         shared here for the reason that block gives: <c>fact-row</c>'s rules live in
///         <c>AssetEditorTheme.vcss</c> and both panels already agree with them, so a shared
///         declaration would buy a file and move nothing — and the four callers that could actually
///         use <c>Vixen.Editor.Ui</c>'s <c>FactRow</c> part are in a different assembly. See the
///         panel ledger's note on the reference graph.
///     </para>
/// </remarks>
internal sealed class HarnessVerdict : UiElement {
    /// <inheritdoc />
    protected override string TagName => "harness-verdict";
}

/// <inheritdoc cref="HarnessVerdict" />
internal sealed class HarnessLabel : UiElement {
    /// <inheritdoc />
    protected override string TagName => "harness-label";
}

/// <inheritdoc cref="HarnessVerdict" />
internal sealed class HarnessGridCell : UiElement {
    /// <inheritdoc />
    protected override string TagName => "harness-cell";
}

/// <inheritdoc cref="HarnessVerdict" />
internal sealed class HarnessTitle : UiElement {
    /// <inheritdoc />
    protected override string TagName => "harness-title";
}

/// <inheritdoc cref="HarnessVerdict" />
internal sealed class HarnessError : UiElement {
    /// <inheritdoc />
    protected override string TagName => "harness-error";
}

/// <inheritdoc cref="HarnessVerdict" />
internal sealed class FactName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "fact-name";
}

/// <inheritdoc cref="HarnessVerdict" />
internal sealed class FactValue : UiElement {
    /// <inheritdoc />
    protected override string TagName => "fact-value";
}

/// <summary>Opens a declared variation run.</summary>
public sealed class HarnessEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Variation Harness";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [HarnessDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new HarnessDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<VariationHarnessView>();
        view.Show((HarnessDocument) document);

        return view;
    }
}
