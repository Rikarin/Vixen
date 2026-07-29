// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Syntax.Diagnostics;
using Vixen.Editor.Core;
using Vixen.Raven;
using Vixen.Raven.Syntax;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.AssetEditors.Code;

/// <summary>A Raven shader, open for editing, with the front end's complaints against it.</summary>
/// <remarks>
///     <para>
///         <b>Lex, parse and bind — and stop there.</b> That is where the diagnostics an author can
///         act on come from: an unterminated string, a type that does not exist, a call whose
///         arguments do not fit. Lowering and a backend produce artefacts, and an artefact is not
///         what a keystroke is asking for. Doc 07's shader compiler service is what runs the rest,
///         out of process, when something actually needs SPIR-V.
///     </para>
///     <para>
///         ⚠ <b>One file, no references.</b> The compilation is this tree alone, so a shader that
///         <c>import</c>s another names a symbol the binder cannot resolve and gets a diagnostic for
///         it. Resolving those needs the project's whole shader set and a reference graph, which is
///         the compiler service's model rather than a panel's — and until an editor has it, the
///         honest thing is that cross-file errors are reported and cross-file <i>successes</i> are
///         not yet possible.
///     </para>
///     <para>
///         ⚠ <b>A diagnostic with no location lands on line one.</b> <c>Location.None</c> means the
///         complaint is about the compilation rather than about a span — and dropping those would
///         hide exactly the ones that are hardest to work out from the file.
///     </para>
/// </remarks>
public sealed class ShaderDocument : CodeDocument {
    /// <summary>What a Raven shader is written as.</summary>
    public const string Extension = ".rvn";

    /// <inheritdoc />
    /// <remarks>
    ///     <c>CStyleTokenizer.Raven</c>, which the control set already ships with Raven's keyword and
    ///     type lists in it — so the highlighting and the language cannot drift apart in the way two
    ///     hand-maintained keyword lists would.
    /// </remarks>
    public override ICodeTokenizer Tokenizer => CStyleTokenizer.Raven;

    /// <summary>Opens a shader.</summary>
    /// <param name="project">The project it belongs to.</param>
    /// <param name="asset">Its identity.</param>
    /// <param name="path">Where the file is, absolute.</param>
    public ShaderDocument(EditorProject project, AssetId asset, string path)
        : base(project, asset, path) {
    }

    /// <inheritdoc />
    protected override IReadOnlyList<CodeDiagnostic> Analyse(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var tree = SyntaxTree.ParseText(text, path: AssetPath);
        var compilation = Compilation.Create(Path.GetFileNameWithoutExtension(AssetPath), tree);

        List<CodeDiagnostic> found = [];

        foreach (var diagnostic in compilation.GetDiagnostics()) {
            found.Add(Translate(diagnostic));
        }

        return found;
    }

    /// <summary>Turns one of the front end's diagnostics into one the editor can draw.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <returns>The editor's form of it.</returns>
    internal static CodeDiagnostic Translate(Diagnostic diagnostic) {
        ArgumentNullException.ThrowIfNull(diagnostic);

        var severity = diagnostic.Severity switch {
            DiagnosticSeverity.Error => CodeSeverity.Error,
            DiagnosticSeverity.Warning => CodeSeverity.Warning,
            _ => CodeSeverity.Hint
        };

        var message = $"{diagnostic.Id}: {diagnostic.GetMessage()}";

        if (diagnostic.Location.IsNone) {
            return new(0, 0, 0, severity, message);
        }

        var span = diagnostic.Location.GetLineSpan();
        var length = span.Start.Line == span.End.Line
            ? Math.Max(span.End.Character - span.Start.Character, 1)

            // A squiggle that runs to the end of the file is a squiggle over the whole file, which
            // says less than one over the line the problem starts on. Multi-line spans are drawn on
            // their first line until the editor can draw a real range.
            : 1;

        return new(span.Start.Line, span.Start.Character, length, severity, message);
    }
}
