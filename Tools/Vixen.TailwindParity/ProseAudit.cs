// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace Vixen.TailwindParity;

/// <summary>Checks the counts doc 43's prose states against the artefacts that measure them.</summary>
/// <remarks>
///     <para>
///         <b>Written because the batch that made these numbers measurable left one of them
///         asserted.</b> Three commits in a row edited <c>docs/plan/43-web-styling-parity.md</c>:
///         the first wrote "163 of v4's 890 static utilities are named by no row", the second shrank
///         <c>docs/plan/43-web-styling-unlisted.txt</c> to 150 lines and did not revisit the prose,
///         the third edited the same document again and did not either. Nothing could see it —
///         <see cref="ParityAudit" /> reads the <c>.txt</c> and never the <c>.md</c> — so the
///         document whose whole point is that its numbers are measured carried one that was thirteen
///         out.
///     </para>
///     <para>
///         ⚠ <b>A missing claim is a finding too, and that is the half worth writing down.</b> A
///         regex gate over prose has one failure mode that matters: somebody rewords the sentence,
///         the pattern stops matching, and the check goes green while measuring nothing — the
///         "comparator that called three empty manifests identical" shape. So each claim asserts
///         <i>exactly one</i> match, and a sentence that has been reworded fails as
///         <c>TWP013</c> until the pattern here is brought with it.
///     </para>
///     <para>
///         ⚠ <b>Only claims that name a number this tool already holds are gated.</b> Doc 43 is
///         forty thousand words of prose and most of its numbers come from suites elsewhere; the
///         three here are the ones whose source is a file in <see cref="RepositoryFiles" />, so the
///         gate can be right rather than merely strict.
///     </para>
/// </remarks>
static partial class ProseAudit {
    /// <summary>Compares the document's stated counts with the measured ones.</summary>
    /// <param name="document">Doc 43's Markdown, as text.</param>
    /// <param name="registry">The committed snapshot.</param>
    /// <param name="unlisted">The static utilities no ledger row names.</param>
    /// <param name="unsupported">The variants the engine does not resolve.</param>
    /// <returns>Every disagreement, in a stable order.</returns>
    public static IReadOnlyList<ParityFinding> Run(
        string document,
        TailwindRegistry registry,
        IReadOnlyCollection<string> unlisted,
        IReadOnlyCollection<string> unsupported
    ) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(unlisted);
        ArgumentNullException.ThrowIfNull(unsupported);

        var findings = new List<ParityFinding>();

        Check(
            findings,
            document,
            UnlistedHeadline(),
            "the unlisted-statics headline",
            [
                ("statics no row names", unlisted.Count, RepositoryFiles.UnlistedName),
                ("v4 static utilities", registry.StaticRoots.Length, RepositoryFiles.RegistryName)
            ]
        );

        Check(
            findings,
            document,
            UnlistedExitCriterion(),
            "exit criterion 1's unlisted-statics count",
            [("statics no row names", unlisted.Count, RepositoryFiles.UnlistedName)]
        );

        Check(
            findings,
            document,
            OwedVariants(),
            "the owed-variants headline",
            [
                ("variants Vixen does not resolve", unsupported.Count, RepositoryFiles.VariantsUnsupportedName),
                ("v4 variants", registry.Variants.Length, RepositoryFiles.RegistryName)
            ]
        );

        return findings;
    }

    /// <summary>Asserts one claim matches once and that each of its numbers is the measured one.</summary>
    /// <param name="findings">Where a disagreement goes.</param>
    /// <param name="document">Doc 43's Markdown.</param>
    /// <param name="pattern">The claim's shape, with one capture group per number.</param>
    /// <param name="claim">What the sentence is called in a failure.</param>
    /// <param name="expected">What each group ought to be, and where the truth lives.</param>
    static void Check(
        List<ParityFinding> findings,
        string document,
        Regex pattern,
        string claim,
        (string What, int Count, string Source)[] expected
    ) {
        var matches = pattern.Matches(document);

        // ⚠ Not `!= 1` folded into the loop below: a claim that has been reworded away matches zero
        // times, and a check that simply found nothing to compare would report nothing. See the
        // remark on this class.
        if (matches.Count != 1) {
            findings.Add(
                new ParityFinding(
                    "TWP013",
                    claim,
                    $"{RepositoryFiles.DocumentName} states this {matches.Count} times and it must state it"
                    + $" exactly once — the sentence matching /{pattern}/ was reworded or removed, so"
                    + " nothing checks its number any more"
                )
            );

            return;
        }

        var groups = matches[0].Groups;

        for (var index = 0; index < expected.Length; index++) {
            var (what, count, source) = expected[index];
            var stated = int.Parse(groups[index + 1].Value, CultureInfo.InvariantCulture);

            if (stated != count) {
                findings.Add(
                    new ParityFinding(
                        "TWP012",
                        claim,
                        $"{RepositoryFiles.DocumentName} says {stated} {what} and {source} has {count}"
                    )
                );
            }
        }
    }

    /// <summary>Part 0's sentence enumerating the survey's shortfall against v4's static set.</summary>
    /// <remarks>
    ///     ⚠ <c>\s+</c> rather than a space in every one of these, because doc 43 is hard-wrapped and
    ///     a sentence moves across the wrap column whenever a word above it changes. The owed-variants
    ///     claim is already written with a newline between <c>of</c> and <c>v4's</c>, so a pattern
    ///     spelled with spaces matched it zero times and reported the claim missing — which is the
    ///     right failure for a reworded sentence and the wrong one for a reflowed paragraph.
    /// </remarks>
    [GeneratedRegex(@"\*\*(\d+)\s+of\s+v4's\s+(\d+)\s+static\s+utilities\s+are\s+named\s+by\s+no\s+row\*\*")]
    private static partial Regex UnlistedHeadline();

    /// <summary>The same number restated in exit criterion 1.</summary>
    [GeneratedRegex(@"the\s+(\d+)\s+v4\s+static\s+utilities\s+no\s+row\s+names")]
    private static partial Regex UnlistedExitCriterion();

    /// <summary>Part 0's sentence counting the variants the engine does not resolve.</summary>
    [GeneratedRegex(@"It\s+is\s+\*\*(\d+)\s+of\s+v4's\s+(\d+)\*\*")]
    private static partial Regex OwedVariants();
}
