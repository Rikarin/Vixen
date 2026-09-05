// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

/// <summary>
///     The half of <see cref="CheckDocs" /> that can be answered from committed text in a second,
///     rather than from a Release build of the solution in eleven minutes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A subsystem landing with public types and no <c>DocsExempt.txt</c> line has redded
///         <see cref="CheckDocs" /> four times in one week</b> (#480, and commit 75e113bf before
///         it). Every one of those was found by a full gate sweep and fixed afterwards by somebody
///         who had not written the types and did not know what four of them were for — which is the
///         opposite of what <c>DocsExempt.txt</c>'s own header asks for: "a new public type is
///         written about in the commit that adds it, which is the one commit where its author knows
///         what it is for."
///     </para>
///     <para>
///         <b>The signal was already in the tree and free.</b> A new public type cannot be committed
///         without a line in a <c>PublicAPI.Unshipped.txt</c> — that is what
///         <see cref="CheckApi" />'s analyzer is for — so the type names are sitting in committed
///         text before <c>Vixen.DocGen</c> ever sees an assembly. Cross-checking those against
///         <c>docs/DocsExempt.txt</c> and the <c>api:</c> lists of the guide pages needs no build at
///         all. Measured on this tree: 2 368 baselined types, 3 634 exempted, 2 135 with a page, and
///         <b>nought</b> uncovered — so the two universes agree exactly today, which is what makes
///         the cheap question worth asking at all.
///     </para>
///     <para>
///         ⚠ <b>An early warning and never the gate</b>, for the reason <see cref="AffectedTests" />
///         records about itself. This sees only the projects that keep a <c>PublicAPI</c> baseline,
///         and <see cref="CheckDocs" /> sees every public type in the graph — so a green run here is
///         not a claim that <c>CheckDocs</c> is green. It is a claim that the commonest way to red
///         it has not happened, available before the push rather than after the sweep.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>
    ///     A documentation id as <c>DocsExempt.txt</c> and a page's <c>api:</c> list spell it, from a
    ///     <c>PublicAPI</c> declaration.
    /// </summary>
    /// <remarks>
    ///     Two rewrites and no more. A baseline line is <c>Namespace.Type</c> or
    ///     <c>Namespace.Type : Base</c>, and a generic one carries its parameters by name where a
    ///     documentation id carries their count — <c>SmallList&lt;T, TBuffer&gt;</c> against
    ///     <c>SmallList`2</c>. ⚠ Getting that second one wrong is invisible in the direction that
    ///     matters: the mangled name simply never matches, and 34 generic types read as undocumented
    ///     on a tree where every one of them has a page.
    /// </remarks>
    static string? DocumentationId(string line) {
        var declaration = line.Split(" : ", StringSplitOptions.None)[0].Trim();

        // A member, not a type: `Type.Member -> T`, `Type.Method(...)`, `Type.Field = 0 -> T`.
        if (declaration.Length == 0 || line.Contains("->", StringComparison.Ordinal) || line.Contains('(')) {
            return null;
        }

        // A bare namespace or a modifier line has nothing to document.
        if (!declaration.Contains('.', StringComparison.Ordinal)) {
            return null;
        }

        var generic = Regex.Match(declaration, @"^(?<name>[^<]+)<(?<arguments>.*)>$");

        return generic.Success
            ? $"{generic.Groups["name"].Value}`{generic.Groups["arguments"].Value.Count(character => character == ',') + 1}"
            : declaration;
    }

    /// <summary>Every type name the <c>PublicAPI</c> baselines declare, as documentation ids.</summary>
    IReadOnlyCollection<string> BaselinedTypes() {
        var baselines = TrackedFiles()
            .Where(path => path.Name is "PublicAPI.Shipped.txt" or "PublicAPI.Unshipped.txt")
            .ToList();

        var types = baselines
            .SelectMany(baseline => baseline.ReadAllLines())
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#') && !line.StartsWith("*REMOVED*", StringComparison.Ordinal))
            .Select(DocumentationId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ The instrument. A reader that stops matching returns the empty set, and the empty set
        // is a gate that passes instantly for ever — the exact shape CLAUDE.md names first. The
        // floor is a "did this read anything at all" check and deliberately not a target: this tree
        // has 132 baselined projects and 2 368 types, and any plausible future has hundreds.
        Assert.True(
            baselines.Count > 0 && types.Count > 1000,
            $"Read {types.Count} type(s) out of {baselines.Count} PublicAPI baseline(s), which is too "
            + "few to be this repository. The baseline format has moved and DocumentationId no longer "
            + "parses it, so this check is passing by reading nothing."
        );

        return types;
    }

    /// <summary>
    ///     The types <c>DocsExempt.txt</c> excuses, and the types a guide page names in its
    ///     <c>api:</c> list.
    /// </summary>
    IReadOnlyCollection<string> DocumentedTypes() {
        var exemptions = (RootDirectory / "docs" / "DocsExempt.txt")
            .ReadAllLines()
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("T:", StringComparison.Ordinal))
            .Select(line => line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries)[0]["T:".Length..]);

        var pages = TrackedFiles()
            .Where(path => path.Extension == ".md" && path.ToString().Contains("/docs/", StringComparison.Ordinal))
            .SelectMany(page => page.ReadAllLines().TakeWhile(line => !line.StartsWith("##", StringComparison.Ordinal)))
            .Select(line => Regex.Match(line, @"^api:\s*\[(?<ids>[^\]]*)\]"))
            .Where(match => match.Success)
            .SelectMany(match => match.Groups["ids"].Value.Split(','))
            .Select(id => id.Trim())
            .Where(id => id.StartsWith("T:", StringComparison.Ordinal))
            .Select(id => id["T:".Length..]);

        return exemptions.Concat(pages).ToHashSet(StringComparer.Ordinal);
    }

    Target CheckDocsCoverage => definition => definition
        .Description("Fails, without building anything, when a type in a PublicAPI baseline has no guide page and no DocsExempt.txt line")
        .Executes(() => {
                var baselined = BaselinedTypes();
                var documented = DocumentedTypes();

                var uncovered = baselined
                    .Where(type => !documented.Contains(type))
                    .OrderBy(type => type, StringComparer.Ordinal)
                    .ToList();

                Log.Information(
                    "{Baselined} baselined type(s) against {Documented} with a page or an exemption.",
                    baselined.Count,
                    documented.Count
                );

                Assert.True(
                    uncovered.Count == 0,
                    $"{uncovered.Count} public type(s) have neither a guide page naming them in `api:` "
                    + $"nor a line in docs/DocsExempt.txt:{Environment.NewLine}  T:"
                    + string.Join($"{Environment.NewLine}  T:", uncovered)
                    + $"{Environment.NewLine}Write the page in this commit — its author is the one person "
                    + "who knows what the type is for — or add a line with a reason. CheckDocs is the "
                    + "gate and will say the same thing eleven minutes later."
                );
            }
        );
}
