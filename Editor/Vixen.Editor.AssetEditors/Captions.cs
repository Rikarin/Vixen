// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.AssetEditors;

/// <summary>The two cells a fact row is made of, as elements a <c>.vxml</c> can write the text of.</summary>
/// <remarks>
///     <para>
///         <b>The panel ledger's shape 5, and the sanctioned escape from it.</b> An interpolation is
///         <c>BuildContext.Text</c>, which appends a <c>text</c> <i>child</i>; an attribute on a
///         lowercase tag is <c>BuildContext.Attribute</c>, which is a selector attribute and not
///         <see cref="UiElement.Text" />. So <c>row.Add("fact-name").Text = label</c> has no markup
///         spelling — but a <i>capitalised</i> tag is a real property assignment, and <c>Text</c> is a
///         <c>[UiProperty]</c> on every element. A four-line subclass answering to the tag the
///         stylesheet already names is the whole fix, and it moves nothing: same tag, same position,
///         same own text.
///     </para>
///     <para>
///         ⚠ <b>Shared, and it was right not to be until now.</b> Wave 3 declared <c>FactName</c> in
///         <c>Vixen.Editor.AssetEditors.Audio</c> and wave 4 declared both in
///         <c>…​.Animation</c>, and the ledger argued against hoisting on the grounds that a shared
///         declaration "would buy a file and move nothing". That was true at two callers. Wave 5's
///         <c>CompiledSceneView</c> and <c>TextureImportView</c> are the third and fourth, and four
///         copies of a tag name is how two of them end up disagreeing about it — so the declaration is
///         here, in the assembly's own namespace, which every one of the four resolves without a
///         <c>@using</c> because C# sees an enclosing namespace.
///     </para>
///     <para>
///         ⚠ <b>Not in <c>Vixen.Editor.Ui</c>'s <c>Parts/FactRow.vxml</c>, and the reason is the
///         reference graph rather than taste.</b> These four panels are in
///         <c>Vixen.Editor.AssetEditors</c>, which does not reference <c>Vixen.Editor.Ui</c> and should
///         not start to for a row. The rules for all three tags live in <c>AssetEditorTheme.vcss</c>
///         either way — the tag names are the contract.
///     </para>
///     <para>
///         ⚠ <b>And these are cells rather than a row, which is why they are types and not a part.</b>
///         A <c>.vxml</c> part is worth a file when it has a shape; <c>FactRow</c> is four elements and
///         two cells that disagree about where the text goes. A caption has none.
///     </para>
/// </remarks>
internal sealed class FactName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "fact-name";
}

/// <inheritdoc cref="FactName" />
internal sealed class FactValue : UiElement {
    /// <inheritdoc />
    protected override string TagName => "fact-value";
}

/// <summary>The two cells a diagnostics row is made of.</summary>
/// <remarks>
///     <para>
///         <see cref="FactName" />'s reason, for the other triple this assembly repeats.
///         <c>analysis-row</c>/<c>analysis-stage</c>/<c>analysis-message</c> is built by hand in nine
///         panels here; <c>AnalysisRow.vxml</c> is the row, and these are the cells it writes the
///         text of.
///     </para>
///     <para>
///         ⚠ <b>Hoisted out of <c>AudioMixerView.cs</c>, which is where wave 3 declared them and said
///         they should move.</b> Its note read "hoisting it into a part is a job for whoever ports the
///         second of them, because doing it here would move pixels in a panel this change has not
///         measured" — wave 6 is that porter, and the move is byte-neutral because it is a namespace
///         change and not a tag change.
///     </para>
/// </remarks>
internal sealed class AnalysisStage : UiElement {
    /// <inheritdoc />
    protected override string TagName => "analysis-stage";
}

/// <inheritdoc cref="AnalysisStage" />
internal sealed class AnalysisMessage : UiElement {
    /// <inheritdoc />
    protected override string TagName => "analysis-message";
}

/// <summary>One line of what a compiler said, as an <c>AnalysisRow</c>'s <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the order the compiler said things.</param>
/// <param name="Stage">Which part of the pipeline spoke, or which node.</param>
/// <param name="Message">What it said.</param>
/// <remarks>
///     <para>
///         Shared for <see cref="AnalysisStage" />'s reason: nine panels here build the same row, and
///         each of them would otherwise declare the same three fields under a different name.
///     </para>
///     <para>
///         ⚠ <b>The whole record is the key and the slot is load-bearing.</b> A compiler that reports
///         every problem before failing once — which is what these lists exist to show — can say the
///         same sentence about two nodes, and <c>BuildContext.For</c> cannot reconcile two equal keys
///         in one loop.
///     </para>
/// </remarks>
internal readonly record struct AnalysisNote(int Slot, string Stage, string Message);

/// <summary>A section heading inside a panel, as an element a <c>.vxml</c> can write the text of.</summary>
/// <remarks>
///     <para>
///         <see cref="FactName" />'s reason again, for the caption this assembly repeats most:
///         <c>panel.Add("panel-title").Text = "Diagnostics"</c> is written twenty-three times across
///         the AI, behaviour-tree and utility editors.
///     </para>
///     <para>
///         ⚠ <b>A tag and not a class, which is what the call sites already say.</b>
///         <c>EditorTheme</c> has one <c>panel-title</c> rule and it is
///         <c>viewport-panel &gt; .panel-title</c> — a <i>class</i> selector, reached only from
///         <c>ViewportChrome</c>'s <c>AddClass</c>. Every panel in this assembly writes the tag
///         instead, which nothing styles; that asymmetry is preserved rather than tidied, because
///         giving these headings the class would restyle five panels in a commit about markup.
///     </para>
/// </remarks>
internal sealed class PanelTitle : UiElement {
    /// <inheritdoc />
    protected override string TagName => "panel-title";
}
