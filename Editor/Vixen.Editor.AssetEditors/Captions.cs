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
