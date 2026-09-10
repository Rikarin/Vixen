// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Vixen.Build;

/// <summary>
///     Where a <c>StringId</c> is written in this repository's text, as a function of the text.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>CheckStrings</c> is textual on purpose</b> — a declaration is <c>Class.Member</c> at
///         every site that uses one, in C# and in <c>.vxml</c> alike, and the markup half is why a
///         Roslyn answer would have needed the generated code as well. What that costs is a type
///         name: a construction is only recognisable where the text says what is being built.
///     </para>
///     <para>
///         ⚠ <b>Three shapes now, and the third was invisible for as long as it existed</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1203">#1203</a>).
///         <c>Unavailable = new("editor.command.foliage.type-remove.unavailable", "…")</c> inside an
///         object initialiser carries neither a <c>new StringId</c> nor the declared type name the
///         initialiser pattern anchors on — the target's type is only in the semantic model. So the
///         id was in no <c>All</c> list, in no violation, in no count and in no translator's
///         template, which is the exact state the census exists to make impossible.
///     </para>
///     <para>
///         ⚠ <b>The type the text does not carry is recovered from the tree rather than guessed.</b>
///         A naive <c>\w+ = new\(\s*"</c> matches forty-nine object initialisers in this repository
///         and not one of them builds a <c>StringId</c> — <c>StartInfo = new("dotnet")</c>,
///         <c>Endpoint = new("127.0.0.1", 7777)</c>, <c>CasterStage = new("Caster")</c>. What tells
///         them apart is the tree's own declarations: <see cref="Members" /> reads every
///         <c>StringId Foo { get; }</c> and <c>StringId? Foo</c> in the repository, so
///         <c>Unavailable</c> is recognised because <c>EditorCommand</c> declares it as one and
///         <c>StartInfo</c> is not because nothing does. The member set is the semantic model this
///         census can afford.
///     </para>
///     <para>
///         ⚠ <b>Uppercase and property-or-field shaped, which is not style pedantry.</b> An object
///         initialiser assigns to a property or a field; the same regular expression relaxed to any
///         identifier picks up <c>StringId title</c> as a <em>parameter</em> name and then reports
///         <c>this.title = new(title)</c> in <c>BackgroundTask</c> and <c>EditorDocument</c>, where
///         the field is a <c>Signal&lt;string&gt;</c>. Three false positives out of four hits, from
///         one relaxed character class.
///     </para>
/// </remarks>
static class StringIdCensus {
    /// <summary>A <c>StringId</c> property or field declaration, wherever it is written.</summary>
    /// <remarks>
    ///     Both the nullable and the plain form, because <c>EditorCommand.Unavailable</c> has been
    ///     written each way and the census is about the member's name rather than its nullability.
    /// </remarks>
    static readonly Regex MemberPattern = new(
        @"\bStringId\??\s+(?<member>[A-Z]\w*)\s*(?:\{\s*get;|;|=)",
        RegexOptions.Compiled
    );

    /// <summary>Every construction of a <c>StringId</c> whose id is a literal, anywhere.</summary>
    /// <remarks>
    ///     ⚠ <b>Three shapes, and each was added after the tree had been living with what the
    ///     previous set could not see.</b> The first is the explicit <c>new StringId("…", …)</c>.
    ///     The second is the initialiser that target-types its <c>new</c> —
    ///     <c>static readonly StringId CategoryWater = new("editor.category.water", "Water");</c> —
    ///     twenty-one production ids written that way, of which <c>editor.category.scene</c> was
    ///     constructed three times in three files while the gate written to stop exactly that
    ///     reported nothing. The third is <see cref="MemberInitialiserPattern" />.
    /// </remarks>
    static readonly Regex[] LiteralIdPatterns = [
        new("""new\s+StringId\(\s*"(?<id>[^"]+)"\s*,""", RegexOptions.Compiled),
        new(
            """StringId\s+\w+\s*(?:\{\s*get;\s*\}\s*)?=\s*new\(\s*"(?<id>[^"]+)"\s*,""",
            RegexOptions.Compiled
        )
    ];

    /// <summary>The object-initialiser shape, whose target type is only in the member set.</summary>
    /// <remarks>
    ///     Matched against <see cref="Members" /> by the caller — on its own this expression matches
    ///     every object initialiser in the tree, which is why it is not simply a third entry in
    ///     <see cref="LiteralIdPatterns" />.
    /// </remarks>
    static readonly Regex MemberInitialiserPattern = new(
        """(?<member>\w+)\s*=\s*new\(\s*"(?<id>[^"]+)"\s*,""",
        RegexOptions.Compiled
    );

    /// <summary>Every construction of a <c>StringId</c>, whatever its first argument is.</summary>
    /// <remarks>
    ///     Anchored on the <c>new</c> alone rather than on a literal, because the point of that half
    ///     of the census is the constructions the literal patterns cannot see. What follows the
    ///     bracket is read separately by <see cref="LiteralFirstArgument" />.
    /// </remarks>
    static readonly Regex[] ConstructionPatterns = [
        new("""new\s+StringId\(""", RegexOptions.Compiled),
        new("""\bStringId\s+\w+\s*(?:\{\s*get;\s*\}\s*)?=\s*new\(""", RegexOptions.Compiled)
    ];

    /// <summary>The object-initialiser shape again, with no claim about the first argument.</summary>
    static readonly Regex MemberInitialiserConstructionPattern = new(
        @"(?<member>\w+)\s*=\s*new\(",
        RegexOptions.Compiled
    );

    /// <summary>A first argument that is a plain string literal and nothing else.</summary>
    static readonly Regex LiteralFirstArgument = new("""^\s*"(?:[^"\\]|\\.)*"\s*,""", RegexOptions.Compiled);

    /// <summary>Every name this repository declares as a <c>StringId</c> property or field.</summary>
    /// <param name="sources">Every source file's text.</param>
    /// <returns>The member names, which is what an object initialiser assigns to.</returns>
    /// <remarks>
    ///     ⚠ <b>Tree-wide rather than per file, and that is the whole of why it works.</b>
    ///     <c>FoliageMode.cs</c> assigns <c>Unavailable</c>; the declaration that says
    ///     <c>Unavailable</c> is a <c>StringId</c> is in <c>Vixen.Ui.Controls/EditorCommand.cs</c>,
    ///     one assembly away. A per-file answer would have missed the id this shape was added for.
    /// </remarks>
    public static HashSet<string> Members(IEnumerable<string> sources) {
        ArgumentNullException.ThrowIfNull(sources);

        var members = new HashSet<string>(StringComparer.Ordinal);

        foreach (var contents in sources) {
            foreach (Match match in MemberPattern.Matches(contents)) {
                members.Add(match.Groups["member"].Value);
            }
        }

        return members;
    }

    /// <summary>Every id one file builds out of a string literal, under any of the three shapes.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="members">Every name declared as a <c>StringId</c>, from <see cref="Members" />.</param>
    /// <returns>The id and the offset the match started at, once per construction.</returns>
    /// <remarks>
    ///     ⚠ <b>Deduplicated on where the argument list starts</b>, because a declaration written as
    ///     <c>static readonly StringId CategoryWater = new("…", "…")</c> satisfies the second shape
    ///     and the third at once. Two shapes agreeing about one site is one site.
    /// </remarks>
    public static IEnumerable<(string Id, int Index)> LiteralIds(string contents, IReadOnlySet<string> members) {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(members);

        return Matched(contents, LiteralIdPatterns, MemberInitialiserPattern, members)
            .Select(match => (match.Groups["id"].Value, match.Index));
    }

    /// <summary>Every construction of a <c>StringId</c> in one file, literal id or not.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="members">Every name declared as a <c>StringId</c>, from <see cref="Members" />.</param>
    /// <returns>Where the match started, and whether its first argument is a plain literal.</returns>
    public static IEnumerable<(int Index, bool Literal)> Constructions(
        string contents,
        IReadOnlySet<string> members
    ) {
        ArgumentNullException.ThrowIfNull(contents);
        ArgumentNullException.ThrowIfNull(members);

        return Matched(contents, ConstructionPatterns, MemberInitialiserConstructionPattern, members)
            .Select(match => (match.Index, LiteralFirstArgument.IsMatch(contents[(match.Index + match.Length)..])));
    }

    /// <summary>Whether an offset falls on a <c>///</c> line.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="index">Where the match started.</param>
    /// <returns>Whether the line it is on is a documentation comment.</returns>
    /// <remarks>
    ///     ⚠ <b>A <c>///</c> line is not a call site.</b> Three of the ids the census saw on the day
    ///     it was written were prose, and every one of the object-initialiser shape's hits on this
    ///     tree today is a remark explaining the shape — including this file's own. Nothing drifts
    ///     when prose and a declaration disagree: a reader sees both.
    /// </remarks>
    public static bool InDocComment(string contents, int index) {
        ArgumentNullException.ThrowIfNull(contents);

        var start = contents.LastIndexOf('\n', Math.Max(index - 1, 0)) + 1;

        return contents.AsSpan(start, index - start).TrimStart().StartsWith("///", StringComparison.Ordinal);
    }

    /// <summary>The union of the anchored shapes and the member-filtered one, one entry per site.</summary>
    /// <param name="contents">The file.</param>
    /// <param name="anchored">The patterns that carry the type name themselves.</param>
    /// <param name="initialiser">The pattern that does not, whose member is checked against the set.</param>
    /// <param name="members">Every name declared as a <c>StringId</c>.</param>
    /// <returns>The matches, deduplicated on where the argument list starts.</returns>
    static IEnumerable<Match> Matched(
        string contents,
        IEnumerable<Regex> anchored,
        Regex initialiser,
        IReadOnlySet<string> members
    ) {
        var seen = new HashSet<int>();
        var matches = anchored
            .SelectMany(pattern => pattern.Matches(contents))
            .Concat(
                initialiser
                    .Matches(contents)
                    .Where(match => members.Contains(match.Groups["member"].Value))
            );

        foreach (var match in matches) {
            if (seen.Add(match.Index + match.Length)) {
                yield return match;
            }
        }
    }
}
