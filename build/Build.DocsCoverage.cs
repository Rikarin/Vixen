// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System;
using System.Collections.Generic;
using System.Linq;
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
///         all. Measured on this tree: <b>4 711</b> baselined types against 5 888 ids with a page or
///         an exemption, and two uncovered when the reader was fixed.
///     </para>
///     <para>
///         ⚠ <b>The "2 368 baselined types" this paragraph used to quote was the defect, not the
///         tree.</b> <see cref="PublicApiTypeNames.DocumentationId" /> skipped every line containing
///         <c>-&gt;</c> as a member, and <c>Vixen.Ecs.Archetype -&gt; sealed class</c> is how this
///         repository's baselines spell a type — so the only types this saw were the ones that
///         additionally name a base or an interface on a line of their own. Half the subject, a
///         count that looks like an answer either way, and a floor set at 1 000 that a 51 % reader
///         cleared twice over. Reading the other 2 313 found two public types with neither a page
///         nor an exemption on a tree this had been calling <em>nought uncovered</em>.
///     </para>
///     <para>
///         ⚠ <b>An early warning and never the gate</b>, for the reason <see cref="AffectedTests" />
///         records about itself — and the blind spot that remains is bigger than "some of the
///         solution": this sees only the projects that keep a <c>PublicAPI</c> baseline, which is
///         132 of the 421 <c>.csproj</c> in the tree, while <see cref="CheckDocs" /> sees every
///         public type in the graph. ⚠ <b>So a green run here is not "the commonest way to red
///         CheckDocs has not happened", which is what this used to claim.</b> Of the six types that
///         redded <c>CheckDocs</c> on 2026-09-03, four were in baselined projects and this would
///         have named them; the other two were in <c>Vixen.Editor.Assets</c>, which packs, keeps no
///         baseline, and is one of the thirty-one projects <c>build/ApiUncovered.txt</c> enumerates
///         for exactly this reason (#641). The honest claim is narrower and still worth having: a
///         type in a baselined project that nobody documented is named here in a second, before the
///         push, instead of by an eleven-minute sweep afterwards. #686 carries the rest.
///     </para>
/// </remarks>
partial class Build {
    /// <summary>Every type name the <c>PublicAPI</c> baselines declare, as documentation ids.</summary>
    IReadOnlyCollection<string> BaselinedTypes() {
        var baselines = TrackedFiles()
            .Where(path => path.Name is "PublicAPI.Shipped.txt" or "PublicAPI.Unshipped.txt")
            .ToList();

        var types = PublicApiTypeNames
            .BaselinedIds(baselines.SelectMany(baseline => baseline.ReadAllLines()))
            .ToHashSet(StringComparer.Ordinal);

        // ⚠ The instrument, and it passed for a week while reading half its subject. A reader that
        // stops matching returns the empty set, and the empty set is a gate that passes instantly
        // for ever — but a reader that matches half returns a plausible number, and this one did:
        // the floor sat at 1 000 against a true 4 711, so dropping every `X -> sealed class` line
        // and keeping only the ones that also name a base cost 2 313 types and cleared the floor by
        // a factor of two. A floor is a "did this read anything at all" check, so it is set below
        // the tree and above the failure it has actually had.
        Assert.True(
            baselines.Count > 0 && types.Count > 3500,
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
        var exemptions = PublicApiTypeNames.ExemptedIds((RootDirectory / "docs" / "DocsExempt.txt").ReadAllLines());

        var pages = TrackedFiles()
            .Where(path => path.Extension == ".md" && path.ToString().Contains("/docs/", StringComparison.Ordinal))
            .SelectMany(page => PublicApiTypeNames.PageIds(page.ReadAllLines()));

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
