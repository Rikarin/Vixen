// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Frozen;

namespace Vixen.Ui.Controls.Advanced;

/// <summary>What a run of characters is, as far as colouring it is concerned.</summary>
/// <remarks>
///     ⚠ <b>A presentation category, not a parse.</b> These are the distinctions a reader's eye uses,
///     which is a much smaller set than a language's grammar — and deliberately so: a highlighter
///     that reported syntax nodes would have to be a parser, would have to be one per language, and
///     would have to be right about a file that is half-typed. Every kind here is written through to
///     a class the theme selects on, in the same way <c>ControlVariant</c> is.
/// </remarks>
public enum CodeTokenKind : byte {
    /// <summary>Anything with no opinion about it, including whitespace.</summary>
    Plain,

    /// <summary>A reserved word.</summary>
    Keyword,

    /// <summary>A type name, by whatever rule the tokenizer uses.</summary>
    Type,

    /// <summary>A numeric literal.</summary>
    Number,

    /// <summary>A string or character literal.</summary>
    String,

    /// <summary>A comment, of either shape.</summary>
    Comment,

    /// <summary>An operator or a bracket.</summary>
    Operator,

    /// <summary>A preprocessor line, an attribute, an annotation.</summary>
    Directive
}

/// <summary>One coloured run of a line.</summary>
/// <param name="Start">Where it starts in the line.</param>
/// <param name="Length">How long it is.</param>
/// <param name="Kind">What it is.</param>
public readonly record struct CodeToken(int Start, int Length, CodeTokenKind Kind);

/// <summary>Turns a line into coloured runs.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A line at a time, carrying a state across the boundary.</b> Highlighting is asked for
///         one screenful at a time by a virtualised editor, so a tokenizer that needed the whole file
///         would defeat the virtualisation it is being called from. The state is what makes a block
///         comment work: line 40 is <i>inside</i> one because line 39 ended inside one.
///     </para>
///     <para>
///         ⚠ <b>An <see cref="int" /> rather than an object.</b> The editor caches one per line of
///         the file, and a boxed state per line would be a per-file allocation the size of the file.
///     </para>
///     <para>
///         This is where a <c>Vixen.Core.Syntax</c>-backed highlighter plugs in. It is not the
///         default because a control assembly that referenced a parser would drag one language's
///         grammar into every application that wanted a text box with colours in it — and because
///         the editor has to colour a file that does not parse, which is most of them, most of the
///         time somebody is looking at one.
///     </para>
/// </remarks>
public interface ICodeTokenizer {
    /// <summary>The state a file starts in.</summary>
    int InitialState => 0;

    /// <summary>Splits one line into runs.</summary>
    /// <param name="line">The line's text, without its terminator.</param>
    /// <param name="state">What the previous line ended in.</param>
    /// <param name="into">Where to put the runs. Cleared by the caller, filled in order.</param>
    /// <returns>The state this line ends in.</returns>
    int Tokenize(string line, int state, List<CodeToken> into);
}

/// <summary>The tokenizer for a file nobody has a grammar for.</summary>
public sealed class PlainTokenizer : ICodeTokenizer {
    /// <summary>The one there needs to be.</summary>
    public static PlainTokenizer Instance { get; } = new();

    /// <inheritdoc />
    public int Tokenize(string line, int state, List<CodeToken> into) {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(into);

        if (line.Length > 0) {
            into.Add(new CodeToken(0, line.Length, CodeTokenKind.Plain));
        }

        return 0;
    }
}

/// <summary>A tokenizer for the curly-brace languages, given their reserved words.</summary>
/// <remarks>
///     <para>
///         One implementation for Raven, C# and VCSS because their <i>lexical</i> shapes agree —
///         <c>//</c> to the end of the line, <c>/* */</c> across them, quoted strings with
///         backslash escapes, numbers, and a set of words. What differs between them is grammar,
///         which a highlighter does not read.
///     </para>
///     <para>
///         ⚠ <b>The only state it carries is "inside a block comment".</b> A multi-line string —
///         C#'s <c>"""</c>, a template literal — would need another, and reporting one as several
///         broken strings is wrong in a way that is visible. Said out loud: this handles the
///         languages named above, and a language with multi-line literals wants its own tokenizer,
///         which is what the interface is for.
///     </para>
/// </remarks>
public sealed class CStyleTokenizer : ICodeTokenizer {
    const int InComment = 1;

    readonly FrozenSet<string> keywords;
    readonly FrozenSet<string> types;

    /// <summary>Creates a tokenizer over two word sets.</summary>
    /// <param name="keywords">The reserved words.</param>
    /// <param name="types">The words shown as types.</param>
    public CStyleTokenizer(IEnumerable<string> keywords, IEnumerable<string>? types = null) {
        ArgumentNullException.ThrowIfNull(keywords);

        this.keywords = keywords.ToFrozenSet(StringComparer.Ordinal);
        this.types = (types ?? []).ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>Raven's reserved words.</summary>
    public static CStyleTokenizer Raven { get; } = new(
        [
            "let", "var", "func", "fn", "return", "if", "else", "for", "while", "in", "match", "case",
            "struct", "class", "enum", "interface", "import", "export", "public", "private", "internal",
            "static", "const", "mutable", "true", "false", "null", "self", "new", "as", "is", "break",
            "continue", "shader", "vertex", "fragment", "compute", "uniform", "layout", "out"
        ],
        [
            "int", "uint", "float", "double", "bool", "string", "void", "vec2", "vec3", "vec4",
            "mat2", "mat3", "mat4", "sampler2D", "texture2D", "byte", "short", "long", "char"
        ]
    );

    /// <summary>C#'s, near enough for an in-editor script.</summary>
    public static CStyleTokenizer CSharp { get; } = new(
        [
            "abstract", "as", "base", "break", "case", "catch", "checked", "class", "const", "continue",
            "default", "delegate", "do", "else", "enum", "event", "explicit", "extern", "false",
            "finally", "fixed", "for", "foreach", "goto", "if", "implicit", "in", "interface",
            "internal", "is", "lock", "namespace", "new", "null", "operator", "out", "override",
            "params", "private", "protected", "public", "readonly", "ref", "return", "sealed",
            "sizeof", "stackalloc", "static", "struct", "switch", "this", "throw", "true", "try",
            "typeof", "unchecked", "unsafe", "using", "var", "virtual", "volatile", "where", "while",
            "yield", "record", "when", "with", "init", "required", "partial", "async", "await"
        ],
        [
            "bool", "byte", "char", "decimal", "double", "float", "int", "long", "object", "sbyte",
            "short", "string", "uint", "ulong", "ushort", "void", "nint", "nuint", "dynamic"
        ]
    );

    /// <inheritdoc />
    public int Tokenize(string line, int state, List<CodeToken> into) {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(into);

        var index = 0;

        if (state == InComment) {
            var close = line.IndexOf("*/", StringComparison.Ordinal);

            if (close < 0) {
                Emit(into, 0, line.Length, CodeTokenKind.Comment);
                return InComment;
            }

            Emit(into, 0, close + 2, CodeTokenKind.Comment);
            index = close + 2;
        }

        while (index < line.Length) {
            var start = index;
            var current = line[index];

            if (char.IsWhiteSpace(current)) {
                while (index < line.Length && char.IsWhiteSpace(line[index])) {
                    index++;
                }

                Emit(into, start, index - start, CodeTokenKind.Plain);
                continue;
            }

            if (current == '/' && index + 1 < line.Length && line[index + 1] == '/') {
                Emit(into, start, line.Length - start, CodeTokenKind.Comment);
                return 0;
            }

            if (current == '/' && index + 1 < line.Length && line[index + 1] == '*') {
                var close = line.IndexOf("*/", index + 2, StringComparison.Ordinal);

                if (close < 0) {
                    Emit(into, start, line.Length - start, CodeTokenKind.Comment);
                    return InComment;
                }

                index = close + 2;
                Emit(into, start, index - start, CodeTokenKind.Comment);

                continue;
            }

            if (current is '"' or '\'') {
                index = Literal(line, index, current);
                Emit(into, start, index - start, CodeTokenKind.String);

                continue;
            }

            if (char.IsDigit(current)) {
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '.')) {
                    index++;
                }

                Emit(into, start, index - start, CodeTokenKind.Number);
                continue;
            }

            if (current is '#' or '@' && index == FirstNonSpace(line)) {
                Emit(into, start, line.Length - start, CodeTokenKind.Directive);
                return 0;
            }

            if (char.IsLetter(current) || current == '_') {
                while (index < line.Length && (char.IsLetterOrDigit(line[index]) || line[index] == '_')) {
                    index++;
                }

                var word = line[start..index];

                Emit(
                    into,
                    start,
                    index - start,
                    keywords.Contains(word) ? CodeTokenKind.Keyword
                    : types.Contains(word) ? CodeTokenKind.Type
                    : CodeTokenKind.Plain
                );

                continue;
            }

            index++;
            Emit(into, start, 1, CodeTokenKind.Operator);
        }

        return 0;
    }

    /// <summary>Runs to the end of a quoted literal, or to the end of the line if it is unterminated.</summary>
    /// <remarks>
    ///     ⚠ <b>An unterminated string ends at the line's end rather than swallowing the file.</b>
    ///     Half a string is what a file looks like for the whole time somebody is typing one, and a
    ///     highlighter that turned the rest of the document red on every opening quote would be
    ///     unusable exactly when it is being watched.
    /// </remarks>
    static int Literal(string line, int index, char quote) {
        index++;

        while (index < line.Length) {
            if (line[index] == '\\' && index + 1 < line.Length) {
                index += 2;
                continue;
            }

            if (line[index] == quote) {
                return index + 1;
            }

            index++;
        }

        return line.Length;
    }

    static int FirstNonSpace(string line) {
        var index = 0;

        while (index < line.Length && char.IsWhiteSpace(line[index])) {
            index++;
        }

        return index;
    }

    static void Emit(List<CodeToken> into, int start, int length, CodeTokenKind kind) {
        if (length > 0) {
            into.Add(new CodeToken(start, length, kind));
        }
    }
}

/// <summary>How bad a diagnostic is.</summary>
public enum CodeSeverity : byte {
    /// <summary>Worth knowing.</summary>
    Hint,

    /// <summary>It compiles and probably should not.</summary>
    Warning,

    /// <summary>It does not compile.</summary>
    Error
}

/// <summary>Something to say about a place in the file.</summary>
/// <param name="Line">Which line, from zero.</param>
/// <param name="Column">Where it starts on it.</param>
/// <param name="Length">How much of it is wrong.</param>
/// <param name="Severity">How bad.</param>
/// <param name="Message">What to say.</param>
public readonly record struct CodeDiagnostic(
    int Line,
    int Column,
    int Length,
    CodeSeverity Severity,
    string Message
);

/// <summary>A range of lines that can be collapsed to its first one.</summary>
/// <param name="Start">The line that stays visible.</param>
/// <param name="End">The last line hidden when it is collapsed.</param>
public readonly record struct CodeFold(int Start, int End);

/// <summary>One thing an autocomplete popup offers.</summary>
/// <param name="Label">What is inserted and what is shown.</param>
/// <param name="Detail">A type, a signature, a namespace — shown greyed beside it.</param>
public readonly record struct CompletionItem(string Label, string? Detail = null);
