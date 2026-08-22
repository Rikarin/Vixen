// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui.Styling.Utilities;

namespace Vixen.StyleGen;

/// <summary>What one run produced, and everything it wants to say about it.</summary>
/// <param name="Css">The whole sheet: the hand-written rules, then the utilities.</param>
/// <param name="Utilities">The generated half on its own.</param>
/// <param name="Unrecognised">Candidates the generator did not know. Prose, mostly.</param>
/// <param name="Unresolved">
///     Candidates that named a registered family and still emitted nothing.
///     <para>
///         ⚠ <b>Reported apart from <paramref name="Unrecognised" />, because the two differ by two
///         orders of magnitude in size and the small one is where a real class hides.</b>
///         <c>Vixen.Editor.Ui</c> produces forty-three of these against seven thousand of those, so
///         a reader who has to answer "why does <c>bg-clip-text</c> do nothing" now has forty-three
///         lines to look at rather than seven thousand and no way to tell which kind of nothing it
///         was.
///     </para>
///     <para>
///         <b>Not a per-line build message, and that was measured rather than assumed.</b> Of those
///         forty-three, thirty-four are a bare English word colliding with a registered family name
///         and the rest are CSS property names out of a scanned <c>.vcss</c> — the scanner is
///         over-inclusive on purpose and no channel downstream of it can undo that. The count goes on
///         standard output and the sentences go in the report.
///     </para>
/// </param>
/// <param name="Errors">Reasons the build should fail.</param>
/// <param name="RuleCount">How many utility rules were emitted.</param>
internal sealed record StyleGenResult(
    string Css,
    string Utilities,
    IReadOnlyList<string> Unrecognised,
    IReadOnlyList<UtilityRefusal> Unresolved,
    IReadOnlyList<string> Errors,
    int RuleCount
);

/// <summary>The build step: tokens in, a stylesheet out.</summary>
/// <remarks>
///     <para>
///         <b>This is the thing the utility system's README listed as ⏳.</b> Before it, every project
///         wanting utilities wrote its own startup bootstrap — embed the markup as resources, walk
///         the manifest, run the scanner, run the generator — which
///         <c>Samples/14-Mmo/Mmo.Ui/Theme/MmoStyles.cs</c> was the last copy of, and whose own
///         remarks admitted it was standing in for this. That file is deleted; the sample declares
///         <c>VixenUi</c> and the generated class keeps its name.
///     </para>
///     <para>
///         ⚠ <b>Out of process, and the reason it had to be has expired.</b> The obvious shape for
///         "read some files at build time and emit code" is an <c>IIncrementalGenerator</c>, and it
///         could not be one here: <see cref="ThemeTokens" /> read YAML through
///         <c>Vixen.Core.Yaml</c>, which is YamlDotNet, and an analyzer's dependencies do not travel
///         with it — <c>OutputItemType="Analyzer"</c> contributes one DLL, so every consuming project
///         would have had to put the rest on the analyzer path itself.
///         <c>Vixen.Ui.Markup.Generators</c> escapes the same trap by <em>linking</em> its front
///         end's source into the analyzer, which works because that front end is Vixen's own code and
///         could not have worked for a package.
///     </para>
///     <para>
///         Under <c>@theme</c> there is no YamlDotNet: a token is a custom property in a stylesheet
///         and the reader is a text scan over <c>Vixen.Ui.Styling.Utilities</c>' own code, which has
///         no package references left at all. So the blocker has lifted and this is still a process,
///         which is a decision now rather than a constraint. Making it a generator is a real change
///         and belongs in its own commit; the cost of leaving it is one process launch per build that
///         changed a scanned file.
///     </para>
///     <para>
///         ⚠ <b>It writes a file, and that is worth more than it looks.</b> A generated constant is
///         enough for the compiler and no use to anything else; a <c>.vcss</c> in <c>obj/</c> is what
///         a hot-reload watcher can watch and what an asset-pipeline step can take as an input. The
///         accessor is emitted <i>as well</i>, so a project that only wants to call
///         <c>document.Load(…)</c> never touches the file system.
///     </para>
/// </remarks>
internal static class StyleGenRunner {
    /// <summary>Runs the step.</summary>
    /// <param name="request">What to do.</param>
    /// <returns>The sheet, and what to say about it.</returns>
    internal static StyleGenResult Run(StyleGenRequest request) {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<string>();

        // ⚠ **The shipped defaults first, and every project gets them.** v4's model is that `@theme`
        // *has* defaults and a project layers over them — `--color-*: initial;` is how you say you
        // want none of it — so a project with no theme file of its own is not a project with no
        // tokens. It is the whole reason `bg-blue-500` works in a game that wrote nothing.
        var tokens = ThemeTokens.CreateDefault();
        var seen = 0;

        // The base sheets are read here rather than where they are emitted, because an `@theme` block
        // in one of them has to reach the token set *before* anything is generated against it. Read
        // twice would be the alternative, and the second read is where a file changing under the
        // build stops being harmless.
        var sheets = new List<(string Path, string Css)>();

        foreach (var path in request.Themes) {
            if (!File.Exists(path)) {
                errors.Add($"{path}: there is no theme file here");
                continue;
            }

            sheets.Add((path, File.ReadAllText(path)));
        }

        var themeCount = sheets.Count;

        foreach (var path in request.Base) {
            if (!File.Exists(path)) {
                errors.Add($"{path}: the base stylesheet is not there");
                continue;
            }

            sheets.Add((path, File.ReadAllText(path)));
        }

        foreach (var (path, text) in sheets) {
            tokens.Apply(text);

            for (; seen < tokens.Diagnostics.Count; seen++) {
                errors.Add($"{path}: {tokens.Diagnostics[seen]}");
            }
        }

        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in request.Scan) {
            if (!File.Exists(path)) {
                // Not an error. MSBuild hands over a glob's worth of items and a file deleted
                // between evaluation and execution is a normal thing in an incremental build; the
                // failure worth reporting is a *token* file that is missing, which is above.
                continue;
            }

            var text = File.ReadAllText(path);

            // ⚠ **A stylesheet is the one scanned input whose shape says where a class name cannot
            // be, and reading it as plain text is what put `.absolute`, `.block`, `.grid`,
            // `.hidden`, `.inline`, `.relative` and `.static` into the editor's sheet.** None of
            // them was written by anybody: they are the right-hand sides of `position: absolute`
            // and `display: block` in the editor's own `.vcss` files, and a class name cannot be
            // used from there. Decided by extension rather than by sniffing the text, because the
            // build step already globs `**/*.vcss` as its own item and the two should agree.
            if (Path.GetExtension(path.AsSpan()).Equals(".vcss", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path.AsSpan()).Equals(".css", StringComparison.OrdinalIgnoreCase)) {
                CandidateScanner.ScanStyleSheet(text, candidates);
                continue;
            }

            CandidateScanner.Scan(text, candidates);
        }

        foreach (var safe in request.Safelist) {
            candidates.Add(safe);
        }

        var generator = new UtilityGenerator(tokens);
        var utilities = generator.Generate(candidates);

        // ⚠ The base sheets first, and `@apply` expanded before anything else sees them. The layer
        // is what makes a component rule beat a utility, so the order here is not what decides it —
        // which is exactly why the base sheet goes first: written second it would win on source
        // order too, and a layering regression would then be invisible.
        var css = new StringBuilder();
        var expander = new ApplyExpander(tokens);

        foreach (var (path, text) in sheets.Skip(themeCount)) {
            // ⚠ Stripped, because `@theme` is a build-time construct and the cascade has never heard
            // of it. Its declarations have already been read into `tokens` above and whatever the
            // sheet references comes back as a `root` rule below, so leaving the block in would be a
            // second copy of the same values that the loader then drops with a diagnostic.
            css.Append(expander.Expand(ThemeTokens.Strip(text))).Append('\n');

            foreach (var diagnostic in expander.Diagnostics) {
                errors.Add($"{path}: {diagnostic}");
            }
        }

        css.Append(utilities);

        // ⚠ **The variables last to compute and first to emit.** Which of three hundred and
        // forty-seven theme declarations a sheet needs is only knowable once the sheet exists, so
        // the scan runs over the finished text — and the block it produces goes at the *top*, where
        // a hand-written `root { --accent: … }` further down still wins on source order. A theme
        // variable that beat the sheet declaring it would make the token model unopinionated in the
        // one direction it must not be.
        var root = tokens.RootRuleFor(css.ToString());

        return new StyleGenResult(
            root.Length > 0 ? root + '\n' + css : css.ToString(),
            utilities,
            [.. generator.Unrecognised],
            [.. generator.Unresolved],
            errors,
            generator.RuleCount);
    }

    /// <summary>Writes whatever the request asked to be written.</summary>
    /// <param name="request">The request.</param>
    /// <param name="result">What the run produced.</param>
    /// <remarks>
    ///     ⚠ <b>Only when the bytes differ.</b> An output rewritten with identical content still has
    ///     a new timestamp, and a timestamp is what every incremental build in the tool chain reads —
    ///     so an unconditional write makes the compiler rerun on every build and, worse, makes a
    ///     watcher fire on every build. This is the whole of the incrementality that MSBuild's
    ///     <c>Inputs</c>/<c>Outputs</c> cannot express, because it is about the content rather than
    ///     about the timestamps.
    /// </remarks>
    internal static void Write(StyleGenRequest request, StyleGenResult result) {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        if (request.Output is { } output) {
            WriteIfDifferent(output, result.Css);
        }

        if (request.Report is { } report) {
            WriteIfDifferent(report, Report(result));
        }

        if (request.Accessor is { } accessor) {
            WriteIfDifferent(accessor, Accessor(request, result));
        }
    }

    /// <summary>The refusal report: the news first, the prose after it.</summary>
    /// <param name="result">What the run produced.</param>
    /// <returns>The report text.</returns>
    /// <remarks>
    ///     ⚠ <b>Two sections in one file rather than two files, and the order is the point.</b> The
    ///     report was a bare list of every candidate that produced no rule, which for any real project
    ///     is several hundred English words with the occasional real class buried among them — so the
    ///     one entry worth acting on was the one nobody could find. The refusals go first, headed, and
    ///     each one names the family that was consulted, so <c>bg-clip-text</c> reads as "the family
    ///     <c>bg</c> has nothing for <c>clip-text</c>" rather than as a word this file did not know.
    ///     <para>
    ///         Headed even when a section is empty, because an unlabelled empty file is
    ///         indistinguishable from a run that did not happen.
    ///     </para>
    /// </remarks>
    internal static string Report(StyleGenResult result) {
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();

        text.Append("# ").Append(result.Unresolved.Count)
            .Append(" candidates named a registered family and emitted nothing\n");

        foreach (var refusal in result.Unresolved) {
            text.Append(refusal.Candidate).Append("\tthe family '").Append(refusal.Family).Append('\'');

            text.Append(
                refusal.Kind == UtilityRefusalKind.Variant
                    ? $" resolves; the variant '{refusal.Detail}' is not one Vixen knows"
                    : $" has no value '{refusal.Detail}'"
            );

            text.Append('\n');
        }

        text.Append("\n# ").Append(result.Unrecognised.Count)
            .Append(" candidates matched no family at all — the scanner is over-inclusive, so most are prose\n");

        foreach (var candidate in result.Unrecognised) {
            text.Append(candidate).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The generated C#: the sheet as constants, and nothing else.</summary>
    /// <remarks>
    ///     ⚠ <b>Constants rather than a file read, so that a shipped game has no build-time artefact
    ///     to deploy.</b> The <c>.vcss</c> in <c>obj/</c> is for the tools; the binary carries the
    ///     text. Split one literal per line and concatenated — string concatenation of literals is a
    ///     constant expression, so this is still a <c>const</c>, and a diff of the generated file is
    ///     readable instead of being one line ten thousand characters long.
    /// </remarks>
    internal static string Accessor(StyleGenRequest request, StyleGenResult result) {
        var text = new StringBuilder();

        text.AppendLine("// <auto-generated />");
        text.AppendLine("// Generated by Vixen.StyleGen. Do not edit: the next build overwrites it.");
        text.AppendLine("#nullable enable");
        text.AppendLine();
        text.Append("namespace ").Append(request.Namespace).AppendLine(";");
        text.AppendLine();
        text.AppendLine("/// <summary>The stylesheet this project's markup and tokens compile to.</summary>");
        text.Append(request.Public ? "public" : "internal").Append(" static class ").AppendLine(request.Class);
        text.AppendLine("{");

        text.AppendLine("    /// <summary>The utilities this project uses, in <c>@layer utilities</c>.</summary>");
        Literal(text, "Utilities", result.Utilities);
        text.AppendLine();
        text.AppendLine("    /// <summary>The whole sheet: the hand-written rules first, then the utilities.</summary>");
        Literal(text, "Css", result.Css);
        text.AppendLine();
        text.AppendLine("    /// <summary>How many utility rules were emitted.</summary>");
        text.Append("    public const int RuleCount = ").Append(result.RuleCount).AppendLine(";");

        text.AppendLine("}");

        return text.ToString();
    }

    static void Literal(StringBuilder text, string name, string value) {
        text.Append("    public const string ").Append(name).Append(" =");

        if (value.Length == 0) {
            text.AppendLine(" \"\";");
            return;
        }

        text.AppendLine();

        var lines = value.Split('\n');

        for (var index = 0; index < lines.Length; index++) {
            var last = index == lines.Length - 1;
            var line = last ? lines[index] : lines[index] + "\n";

            if (last && line.Length == 0) {
                // A trailing newline made the split produce an empty tail; the line before it
                // already carries the `\n`.
                text.Length -= 3;
                text.AppendLine(";");
                return;
            }

            text.Append("        \"").Append(Escape(line)).Append('"').AppendLine(last ? ";" : " +");
        }
    }

    static string Escape(string line) {
        var escaped = new StringBuilder(line.Length + 8);

        foreach (var c in line) {
            switch (c) {
                case '\\': escaped.Append("\\\\"); break;
                case '"': escaped.Append("\\\""); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                default: escaped.Append(c); break;
            }
        }

        return escaped.ToString();
    }

    static void WriteIfDifferent(string path, string content) {
        if (Path.GetDirectoryName(path) is { Length: > 0 } directory) {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal)) {
            return;
        }

        File.WriteAllText(path, content);
    }
}
