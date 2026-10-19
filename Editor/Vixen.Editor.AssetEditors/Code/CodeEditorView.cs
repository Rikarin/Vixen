// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Editor.Ui.Documents;
using Vixen.Ui;
using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Markup.Binding;

namespace Vixen.Editor.AssetEditors.Code;

/// <summary>A code editor over a document, with its diagnostics in the gutter.</summary>
/// <remarks>
///     <para>
///         <b>The pane, not the editor.</b> <see cref="CodeEditor" /> is the control — lines,
///         folding, completion, a caret — and this is what joins one to a
///         <see cref="CodeDocument" />: the buffer, the tokenizer, and putting the analysis back on
///         it after it runs.
///     </para>
///     <para>
///         ⚠ <b>Analysis is driven from here and is not automatic.</b> <see cref="Analyse" /> is
///         called by whatever decides when a pause has happened — the host's frame loop, a timer, a
///         Save — because parsing per keystroke is a parse per keystroke and a document that did it
///         for you could not be talked out of it.
///     </para>
///     <para>
///         The pane is <c>CodeEditorView.vxml</c>; this file is the accessibility modifier and
///         the two factories, on the arrangement <c>FactRow</c> and <c>NodeInspector</c> use. The
///         emitter's partial carries no modifier, and this type is <c>public</c> because
///         <c>PreviewCodeEditorView</c> derives from it and because the shader editor's factory
///         hands one out.
///     </para>
/// </remarks>
public partial class CodeEditorView;

/// <summary>A code editor with a pane beside it showing what the file describes.</summary>
/// <remarks>
///     <para>
///         Doc 11's "<c>CodeEditor</c> + live preview pane", for the two file types that have
///         something to show. The two halves are genuinely different — see
///         <see cref="MarkupDocument" /> and <see cref="StyleSheetDocument" /> for why a stylesheet's
///         preview is the real cascade and a component's is its static structure — so the pane says
///         which of the two it is drawing rather than letting them look the same.
///     </para>
///     <para>
///         ⚠ <b>The preview has its own document, and it must.</b> Loading the file's stylesheet into
///         the editor's own <see cref="UiDocument" /> would restyle the editor with the user's game
///         CSS — an author testing a rule that hides everything would hide the editor. A nested
///         document is not something <c>Vixen.Ui</c> supports, so the preview is a subtree with the
///         sheet loaded at author origin and every selector scoped under the pane's own class, which
///         is the containment this can actually offer today. A rule with <c>!important</c> at
///         user-agent origin still escapes it; that is a real hole and the fix is a second
///         <c>UiDocument</c> rendered into a texture.
///     </para>
///     <para>
///         The pane is <c>PreviewCodeEditorView.vxml</c>; this file is the accessibility
///         modifier. ⚠ It is the first <c>.vxml</c> in the tree that derives from another, and
///         the arrangement costs nothing: the emitter's element scaffold builds in
///         <c>OnCreated</c> and calls <c>base.OnCreated()</c> first, so the base's
///         <c>code-editor</c> is child nought and this one's pane follows it.
///     </para>
/// </remarks>
public sealed partial class PreviewCodeEditorView;

/// <summary>Opens a Raven shader.</summary>
public sealed class ShaderEditorFactory : DocumentKind {
    /// <inheritdoc />
    public override string Name => "Shader";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } = [ShaderDocument.Extension];

    /// <inheritdoc />
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(ShaderDocument)];

    /// <inheritdoc />
    public override EditorDocument Open(DocumentRequest request) {
        ArgumentNullException.ThrowIfNull(request);
        return new ShaderDocument(request.Project, request.Asset, request.Path);
    }
}

/// <summary>What <see cref="ShaderEditorFactory" />'s documents are shown in: the Workspace half of the pair (#1294).</summary>
public sealed class ShaderEditorView : DocumentView<ShaderDocument> {
    /// <inheritdoc />
    protected override UiElement Build(ShaderDocument document, UiElement panel) {
        // ⚠ `CodeEditor` virtualises: it computes the first visible line from its own `ScrollTop`,
        // how many rows to build from its own height, and where the caret goes from
        // `Scroller.Content.AbsoluteLeft`. A panel that scrolled it would slide a window that has
        // already decided which lines exist, so the file would end where the built rows do.
        DockPanel.Fills(panel);

        var view = panel.Add<CodeEditorView>();
        view.Show(document);

        return view;
    }
}

/// <summary>Opens a VXML component or a VCSS stylesheet, with the preview pane beside it.</summary>
public sealed class MarkupEditorFactory : DocumentKind {
    /// <inheritdoc />
    public override string Name => "UI";

    /// <inheritdoc />
    public override IReadOnlyList<string> Extensions { get; } = [MarkupDocument.Extension, StyleSheetDocument.Extension];

    /// <inheritdoc />
    public override IReadOnlyList<Type> DocumentTypes { get; } = [typeof(StyleSheetDocument), typeof(MarkupDocument)];

    /// <inheritdoc />
    public override EditorDocument Open(DocumentRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        return string.Equals(
            Path.GetExtension(request.Path),
            StyleSheetDocument.Extension,
            StringComparison.OrdinalIgnoreCase
        )
            ? new StyleSheetDocument(request.Project, request.Asset, request.Path)
            : new MarkupDocument(request.Project, request.Asset, request.Path);
    }
}

/// <summary>What <see cref="MarkupEditorFactory" />'s documents are shown in: the Workspace half of the pair (#1294).</summary>
/// <remarks>
///     ⚠ <b>Two views over one control</b>, because the kind opens two types and a view is found by
///     type: one keyed by <see cref="CodeDocument" /> would also be found for a
///     <see cref="ShaderDocument" />, and which of the two won would be registration order.
/// </remarks>
public sealed class MarkupEditorView : DocumentView<MarkupDocument> {
    /// <inheritdoc />
    protected override UiElement Build(MarkupDocument document, UiElement panel) => Preview(document, panel);

    /// <summary>A <c>CodeEditor</c> with a preview beside it — see <see cref="ShaderEditorView" />.</summary>
    internal static UiElement Preview(CodeDocument document, UiElement panel) {
        DockPanel.Fills(panel);

        var view = panel.Add<PreviewCodeEditorView>();
        view.Show(document);

        return view;
    }
}

/// <summary>What <see cref="MarkupEditorFactory" />'s stylesheets are shown in: the same control as <see cref="MarkupEditorView" />.</summary>
public sealed class StyleSheetEditorView : DocumentView<StyleSheetDocument> {
    /// <inheritdoc />
    protected override UiElement Build(StyleSheetDocument document, UiElement panel) =>
        MarkupEditorView.Preview(document, panel);
}
