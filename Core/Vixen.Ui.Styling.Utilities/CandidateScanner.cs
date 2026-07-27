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
/// </remarks>
public static class CandidateScanner {
    /// <summary>Pulls candidate class names out of a file's text.</summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="into">Receives the candidates.</param>
    public static void Scan(string text, ISet<string> into) {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(into);

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
                Take(text.AsSpan(start, i - start), into);
                start = -1;
            }

            depth = 0;
        }

        if (start >= 0) {
            Take(text.AsSpan(start), into);
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
