// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     Reads a <c>PublicAPI</c> baseline line and answers with the documentation id of the type it
///     declares, or nothing when the line declares something else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This used to skip every line containing <c>-&gt;</c> as "a member, not a type", and
///         that is how this repository spells a type declaration.</b> <c>Vixen.Ecs.Archetype
///         -&gt; sealed class</c> is the declaration; the arrow separates the name from the kind
///         exactly as it separates a property from its type. The only type lines that survived the
///         skip were the ones that also name a base or an interface — <c>X : System.IDisposable</c>
///         — so the check that reads this saw <b>2 398 of the 4 711 types</b> in the committed
///         baselines and reported success over the other 2 313. Reading them finds two public types
///         with neither a guide page nor an exemption, on a tree whose cheap documentation guard had
///         been printing <c>nought uncovered</c>.
///     </para>
///     <para>
///         That is the failure shape CLAUDE.md names first, in its quietest form: not a check that
///         was never wired up, but one wired to half its subject, whose output — a count — looks the
///         same either way. The floor in <see cref="Build.BaselinedTypes" /> is what keeps the other
///         version of it honest, and it passed at 1 000 while the true number was more than four
///         times that.
///     </para>
///     <para>
///         Kept dependency-free and outside <c>Build</c> so that
///         <c>Tools/Vixen.ApiCheck.Tests</c> can compile the same source rather than a copy of it:
///         <c>build/_build.csproj</c> is not in <c>Vixen.slnx</c> and no suite in the tree tests it,
///         which is the same reason <c>AotProbeProjectFile</c> lives beside this file.
///     </para>
/// </remarks>
static class PublicApiTypeNames {
    /// <summary>
    ///     The words a type declaration's right-hand side is made of. A line whose right-hand side
    ///     is anything else — <c>int</c>, <c>Vixen.Core.Symbol</c>, <c>string?</c> — is a property or
    ///     a field, and its left-hand side is a member name rather than a type's.
    /// </summary>
    /// <remarks>
    ///     ⚠ Closed rather than "anything without a dot", because <c>-&gt; string</c> and
    ///     <c>-&gt; int</c> have no dot either. The set is every kind this repository's baselines
    ///     actually spell — measured across all 132 of them — and an unrecognised one drops the type
    ///     rather than admitting a member, which fails the reader's own floor instead of quietly
    ///     documenting a getter.
    /// </remarks>
    static readonly HashSet<string> KindWords = [
        "sealed", "static", "abstract", "readonly", "ref", "partial", "unsafe",
        "class", "struct", "interface", "enum", "record", "delegate",
    ];

    /// <summary>The type ids a <c>PublicAPI</c> baseline's lines declare.</summary>
    /// <remarks>
    ///     Here rather than at the one call site so that a test can ask the same question of the
    ///     same code. A removal marker is a type that <em>was</em> declared, and documenting it
    ///     would be documenting something the assembly no longer has.
    /// </remarks>
    public static IEnumerable<string> BaselinedIds(IEnumerable<string> lines) =>
        lines
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#')
                && !line.StartsWith("*REMOVED*", StringComparison.Ordinal))
            .Select(DocumentationId)
            .OfType<string>();

    /// <summary>The type ids <c>docs/DocsExempt.txt</c> excuses.</summary>
    /// <remarks>
    ///     The reason after the id is not optional and is not read here: what makes the file a
    ///     baseline rather than a mute button is that a person wrote one and another read it.
    /// </remarks>
    public static IEnumerable<string> ExemptedIds(IEnumerable<string> lines) =>
        lines
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("T:", StringComparison.Ordinal))
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0]["T:".Length..]);

    /// <summary>The type ids a guide page names in the <c>api:</c> list of its front matter.</summary>
    /// <remarks>
    ///     Only above the first <c>##</c>: an <c>api:</c> line further down is prose about a page's
    ///     own format rather than that page's claim to document a type.
    /// </remarks>
    public static IEnumerable<string> PageIds(IEnumerable<string> lines) =>
        lines
            .TakeWhile(line => !line.StartsWith("##", StringComparison.Ordinal))
            .Select(line => Regex.Match(line, @"^api:\s*\[(?<ids>[^\]]*)\]"))
            .Where(match => match.Success)
            .SelectMany(match => match.Groups["ids"].Value.Split(','))
            .Select(id => id.Trim())
            .Where(id => id.StartsWith("T:", StringComparison.Ordinal))
            .Select(id => id["T:".Length..]);

    /// <summary>
    ///     A documentation id as <c>DocsExempt.txt</c> and a page's <c>api:</c> list spell it, or
    ///     <c>null</c> when the line is not a type declaration.
    /// </summary>
    /// <remarks>
    ///     Three shapes are a type: <c>Namespace.Type -&gt; sealed class</c>,
    ///     <c>Namespace.Type : Base</c> — the analyzer writes the base list on its own line — and
    ///     <c>Namespace.Type -&gt; enum : byte</c>, whose tail is stripped with the base list. A
    ///     member is anything with parentheses, and anything whose arrow points at a type rather
    ///     than at a kind.
    /// </remarks>
    public static string? DocumentationId(string line) {
        var declaration = line.Split(" : ", StringSplitOptions.None)[0].Trim();

        // `Type.Method(...)` and `Type.this[int index].get -> T`, whose arrow points at a type.
        if (declaration.Length == 0 || line.Contains('(')) {
            return null;
        }

        var arrow = declaration.IndexOf("->", StringComparison.Ordinal);

        if (arrow >= 0) {
            var kind = declaration[(arrow + 2)..].Trim();

            // `Vixen.Ecs.Chunk.Count.get -> int` — the arrow points at a type, so the left is a
            // member. `const X.Y = 0 -> string` and `static X.M -> T` are members with a modifier,
            // which the space in their left-hand side gives away.
            if (kind.Length == 0 || !kind.Split(' ').All(KindWords.Contains)) {
                return null;
            }

            declaration = declaration[..arrow].Trim();

            // ⚠ Before the argument list, not across it: `SmallList<T, TBuffer>` has a space in it
            // and is a type, where `const X.Y = 0` has one and is not.
            var name = declaration.Split('<')[0];

            if (name.Contains(' ', StringComparison.Ordinal)) {
                return null;
            }
        }

        // A bare namespace or a modifier line has nothing to document.
        return declaration.Contains('.', StringComparison.Ordinal) ? Mangled(declaration) : null;
    }

    /// <summary>
    ///     A generic type carries its parameters by name where a documentation id carries their
    ///     count — <c>SmallList&lt;T, TBuffer&gt;</c> against <c>SmallList`2</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every argument list rather than a trailing one, because a nested type is spelled
    ///     <c>ChunkedArray&lt;T&gt;.Enumerator</c> and its id is <c>ChunkedArray`1.Enumerator</c>. A
    ///     regex anchored at the end of the string leaves that one unmangled, and getting it wrong
    ///     is invisible in the direction that matters: the mangled name simply never matches, and a
    ///     type with a page reads as undocumented.
    /// </remarks>
    static string Mangled(string declaration) {
        if (!declaration.Contains('<', StringComparison.Ordinal)) {
            return declaration;
        }

        var mangled = new StringBuilder();
        var depth = 0;
        var arguments = 0;

        foreach (var character in declaration) {
            switch (character) {
                case '<':
                    depth++;

                    if (depth == 1) {
                        arguments = 1;
                    }

                    break;

                case '>':
                    depth--;

                    if (depth == 0) {
                        mangled.Append('`').Append(arguments);
                    }

                    break;

                case ',' when depth == 1:
                    arguments++;

                    break;

                default:
                    if (depth == 0) {
                        mangled.Append(character);
                    }

                    break;
            }
        }

        return mangled.ToString();
    }
}
