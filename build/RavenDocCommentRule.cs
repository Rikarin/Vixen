// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vixen.Build;

/// <summary>
///     <see cref="DocCommentRule" />'s question asked of a <c>.rvn</c>, which no compiler in this
///     repository parses.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>CheckDocComments</c> parses C#, and Raven has the same <c>///</c> doc comments</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1076">#1076</a>).
///         <c>Raven/Library</c> and the four <c>Shaders/</c> folders hold 176 files between them and
///         nothing in this repository could see a block stapled above the wrong <c>func</c> in one of
///         them. It is not theoretical: the commit that quoted that blind spot as a warning then
///         inserted <c>Lift</c> between <c>Blend.Combine</c>'s doc block and <c>Combine</c>, so a
///         block describing sixteen blend modes and their neutrals described a two-line widening
///         helper.
///     </para>
///     <para>
///         ⚠ <b>The rule #1076 proposed — "a block whose prose names a different <c>func</c> in the
///         same file" — was measured and refused.</b> It reports 49 findings on today's tree and
///         every one sampled is a deliberate cross-reference (<c>Lift</c>'s block says what
///         <c>Combine</c> does with its fourth lane, on purpose). It also misses the defect it was
///         proposed for: <c>Combine</c>'s old summary named no function at all. A rule with that
///         ratio is <see cref="DocCommentRule" />'s rejected regular-expression draft again.
///     </para>
///     <para>
///         <b>What is checkable without a type system is where the block <em>ends</em>.</b> Raven doc
///         comments are prose rather than XML, so there is no second <c>&lt;summary&gt;</c> to count —
///         but two blocks spliced into one leave a mark that a compiler would not: a line that
///         finishes a sentence well short of the wrap column, followed by a line that starts a new
///         one, with no <c>///</c> separator between them. Every real paragraph break in this tree is
///         written with that separator. <see cref="Splice" /> is that, and it fires on the pre-fix
///         <c>Blend.rvn</c> text.
///     </para>
///     <para>
///         ⚠ <b>And it fires on eight places in a tree everybody believed was clean, two of which are
///         live staples</b> — <c>IIrradianceSource</c>'s block sat on <c>struct IrradianceSample</c>
///         with the protocol left undocumented, and <c>CascadeTile</c>'s sat on
///         <c>CascadeContaining</c> ninety lines above the function it describes. Those are the
///         <c>KeyChord.MacFormat</c> defect in a shader, and they are why this is a gate rather than a
///         lint.
///     </para>
/// </remarks>
static class RavenDocCommentRule {
    /// <summary>How far short of the wrap column a line has to stop to be a paragraph's last.</summary>
    /// <remarks>
    ///     ⚠ <b>Measured, not chosen.</b> Swept over the 2 349 doc comment lines this tree holds, the
    ///     finding count falls 14 · 11 · 10 · 8 · 8 as this number goes 12 · 14 · 16 · 18 · 20 — a
    ///     plateau at eighteen, which is where the paragraph breaks stop and the hand-wrapped lines
    ///     that merely stopped a word early begin. The two live staples it catches sit 54 and 90
    ///     columns short and the pre-fix <c>Blend.rvn</c> splice 41, so the threshold is nowhere near
    ///     any of them. The first draft asked "would the next line's first word have fitted here" —
    ///     principled, and 41 findings, because a hand-wrapped paragraph routinely leaves a word's
    ///     worth of room.
    /// </remarks>
    const int WrapSlack = 18;

    /// <summary>What a Raven declaration starts with, after its attributes.</summary>
    /// <remarks>
    ///     Taken from the tree rather than from the grammar: these are every token that follows a doc
    ///     comment in the 176 committed <c>.rvn</c> files. A keyword this misses turns a documented
    ///     declaration into a reported orphan, which is a loud failure rather than a quiet one.
    /// </remarks>
    static readonly string[] DeclarationKeywords = [
        "compose",
        "const",
        "enum",
        "extern",
        "func",
        "groupshared",
        "import",
        "impl",
        "interface",
        "override",
        "package",
        "protocol",
        "shader",
        "static",
        "stream",
        "struct",
        "val",
        "var"
    ];

    /// <summary>Directory fragments no walk of this repository should read.</summary>
    /// <remarks>
    ///     <see cref="DocCommentRule.Sources" />'s list, for its reasons — <c>.claude/worktrees</c>
    ///     holds a whole checkout per agent, and a walk that does not stop at the repository's edge
    ///     reports another session's shaders by their worktree path.
    /// </remarks>
    static readonly string[] SkippedFragments = ["/bin/", "/obj/", "/artifacts/"];

    /// <summary>The other checkouts of this repository, and only the other ones.</summary>
    /// <remarks>
    ///     ⚠ Anchored at the root and never matched as a substring. An agent's own root <em>is</em>
    ///     <c>…/.claude/worktrees/&lt;name&gt;</c>, so a substring test excludes the whole tree and the
    ///     rule then reports a clean repository having read nothing — the mistake
    ///     <see cref="DocCommentRule" /> records making on its first run.
    /// </remarks>
    static readonly string[] SkippedRoots = [".claude/", ".git/"];

    /// <summary>A line that carries doc comment prose, and what the prose is.</summary>
    static readonly Regex DocLine = new(@"^\s*///(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>A <c>func</c> declaration, with the parameter list a block may name into.</summary>
    static readonly Regex Function = new(@"\bfunc\s+(?<name>[A-Za-z_]\w*)\s*\((?<parameters>[^)]*)\)", RegexOptions.Compiled);

    /// <summary>The XML parameter tag, which one library shader uses and a staple would misname.</summary>
    static readonly Regex ParamTag = new(@"<param\s+name=""(?<name>[^""]+)""", RegexOptions.Compiled);

    /// <summary>Attribute groups a declaration may wear before its keyword.</summary>
    static readonly Regex Attributes = new(@"^(\[[^\]]*\]\s*)+", RegexOptions.Compiled);

    /// <summary>A line that opens a new sentence rather than continuing one.</summary>
    static readonly Regex OpensSentence = new(@"^[⚠A-Z`*_]", RegexOptions.Compiled);

    /// <summary>A line that finishes a sentence, allowing for the markup that trails one.</summary>
    static readonly Regex ClosesSentence = new(@"[.!?][)*`_\]""]*$", RegexOptions.Compiled);

    /// <summary>Where the files this rule is not asked about today are listed.</summary>
    /// <remarks>
    ///     Its own list rather than <see cref="DocCommentRule.ExemptionsPath" />, because each list is
    ///     asserted to hold nothing but files its own sweep flags — a shared file would report every
    ///     line of one sweep as stale to the other. It ships empty: all eight findings the rule made
    ///     on the day it was written were fixed in the same commit.
    /// </remarks>
    public const string ExemptionsPath = "docs/RavenDocCommentExempt.txt";

    /// <summary>Every Raven source under a root that this rule is asked about.</summary>
    /// <param name="root">The repository root to walk.</param>
    /// <returns>Absolute paths with forward slashes, ordered.</returns>
    public static List<string> Sources(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var normalised = root.Replace('\\', '/').TrimEnd('/');

        return Directory
            .EnumerateFiles(normalised, "*.rvn", SearchOption.AllDirectories)
            .Select(path => path.Replace('\\', '/'))
            .Where(path => !SkippedFragments.Any(fragment => path.Contains(fragment, StringComparison.Ordinal)))
            .Where(path => !SkippedRoots.Any(directory => path.StartsWith($"{normalised}/{directory}", StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The files this rule is not asked about today.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Repository-relative paths with forward slashes.</returns>
    public static HashSet<string> Exemptions(string root) {
        ArgumentNullException.ThrowIfNull(root);

        var file = Path.Combine(root, ExemptionsPath.Replace('/', Path.DirectorySeparatorChar));

        return File.Exists(file)
            ? File
                .ReadAllLines(file)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>How many doc comment blocks one file holds.</summary>
    /// <param name="text">The file's Raven.</param>
    /// <returns>The number of <c>///</c> runs.</returns>
    /// <remarks>
    ///     ⚠ <b>The instrument, and the only number that separates "this tree is clean" from "the run
    ///     splitter stopped splitting".</b> A caller asserts the whole tree's total, the way
    ///     <c>CheckDocComments</c> asserts its file count — a sweep that reads no blocks reports no
    ///     findings, and no findings is what success looks like.
    /// </remarks>
    public static int Blocks(string text) {
        ArgumentNullException.ThrowIfNull(text);

        return Runs(Lines(text)).Count;
    }

    /// <summary>Everything wrong with the doc comments in one Raven file.</summary>
    /// <param name="file">The name to report findings against.</param>
    /// <param name="text">The file's Raven.</param>
    /// <returns>One finding per problem, in source order; empty when the file is clean.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Three questions, none of them needing a type system.</b> Does the block head a
    ///         declaration at all; does it splice two paragraphs with no separator between them; does
    ///         an XML <c>&lt;param&gt;</c> name a parameter the <c>func</c> below does not have.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The third fires nowhere today and is kept anyway</b>, because it is the one check
    ///         <see cref="DocCommentRule" /> and this rule share, and one library shader
    ///         (<c>SpecularModels.GgxAnisotropic</c>) does use the tag. A rule that is only ever
    ///         exercised by its fixture is still a rule; a rule with no fixture is decoration.
    ///     </para>
    /// </remarks>
    public static List<DocCommentRule.Finding> Check(string file, string text) {
        ArgumentNullException.ThrowIfNull(text);

        var lines = Lines(text);
        var wrap = lines.Where(line => DocLine.IsMatch(line)).Select(line => line.TrimEnd().Length).DefaultIfEmpty(0).Max();
        List<DocCommentRule.Finding> findings = [];

        foreach (var run in Runs(lines)) {
            findings.AddRange(Orphan(file, lines, run));
            findings.AddRange(Splice(file, lines, run, wrap));
            findings.AddRange(Parameters(file, lines, run));
        }

        return findings.OrderBy(finding => finding.Line).ToList();
    }

    /// <summary>The file's lines, with no trailing carriage returns.</summary>
    /// <param name="text">The file's Raven.</param>
    /// <returns>One entry per line.</returns>
    static List<string> Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();

    /// <summary>Every run of consecutive <c>///</c> lines, as half-open index ranges.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <returns>One entry per doc comment block.</returns>
    static List<(int Start, int End)> Runs(List<string> lines) {
        List<(int, int)> runs = [];

        for (var index = 0; index < lines.Count;) {
            if (!DocLine.IsMatch(lines[index])) {
                index++;

                continue;
            }

            var start = index;

            while (index < lines.Count && DocLine.IsMatch(lines[index])) {
                index++;
            }

            runs.Add((start, index));
        }

        return runs;
    }

    /// <summary>A block that heads no declaration at all.</summary>
    /// <param name="file">The name to report against.</param>
    /// <param name="lines">The file's lines.</param>
    /// <param name="run">The block.</param>
    /// <returns>At most one finding.</returns>
    /// <remarks>
    ///     ⚠ <b>Reported here and deliberately not in C#.</b> <see cref="DocCommentRule" /> leaves an
    ///     unattached block alone because Roslyn attaches one to whatever token follows and the
    ///     interesting case cannot be told from a comment before a closing brace. Raven's is simpler:
    ///     a <c>///</c> run whose next line is blank, or a <c>}</c>, documents nothing — the shape a
    ///     deleted or moved declaration leaves behind.
    /// </remarks>
    static IEnumerable<DocCommentRule.Finding> Orphan(string file, List<string> lines, (int Start, int End) run) {
        var next = Declaration(lines, run.End);
        var keyword = next.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

        if (DeclarationKeywords.Contains(keyword.TrimEnd(':'), StringComparer.Ordinal)) {
            yield break;
        }

        yield return new(
            file,
            run.Start + 1,
            "this doc comment block is followed by "
            + (next.Length == 0 ? "a blank line" : $"`{Trim(next)}`")
            + " rather than a declaration, so it documents nothing. A block whose declaration moved away "
            + "describes whatever is read next instead."
        );
    }

    /// <summary>Two paragraphs spliced into one block with no separator between them.</summary>
    /// <param name="file">The name to report against.</param>
    /// <param name="lines">The file's lines.</param>
    /// <param name="run">The block.</param>
    /// <param name="wrap">The file's longest doc comment line, which is its wrap column.</param>
    /// <returns>One finding per splice.</returns>
    /// <remarks>
    ///     ⚠ <b>This is the whole gate, and what makes it one is <paramref name="wrap" />.</b> A line
    ///     that ends a sentence *at* the wrap column ran out of room and the paragraph continues; one
    ///     that ends a sentence <see cref="WrapSlack" /> columns short chose to stop, and a following
    ///     line that opens a new sentence is then a new paragraph written without the <c>///</c>
    ///     separator every other paragraph in this tree has. That is what a block pasted onto the end
    ///     of another block looks like.
    /// </remarks>
    static IEnumerable<DocCommentRule.Finding> Splice(string file, List<string> lines, (int Start, int End) run, int wrap) {
        for (var index = run.Start; index < run.End - 1; index++) {
            var current = DocLine.Match(lines[index]).Groups["text"].Value.Trim();
            var following = DocLine.Match(lines[index + 1]).Groups["text"].Value.Trim();

            if (current.Length == 0 || following.Length == 0) {
                continue;
            }

            if (!ClosesSentence.IsMatch(current) || !OpensSentence.IsMatch(following)) {
                continue;
            }

            if (lines[index].TrimEnd().Length > wrap - WrapSlack) {
                continue;
            }

            yield return new(
                file,
                index + 1,
                $"`{Trim(current)}` ends a paragraph {wrap - lines[index].TrimEnd().Length} columns short of this "
                + $"file's wrap column, and `{Trim(following)}` starts a new one on the very next line with no `///` "
                + "separator. Two blocks have been spliced: one of them describes a declaration that is no longer "
                + "below it."
            );
        }
    }

    /// <summary>An XML <c>&lt;param&gt;</c> naming a parameter the function below does not have.</summary>
    /// <param name="file">The name to report against.</param>
    /// <param name="lines">The file's lines.</param>
    /// <param name="run">The block.</param>
    /// <returns>One finding per misnamed or repeated parameter.</returns>
    static IEnumerable<DocCommentRule.Finding> Parameters(string file, List<string> lines, (int Start, int End) run) {
        List<string> documented = [];

        for (var index = run.Start; index < run.End; index++) {
            documented.AddRange(ParamTag.Matches(lines[index]).Select(match => match.Groups["name"].Value));
        }

        if (documented.Count == 0) {
            yield break;
        }

        foreach (var duplicate in documented.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1)) {
            yield return new(file, run.Start + 1, $"this doc comment block documents the parameter `{duplicate.Key}` {duplicate.Count()} times.");
        }

        // ⚠ Through the same attribute skip the orphan check uses, and not `lines[run.End]`. The one
        // shader in this tree that documents a parameter has no attributes on the function; the first
        // one that does would otherwise have silently stopped being checked.
        var owner = Function.Match(Declaration(lines, run.End));

        if (!owner.Success) {
            yield break;
        }

        var declared = owner
            .Groups["parameters"]
            .Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(parameter => parameter.Split(':')[0].Trim())
            .Where(parameter => parameter.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in documented.Distinct(StringComparer.Ordinal).Where(name => !declared.Contains(name))) {
            yield return new(
                file,
                run.Start + 1,
                $"this doc comment block documents a parameter `{name}`, which `{owner.Groups["name"].Value}` does "
                + $"not have. Its parameters are: {string.Join(", ", declared.Order(StringComparer.Ordinal))}."
            );
        }
    }

    /// <summary>The declaration line a doc comment block heads, with its attributes stripped.</summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="start">The line just past the block.</param>
    /// <returns>The declaration's text, or empty when the block heads none.</returns>
    /// <remarks>
    ///     An attribute may sit on its own line or share the declaration's, and both shapes are in the
    ///     tree — <c>[VertexShader]</c> alone above <c>[Semantic("SV_Position")] func Vertex(…)</c>,
    ///     and <c>[PushConstant] var viewProjection: mat4</c> on one line.
    /// </remarks>
    static string Declaration(List<string> lines, int start) {
        var index = start;

        while (index < lines.Count) {
            var stripped = Attributes.Replace(lines[index].Trim(), string.Empty);

            if (stripped.Length > 0 || lines[index].Trim().Length == 0) {
                return stripped;
            }

            index++;
        }

        return string.Empty;
    }

    /// <summary>A fragment of prose, short enough to sit inside a one-line message.</summary>
    /// <param name="text">The line.</param>
    /// <returns>Its first sixty characters, elided.</returns>
    static string Trim(string text) => text.Length <= 60 ? text : $"{text[..57]}…";
}
