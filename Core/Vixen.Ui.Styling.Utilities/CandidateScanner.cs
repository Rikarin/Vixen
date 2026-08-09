// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling.Utilities;

/// <summary>Finds the class names a project might be using.</summary>
/// <remarks>
///     <para>
///         <b>Deliberately over-inclusive, and that is the whole design.</b> It does not parse VXML
///         or C#; it pulls out every run of characters that <i>could</i> be a class name and lets the
///         generator throw away what is not one. A false positive costs one unused rule that no
///         element matches; a false negative is a style silently missing at runtime, which someone
///         debugs for an hour. The asymmetry is enormous and the design follows it.
///     </para>
///     <para>
///         It also means the scanner cannot be defeated by cleverness, which a parser can. A class
///         name built by string concatenation is invisible to any analysis, but the pieces are
///         usually literals, and a scanner that takes every literal finds them.
///     </para>
///     <para>
///         The one thing it must not do is find so much that generation slows down. Runs are bounded
///         by the characters a utility can contain, which excludes whitespace, quotes and most
///         punctuation, so a paragraph of prose yields its words and nothing longer.
///     </para>
///     <para>
///         ⚠ <b>Exactly one input is narrowed, and it is narrowed by structure rather than by
///         judgement.</b> <see cref="ScanStyleSheet" /> skips a declaration's value, because a class
///         name cannot be <i>used</i> from the right of a colon and <c>position: absolute</c> is
///         otherwise indistinguishable from <c>class="absolute"</c>. That is a fact about where CSS
///         puts class names, not a guess about which candidates are real, so it takes nothing away
///         from the asymmetry above — and it is deliberately not generalised to C# or <c>.vxml</c>,
///         where a colon means whatever the language it was written in says it means.
///     </para>
/// </remarks>
public static class CandidateScanner {
    /// <summary>Pulls candidate class names out of a file's text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="into">Receives the candidates.</param>
    public static void Scan(string text, ISet<string> into) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(into);

        Scan(text.AsSpan(), into);
    }

    /// <summary>Pulls candidate class names out of a stylesheet, skipping declaration values.</summary>
    /// <param name="text">The stylesheet.</param>
    /// <param name="into">Receives the candidates.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one input where over-inclusiveness has a shape it can be narrowed by, and the
    ///         narrowing is a delimiter rule rather than a parser.</b> <c>position: absolute;</c> and
    ///         <c>class="absolute"</c> are the same characters to <see cref="Scan(string,ISet{string})" />,
    ///         so seven of the editor's twenty-five generated rules were CSS keywords out of its own
    ///         sheets. <b>A class name cannot be <i>used</i> from the right of a colon</b>: inside a
    ///         block, the text between a declaration's <c>:</c> and its terminator is a value, and a
    ///         value is never a list of class names. Skipping it loses nothing, which is what makes
    ///         this narrowing legitimate where a general one would not be — C# and <c>.vxml</c> input
    ///         still go through <see cref="Scan(string,ISet{string})" /> unchanged.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The colon is not the test — <c>an identifier, then the colon</c> is — and
    ///         <c>@apply p-4 flex;</c> is why.</b> An at-rule's prelude inside a block <i>is</i> a list
    ///         of class names — <c>@apply</c> is the one construct in CSS that puts one there. A rule
    ///         written as "skip from any <c>:</c> inside a block" would take <c>flex</c> out of
    ///         <c>@apply p-4 hover:bg-accent flex;</c> — the false negative this whole design exists to
    ///         prevent, reintroduced by the fix for the false positives. Requiring the colon to follow
    ///         a single identifier is what excludes it: <c>@</c> is not an identifier character and a
    ///         prelude has spaces in it, so an at-rule cannot be mistaken for a declaration however it
    ///         is punctuated.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The same rule is what keeps prose safe, and prose is not a hypothetical here.</b>
    ///         A CSS comment ends at its first <c>*/</c>, and this tree's own comments talk about globs
    ///         — <c>**/*.vcss</c> closes the comment it is written inside. Everything after that is
    ///         ordinary text as far as any reader of the syntax is concerned, sentence colons included,
    ///         and a bare-colon rule swallowed a paragraph of it as if it were a value. An English
    ///         sentence never puts an identifier and nothing else in front of its colon.
    ///     </para>
    ///     <para>
    ///         Three delimiters are tracked and nothing else. Brace depth, because a <c>:</c> outside a
    ///         block belongs to a selector — <c>button:hover</c> — and not to a declaration. An
    ///         opening <c>{</c> retracts the guess rather than ending it: a declaration cannot contain
    ///         one, so text that ran into a brace was a selector inside a wrapper — <c>@layer
    ///         components { tree-row:hover { … } }</c> is most of this tree — and it is scanned after
    ///         all. And <c>/* … */</c> is stepped over as structure while still being scanned as text,
    ///         because a comment is where a good deal of this tree's candidates come from and a colon
    ///         inside one is not a declaration.
    ///     </para>
    ///     <para>
    ///         The colon itself stays in the scanned text. A run ending in a separator is already
    ///         rejected, which is the only reason <c>color</c> in <c>color: red</c> was never a
    ///         candidate — cutting the segment before the colon would turn every property name in
    ///         every sheet into one, and <c>border</c>, <c>grid</c> and <c>flex</c> are all property
    ///         names.
    ///     </para>
    /// </remarks>
    public static void ScanStyleSheet(string text, ISet<string> into) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(into);

        var depth = 0;

        // The start of the text not yet handed to the scanner, and the first non-blank character of
        // the statement being read — which is what tells `@apply p-4 flex` from `padding: 4px`.
        var keep = 0;
        var statement = -1;

        for (var i = 0; i < text.Length;) {
            var c = text[i];

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '*') {
                var comment = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = comment < 0 ? text.Length : comment + 2;
                continue;
            }

            switch (c) {
                case '{':
                    depth++;
                    statement = -1;
                    break;

                case '}':
                    if (depth > 0) {
                        depth--;
                    }

                    statement = -1;
                    break;

                case ';':
                    statement = -1;
                    break;

                case ':' when depth > 0 && IsDeclaration(text, statement, i): {
                    var end = text.IndexOfAny([';', '}', '{'], i + 1);
                    if (end < 0) {
                        end = text.Length;
                    }

                    // ⚠ A `{` says the guess was wrong and nothing is dropped. A declaration ends at
                    // its `;` or at its block's `}`; text that ran into an *opening* brace was a
                    // selector all along — `tree-row:hover {`, which is what a whole sheet of nested
                    // or grouped rules is made of — and a selector is scanned like any other text.
                    if (end < text.Length && text[end] == '{') {
                        i = end;
                        continue;
                    }

                    Scan(text.AsSpan(keep, i + 1 - keep), into);
                    keep = end;
                    i = end;

                    continue;
                }

                default:
                    if (statement < 0 && !char.IsWhiteSpace(c)) {
                        statement = i;
                    }

                    break;
            }

            i++;
        }

        Scan(text.AsSpan(keep), into);
    }

    /// <summary>Whether the colon at <paramref name="colon" /> ends a declaration's property name.</summary>
    /// <remarks>
    ///     A declaration's prelude is one identifier and the colon comes straight after it — so
    ///     <c>padding:</c> qualifies, <c>@apply p-4 hover:</c> does not, and neither does the colon in
    ///     a sentence. Custom properties are identifiers too, which is why <c>-</c> is in the set.
    /// </remarks>
    static bool IsDeclaration(string text, int statement, int colon) {
        if (statement < 0 || statement == colon) {
            return false;
        }

        for (var i = statement; i < colon; i++) {
            if (!char.IsAsciiLetterOrDigit(text[i]) && text[i] is not ('-' or '_')) {
                return false;
            }
        }

        return true;
    }

    static void Scan(ReadOnlySpan<char> text, ISet<string> into) {
        var start = -1;
        var depth = 0;

        for (var i = 0; i < text.Length; i++) {
            var c = text[i];

            // Brackets are part of a candidate — `w-[37px]` — so a run does not end inside one.
            if (c == '[') {
                depth++;
            } else if (c == ']' && depth > 0) {
                depth--;
            }

            if (IsCandidateChar(c) || (depth > 0 && c != '"' && c != '\'')) {
                if (start < 0) {
                    start = i;
                }

                continue;
            }

            if (start >= 0) {
                Take(text[start..i], into);
                start = -1;
            }

            depth = 0;
        }

        if (start >= 0) {
            Take(text[start..], into);
        }
    }

    static void Take(ReadOnlySpan<char> run, ISet<string> into) {
        // A bare word is a candidate — `flex`, `truncate` — but a run has to contain something that
        // makes it plausible, or every word of every comment becomes one. A hyphen, a colon or a
        // bracket is that something; bare words are only taken when they are short.
        var interesting = run.ContainsAny(Interesting);

        if (!interesting && run.Length > 12) {
            return;
        }

        if (run.Length is 0 or > 120) {
            return;
        }

        // A run ending in a separator is a fragment of something the scanner cut, not a class name.
        if (run[^1] is '-' or ':' or '/') {
            return;
        }

        into.Add(run.ToString());
    }

    static ReadOnlySpan<char> Interesting => "-:[/";

    static bool IsCandidateChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ':' or '/' or '.' or '[' or ']' or '&' or '>' or '*' or '=' or '!' or '%' or '#' or '(' or ')' or ',';
}
