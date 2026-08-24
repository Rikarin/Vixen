// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;

namespace Vixen.Editor.AssetEditors.Animation;

/// <summary>A project's shape vocabulary: the names, the tags, and the body plans.</summary>
/// <remarks>
///     <para>
///         <b>The first file in doc 34's workflow, and the one that had no panel.</b> Everything after
///         it is checked against it, so it being editable only outside Vixen made the first step of
///         the whole module the one step that left the editor.
///     </para>
///     <para>
///         ⚠ <b>One list holding all three kinds rather than three panels.</b> The mistake this file
///         exists to prevent — a class requiring a shape the vocabulary does not declare — is only
///         visible when the terms and the classes are in front of each other. Three tabs would hide
///         exactly the relationship being authored.
///     </para>
///     <para>
///         ⚠ <b>The problems are the vocabulary's own answer, not this panel's.</b>
///         <see cref="ShapeVocabularyContent.Problems" /> is what the importer reports from too — two
///         copies of the rules would be one copy that goes out of step, and the way that shows up is
///         a file the panel calls clean and the build refuses.
///     </para>
///     <para>
///         The panel is <c>ShapeVocabularyView.vxml</c>; this file is the accessibility modifier, the
///         two records its lists key on, and the four elements that exist only so that markup can
///         write an intrinsic tag's own <c>Text</c>. ⚠ <b>The field pane is still built in C#</b> and
///         the markup's header carries the argument: it is a <c>switch</c> over the selection's
///         <i>type</i>, which is wave 4's surviving-region trap with the sharpest edge it has.
///     </para>
/// </remarks>
public sealed partial class ShapeVocabularyView;

/// <summary>One line of the declaration list, as the <c>@for</c> keys it.</summary>
/// <param name="Slot">Where it is in the list, headings counted.</param>
/// <param name="Heading">Whether it is one of the three headings, which have no detail cell.</param>
/// <param name="Class">
///     <c>header</c>, <c>member</c>, <c>missing</c> or a space-joined pair — everything the row wears
///     that is a fact about the row. ⚠ <b><c>selected</c> is deliberately not in here</b>; see below.
/// </param>
/// <param name="Name">What the thing is called.</param>
/// <param name="Detail">What it means, or what is wrong with it. Empty on a heading.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The selection is a binding and not part of the key, which is the opposite of
///         <c>QueryView</c>'s call and is right for a different reason.</b> A selected row here is the
///         same tag with one more class, and a class can be bound where a tag cannot. Keying on the
///         flag would work and would rebuild two rows on every press — and it would also break the
///         hit test, which holds <c>(key, entry)</c> pairs and would find every one of them naming a
///         row that no longer exists.
///     </para>
///     <para>
///         ⚠ <b><see cref="Heading" /> <i>is</i> in the key</b>, because a heading is a shorter row —
///         one cell rather than two — which is an <c>@if</c> inside the loop body, and an <c>@if</c>
///         inside a surviving region is not re-evaluated. It is safe there because it is a fact about
///         what the row is rather than about what is chosen.
///     </para>
///     <para>
///         ⚠ <b>The slot is load-bearing.</b> Two names with the same word and the same meaning are
///         an ordinary thing to have while one of them is being renamed, and
///         <c>BuildContext.For</c> cannot reconcile two equal keys in one loop.
///     </para>
/// </remarks>
internal readonly record struct VocabularyRow(int Slot, bool Heading, string Class, string Name, string Detail);

/// <summary>One thing the vocabulary says is wrong with itself.</summary>
/// <param name="Slot">Where it is in the order the check reports them.</param>
/// <param name="Class"><c>fatal</c> for one that would fail a build, and empty for a warning.</param>
/// <param name="Message">What it says.</param>
internal readonly record struct VocabularyProblemRow(int Slot, string Class, string Message);

/// <summary>
///     ⚠ The elements that exist only so that markup can set an intrinsic tag's own <c>Text</c>.
/// </summary>
/// <remarks>
///     The panel ledger's shape 5 and its sanctioned escape; <c>FactName</c> in <c>Captions.cs</c>
///     carries the full argument.
/// </remarks>
internal sealed class VocabName : UiElement {
    /// <inheritdoc />
    protected override string TagName => "vocab-name";
}

/// <inheritdoc cref="VocabName" />
internal sealed class VocabDetail : UiElement {
    /// <inheritdoc />
    protected override string TagName => "vocab-detail";
}

/// <inheritdoc cref="VocabName" />
internal sealed class VocabTitle : UiElement {
    /// <inheritdoc />
    protected override string TagName => "vocab-title";
}

/// <inheritdoc cref="VocabName" />
internal sealed class VocabNote : UiElement {
    /// <inheritdoc />
    protected override string TagName => "vocab-note";
}

/// <summary>Opens a project's shape vocabulary.</summary>
public sealed class ShapeVocabularyEditorFactory : IAssetEditorFactory {
    /// <inheritdoc />
    public string Name => "Shape Vocabulary";

    /// <inheritdoc />
    public IReadOnlyList<string> Extensions { get; } = [ShapeVocabularyDocument.Extension];

    /// <inheritdoc />
    public EditorDocument Open(AssetEditorRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return new ShapeVocabularyDocument(request.Project, request.Asset, request.Path);
    }

    /// <inheritdoc />
    public UiElement CreateView(EditorDocument document, UiElement panel) {
        ArgumentNullException.ThrowIfNull(panel);

        var view = panel.Add<ShapeVocabularyView>();
        view.Show((ShapeVocabularyDocument) document);

        return view;
    }
}
