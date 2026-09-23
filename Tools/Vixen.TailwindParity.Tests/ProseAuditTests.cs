// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.TailwindParity.Tests;

/// <summary>That the counts doc 43 <i>states</i> are the counts its artefacts <i>hold</i>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This gate exists because the batch that replaced doc 43's assertions with
///         measurements left one of the assertions behind.</b> One commit wrote "163 of v4's 890
///         static utilities are named by no row"; the next shrank that list to 150 and did not
///         revisit the sentence; a third edited the same file again and did not either. Every
///         artefact was right and the document describing them was thirteen out, because
///         <see cref="ParityAudit" /> reads the <c>.txt</c> and nothing read the <c>.md</c>.
///     </para>
///     <para>
///         ⚠ <b>The instrument's own failure is the first thing asserted.</b> A regex over prose
///         goes quiet rather than red when the prose is reworded, so the case that matters most here
///         is <see cref="A_claim_that_has_been_reworded_away_is_TWP013" /> — without it this file
///         would be one edit away from passing while checking nothing, which is the shape doc 43's
///         exit criterion 3 records having shipped.
///     </para>
/// </remarks>
public sealed class ProseAuditTests {
    static readonly RepositoryFiles Files = RepositoryFiles.Locate();

    /// <summary>A registry whose two totals are small enough to write into a sentence by hand.</summary>
    static readonly TailwindRegistry Miniature = new() {
        Package = "tailwindcss",
        Version = "4.3.3",
        Taken = "2026-09-23",
        StaticRoots = ["sr-only", "static"],
        FunctionalRoots = ["left", "top"],
        Variants = [new TailwindVariant("hover", "hover"), new TailwindVariant("before", "before")],
        Checked = ["sr-only", "static"],
        Refused = [],
    };

    /// <summary>Prose that agrees with <see cref="Miniature" /> and with one entry in each list.</summary>
    static string Clean() =>
        """
        Measured: **1 of v4's 2 static utilities are named by no row**, listed one per line.
        It is **1 of v4's 2**, in the variants file.
        ...and the 1 v4 static utilities no row names are enumerated in that file.
        """;

    static IReadOnlyList<string> Codes(string document) =>
        [.. ProseAudit.Run(document, Miniature, ["sr-only"], ["before"]).Select(finding => finding.Code)];

    /// <summary>The check this file exists for, over the committed document.</summary>
    [Fact]
    public void Doc_43_states_the_counts_its_own_artefacts_measure() {
        var registry = TailwindRegistry.Read(Files.Registry);
        var unlisted = ParityAudit.ReadUnlisted(Files.Unlisted);
        var unsupported = ParityAudit.ReadUnlisted(Files.VariantsUnsupported);

        Assert.Equal(
            [],
            ProseAudit.Run(File.ReadAllText(Files.Document), registry, unlisted, unsupported)
                .Select(finding => finding.ToString())
        );
    }

    /// <summary>The baseline the three cases below mutate reports nothing at all.</summary>
    [Fact]
    public void Prose_that_agrees_with_the_artefacts_reports_nothing() {
        Assert.Empty(Codes(Clean()));
    }

    /// <summary>A stated count the shrinking list disagrees with — the defect that prompted this.</summary>
    [Fact]
    public void A_count_the_artefact_disagrees_with_is_TWP012() {
        Assert.Contains("TWP012", Codes(Clean().Replace("**1 of v4's 2 static", "**9 of v4's 2 static")));
        Assert.Contains("TWP012", Codes(Clean().Replace("the 1 v4 static utilities", "the 9 v4 static utilities")));
        Assert.Contains("TWP012", Codes(Clean().Replace("It is **1 of v4's 2**", "It is **9 of v4's 2**")));
    }

    /// <summary>A stated total the registry snapshot disagrees with.</summary>
    /// <remarks>
    ///     The denominator is checked as well as the numerator because it is the number that moves
    ///     when the pinned <c>tailwindcss</c> version does, which is the one occasion nobody is
    ///     looking at this sentence.
    /// </remarks>
    [Fact]
    public void A_stated_v4_total_the_snapshot_disagrees_with_is_TWP012() {
        Assert.Contains("TWP012", Codes(Clean().Replace("**1 of v4's 2 static", "**1 of v4's 890 static")));
        Assert.Contains("TWP012", Codes(Clean().Replace("It is **1 of v4's 2**", "It is **1 of v4's 88**")));
    }

    /// <summary>⚠ And a claim that is no longer in the document at all, which is the instrument's own failure.</summary>
    /// <remarks>
    ///     ⚠ <b>Zero matches and two matches are both failures, and zero is the dangerous one.</b> A
    ///     gate that compares whatever it happened to find reports nothing on the day the sentence is
    ///     reworded — it does not report that it has stopped looking. Pairing the count with the
    ///     claim is what makes rewording this prose cost an edit here rather than cost the check.
    /// </remarks>
    [Fact]
    public void A_claim_that_has_been_reworded_away_is_TWP013() {
        Assert.Contains("TWP013", Codes(Clean().Replace("are named by no row", "are named by no ledger row")));
        Assert.Contains("TWP013", Codes(Clean().Replace("It is **1 of v4's 2**", "It is 1 of v4's 2")));
        Assert.Contains("TWP013", Codes(""));

        // And twice, which is the other way a count stops being one number: two sentences stating it
        // can disagree with each other while each agrees with the file.
        Assert.Contains("TWP013", Codes(Clean() + "\n" + Clean()));
    }
}
