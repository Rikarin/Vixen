// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Editor.Core;
using Vixen.Ui.Controls.Advanced;
using Vixen.Ui.Markup.Binding;
using MarkupTree = Vixen.Ui.Markup.Syntax.SyntaxTree;

namespace Vixen.Editor.AssetEditors.Code;

/// <summary>A VXML component, open for editing, with a bound tree the preview pane draws.</summary>
/// <remarks>
///     <para>
///         <b>Lex, parse and bind.</b> The binder is where the useful complaints are — an unclosed
///         tag, an attribute on a tag that has none, an <c>@for</c> body whose elements have no key
///         — and it produces a <see cref="BoundComponent" />, which is the thing a preview can be
///         built from without a Roslyn compilation.
///     </para>
///     <para>
///         ⚠ <b>The preview is the static structure and not the running component.</b> A
///         <c>.vxml</c> becomes a C# partial class, so a genuinely live preview means compiling and
///         loading the generated type — which is the hot-reload pipeline doc 09 describes and doc 11
///         wants this pane to sit on. What is here is one step short of it and is what makes the
///         pane useful today: the element tree with its literal attributes, its text, and a
///         placeholder where an expression would go. Layout and styling — which is most of what a
///         <c>.vxml</c> is being edited for — are exactly right in that picture; state and bindings
///         are not there at all, and the pane says so rather than pretending.
///     </para>
/// </remarks>
public sealed class MarkupDocument : CodeDocument {
    /// <summary>What a VXML component is written as.</summary>
    public const string Extension = ".vxml";

    /// <summary>The bound component, as of the last analysis, or <see langword="null" />.</summary>
    public BoundComponent? Component { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Plain, deliberately.</b> The control set has no markup tokenizer, and colouring VXML
    ///     with the C-style one would paint its tag names as identifiers and its <c>@</c> forms as
    ///     nothing — which reads as broken highlighting rather than as none. A VXML tokenizer belongs
    ///     beside <c>CStyleTokenizer</c> in <c>Vixen.Ui.Controls.Advanced</c>, where the lexer it
    ///     would have to agree with already is.
    /// </remarks>
    public override ICodeTokenizer Tokenizer => PlainTokenizer.Instance;

    /// <summary>Opens a VXML component.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public MarkupDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, path) {
    }

    /// <inheritdoc />
    protected override IReadOnlyList<CodeDiagnostic> Analyse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var tree = MarkupTree.ParseText(text, AssetPath);

        Component = Binder.Bind(tree, out var diagnostics);

        List<CodeDiagnostic> found = [];

        foreach (var diagnostic in diagnostics) {
            found.Add(ShaderDocument.Translate(diagnostic));
        }

        return found;
    }
}

/// <summary>A stylesheet, open for editing.</summary>
/// <remarks>
///     <para>
///         <b>The preview is genuinely live here, and that is the difference from VXML.</b> A
///         stylesheet needs no compilation: <c>StyleEngine.Replace</c> swaps a sheet's text and
///         restyles, so the pane shows the real cascade over a real element tree. It is the cheapest
///         thing in doc 11's table and the one that pays back most often, because most of what a
///         <c>.vcss</c> is edited for is a number somebody wants to see move.
///     </para>
///     <para>
///         ⚠ <b>Nothing here reports a syntax error.</b> <c>StyleSheetLoader</c> follows CSS's own
///         recovery rules — a declaration it cannot parse is dropped and the rest of the rule stands
///         — and reports nothing to a caller, which is right for a browser and unhelpful in an
///         editor. The rule that vanished is visible in the preview and invisible in the gutter; a
///         diagnostic-producing loader is the fix and it belongs in <c>Vixen.Ui.Styling</c>.
///     </para>
/// </remarks>
public sealed class StyleSheetDocument : CodeDocument {
    /// <summary>What a stylesheet is written as.</summary>
    public const string Extension = ".vcss";

    /// <inheritdoc />
    /// <remarks>
    ///     The C-style tokenizer with no keywords, which gets comments, strings and numbers right and
    ///     leaves selectors and properties plain. Better than nothing and honestly less than CSS
    ///     deserves; see <see cref="MarkupDocument.Tokenizer" /> for where a real one belongs.
    /// </remarks>
    public override ICodeTokenizer Tokenizer => Css;

    static ICodeTokenizer Css { get; } = new CStyleTokenizer([]);

    /// <summary>Opens a stylesheet.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public StyleSheetDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, path) {
    }
}
