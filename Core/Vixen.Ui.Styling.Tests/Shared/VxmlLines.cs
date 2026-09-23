// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Markup.Testing;

/// <summary>Which of a <c>.vxml</c>'s languages a line is written in.</summary>
enum VxmlRegion {
    /// <summary>Markup, with any commented span already removed.</summary>
    Markup,

    /// <summary>The C# of a <c>@code</c> body, comment-only lines already dropped.</summary>
    Code
}

/// <summary>One line of a <c>.vxml</c> that carries something, and what language it is in.</summary>
/// <param name="Number">The one-based line number in the original file.</param>
/// <param name="Text">The line, with commented spans blanked out.</param>
/// <param name="Region">Which language <paramref name="Text" /> is in.</param>
readonly record struct VxmlLine(int Number, string Text, VxmlRegion Region);

/// <summary>What a source sweep is allowed to believe a <c>.vxml</c> line says.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A <c>.vxml</c> is three languages in one file and two of them read as markup.</b>
///         <c>&lt;!-- … --&gt;</c> is prose <i>about</i> markup — <c>&lt;harness-matrix&gt;</c> and
///         <c>&lt;mixer-strip-name&gt;</c> are described in header comments that way — and a
///         <c>@code</c> body is C#, where <c>List&lt;int&gt;</c> matches an element rule and
///         <c>&lt;b&gt;</c>, <c>&lt;para&gt;</c> and <c>&lt;paramref</c> match it from the <c>///</c>
///         blocks. Reading either as markup made <c>b</c>, <c>i</c> and <c>em</c> tags the repository
///         writes while <c>strong</c>, which no XML doc element is named after, was correctly
///         reported (<c>Rikarin/Vixen#1317</c>): an instrument that answered differently for two
///         selectors in one declaration.
///     </para>
///     <para>
///         ⚠ <b>Compiled into both sweeps rather than written twice, which is why its namespace
///         matches neither project.</b> <c>TypeSelectorReachTests</c> asks which tags the repository
///         writes and <c>MarkupAccessibleNameTests</c>, one assembly away, asks which words a control
///         is handed to say. They disagree about nothing except which pattern they run, and the
///         second was written with no comment or <c>@code</c> filter at all — so the batch that fixed
///         the defect in one scan shipped it in the other. Two copies of "what is a markup line" are
///         two chances to answer it differently. <c>Vixen.Geometry.Uv.Tests</c>' <c>RunawayGuard</c>
///         is linked into a second geometry suite the same way.
///     </para>
///     <para>
///         ⚠ <b>And then into four more, because two was not every reader.</b> Five further sweeps
///         read a <c>.vxml</c> with the C# comment forms as their only guard, or none —
///         <c>ScrollViewReachTests</c>, <c>ResponderReachTests</c>, <c>ClassSelectorReachTests</c>,
///         <c>SchedulerReachTests</c> and <c>StylesheetTests</c> — and four of them are reach
///         censuses, whose job is to say a finished thing has a caller. Prose about an API is not a
///         caller, and <c>FactRow.vxml</c>'s header is the proof that the prose here is written as
///         markup: its <c>Name="…"</c> example was a committed census row for a word nothing said
///         (<c>Rikarin/Vixen#1341</c>).
///     </para>
///     <para>
///         ⚠ <b>A comment is cut out of its line rather than taking the line with it, and that is the
///         direction that matters.</b> The first spelling of this dropped any line containing
///         <c>&lt;!--</c>, which loses the <c>foo</c> in <c>&lt;foo /&gt; &lt;!-- note --&gt;</c> and
///         the <c>bar</c> in <c>--&gt; &lt;bar&gt;</c>. A reach census that cannot see a tag reports
///         it as a name the repository never writes, which is a <i>false accusation</i> — the one
///         direction those censuses must not err in, and the opposite of the false negative they
///         accept on purpose. No committed <c>.vxml</c> writes such a line today; this is the guard
///         for the day one does, and <see cref="VxmlLinesTests" /> is where it is measured.
///     </para>
/// </remarks>
static class VxmlLines {
    /// <summary>Whether a line opens the <c>@code</c> body, after which the file is C#.</summary>
    /// <param name="text">The raw line.</param>
    /// <returns><c>true</c> when this is the <c>@code</c> directive.</returns>
    /// <remarks>
    ///     ⚠ <b>The line matters rather than the brace, because the brace is not where the C# stops
    ///     looking like markup.</b> The premise this rests on — one <c>@code</c> per file, and
    ///     nothing after its body — is asserted rather than assumed, by
    ///     <c>TypeSelectorReachTests.A_code_block_is_the_tail_of_its_file</c>, so the region does not
    ///     have to be found by counting braces the way <c>VxmlLexer</c> can only do because it knows
    ///     where the strings are.
    /// </remarks>
    public static bool IsCode(string text) {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.TrimStart();

        if (!trimmed.StartsWith("@code", StringComparison.Ordinal)) {
            return false;
        }

        return trimmed.Length == 5 || (!char.IsLetterOrDigit(trimmed[5]) && trimmed[5] != '_');
    }

    /// <summary>The lines of a <c>.vxml</c> a sweep may read, each labelled with its language.</summary>
    /// <param name="lines">The file, in order.</param>
    /// <returns>Every line that carries something, commented spans removed.</returns>
    /// <remarks>
    ///     A comment-only C# line is dropped outright — the <c>.cs</c> sweeps drop
    ///     <c>//</c> and continuation <c>*</c> lines the same way, and the <c>///</c> blocks in a
    ///     <c>@code</c> body are exactly where the XML doc vocabulary comes from. A markup line
    ///     survives with its commented spans replaced by a space, so the markup beside a comment is
    ///     still read and the two cannot be glued into a token neither wrote.
    /// </remarks>
    public static List<VxmlLine> Read(IEnumerable<string> lines) {
        ArgumentNullException.ThrowIfNull(lines);

        var read = new List<VxmlLine>();
        var number = 0;
        var prose = false;
        var code = false;

        foreach (var raw in lines) {
            number++;

            if (!code && !prose && IsCode(raw)) {
                code = true;
            }

            if (code) {
                var csharp = raw.TrimStart();

                // ⚠ `/*` as well, which the first spelling of this left out: it starts with neither a
                // `//` nor a `*`, so a block comment opening a line of a `@code` body was read as C#.
                // Every `.cs` sweep that moved onto this reader already refused that line itself, and
                // would have started accepting it in a `.vxml` the day it trusted this instead.
                if (csharp.StartsWith("//", StringComparison.Ordinal)
                    || csharp.StartsWith("/*", StringComparison.Ordinal)
                    || csharp.StartsWith('*')) {
                    continue;
                }

                read.Add(new VxmlLine(number, raw, VxmlRegion.Code));
                continue;
            }

            var text = Uncommented(raw, ref prose);

            // A line that was nothing but a comment is nothing at all; so is a blank one. Neither
            // can carry a name, and keeping them would make every count here a count of whitespace.
            if (text.Trim().Length != 0) {
                read.Add(new VxmlLine(number, text, VxmlRegion.Markup));
            }
        }

        return read;
    }

    /// <summary>The file line for line, with everything <see cref="Read" /> would not yield blanked.</summary>
    /// <param name="lines">The file, in order.</param>
    /// <returns>
    ///     As many lines as <paramref name="lines" />, so an index into it is still the file's line
    ///     number less one and a sweep that counts newlines still reports the right line.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>For the sweeps that read a whole file as one string or number lines themselves</b> —
    ///     a paren walk over a call's arguments, a regex over the file's text. They cannot take
    ///     <see cref="Read" />'s list without renumbering every line after the first comment, which is
    ///     the failure <c>The_numbers_are_the_files_own</c> is written against.
    /// </remarks>
    public static string[] Masked(IReadOnlyList<string> lines) {
        ArgumentNullException.ThrowIfNull(lines);

        var masked = new string[lines.Count];
        Array.Fill(masked, string.Empty);

        foreach (var line in Read(lines)) {
            masked[line.Number - 1] = line.Text;
        }

        return masked;
    }

    /// <summary>What a source sweep may read of a file: a <c>.vxml</c> masked, anything else as it is.</summary>
    /// <param name="path">The file's path, which is all that decides whether it is markup.</param>
    /// <param name="lines">Its lines, in order — passed in so a test can hand a sweep lines no file holds.</param>
    /// <returns>As many lines as <paramref name="lines" />.</returns>
    /// <remarks>
    ///     ⚠ <b>Only the <c>.vxml</c> half is this reader's.</b> A <c>.cs</c> file comes back untouched,
    ///     because every sweep that calls this already has its own answer to <c>//</c>, <c>*</c> and
    ///     <c>/*</c> and those answers differ on purpose — one refuses a declaration line, another reads
    ///     the prose of a census file. What none of them had was an answer to <c>&lt;!--</c>, which a
    ///     line-start test can never give: the shared parts' header comments demonstrate their own
    ///     element, so the prose is indented markup (<c>Rikarin/Vixen#1341</c>).
    /// </remarks>
    public static IReadOnlyList<string> Source(string path, IReadOnlyList<string> lines) {
        ArgumentNullException.ThrowIfNull(path);

        return path.EndsWith(".vxml", StringComparison.OrdinalIgnoreCase) ? Masked(lines) : lines;
    }

    /// <summary>One line with its commented spans taken out, carrying the open-comment state.</summary>
    /// <param name="text">The raw line.</param>
    /// <param name="prose">Whether a comment was open when the line began; updated on the way out.</param>
    /// <returns>What is left of the line once every commented span is a space.</returns>
    public static string Uncommented(string text, ref bool prose) {
        ArgumentNullException.ThrowIfNull(text);

        if (!prose && !text.Contains("<!--", StringComparison.Ordinal)) {
            return text;
        }

        var kept = new StringBuilder();
        var at = 0;

        while (at < text.Length) {
            if (prose) {
                var close = text.IndexOf("-->", at, StringComparison.Ordinal);

                if (close < 0) {
                    break;
                }

                // ⚠ The gap the comment occupied, left as a space so that the text on either side of
                // it cannot be spliced into a name neither half writes. Once here rather than at
                // both ends, because the open end only ever abuts text this same call already kept
                // and a comment left open runs to a later line, where no join is possible.
                prose = false;
                at = close + 3;
                kept.Append(' ');
                continue;
            }

            var open = text.IndexOf("<!--", at, StringComparison.Ordinal);

            if (open < 0) {
                kept.Append(text, at, text.Length - at);
                break;
            }

            kept.Append(text, at, open - at);
            prose = true;
            at = open + 4;
        }

        return kept.ToString();
    }
}
