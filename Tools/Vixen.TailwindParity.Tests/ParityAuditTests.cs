// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.TailwindParity.Tests;

/// <summary>The Tailwind half of doc 43's cross product, checked against the committed snapshot.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is what makes the tool a gate rather than a script somebody remembers.</b> Doc
///         43's exit criterion 1 is measured by <c>ParityLedgerTests</c> against the engine, and the
///         columns it measures <i>against</i> — which Tailwind roots exist, which classes each
///         covers — were a hand transcription dated 2026-08-07 that nothing in this repository could
///         read. The transcription is now a committed snapshot and this file is the reader.
///     </para>
///     <para>
///         ⚠ <b>The instrument is checked before the subject.</b> An audit over an empty registry, an
///         empty ledger or an exemption list that swallows everything agrees with any ledger ever
///         written — the failure shape doc 43's own exit criterion 3 records having shipped once —
///         so each of the seven findings is shown reachable from a baseline that is clean.
///     </para>
/// </remarks>
public sealed class ParityAuditTests {
    static readonly RepositoryFiles Files = RepositoryFiles.Locate();

    /// <summary>A registry small enough to reason about, with two roots of each kind.</summary>
    static readonly TailwindRegistry Miniature = new() {
        Package = "tailwindcss",
        Version = "4.3.3",
        Taken = "2026-09-23",
        StaticRoots = ["sr-only", "static"],
        FunctionalRoots = ["left", "top"],
        Variants = ["hover"],
        Checked = ["refused-class", "sr-only", "static", "top-0"],
        Refused = ["refused-class"],
    };

    /// <summary>A ledger over <see cref="Miniature" /> with nothing wrong with it.</summary>
    static List<LedgerRow> Clean() =>
    [
        new("top-*", "functional", "top-0", ["sr-only", "static"]),
        new("left-*", "functional", "", []),
    ];

    static IReadOnlyList<string> Codes(IEnumerable<LedgerRow> rows, params string[] unlisted) =>
        [.. ParityAudit.Run(Miniature, [.. rows], unlisted).Select(finding => finding.Code)];

    /// <summary>The check this file exists for.</summary>
    [Fact]
    public void The_ledger_surveys_the_vocabulary_tailwindcss_actually_registers() {
        var registry = TailwindRegistry.Read(Files.Registry);
        var rows = ParityLedgerTable.Read(Files.Ledger);
        var unlisted = ParityAudit.ReadUnlisted(Files.Unlisted);

        Assert.Equal([], ParityAudit.Run(registry, rows, unlisted).Select(finding => finding.ToString()));
    }

    /// <summary>The baseline the six cases below mutate reports nothing at all.</summary>
    [Fact]
    public void A_ledger_that_matches_the_registry_reports_nothing() {
        Assert.Empty(Codes(Clean()));
    }

    /// <summary>A functional root the ledger surveys that Tailwind does not have.</summary>
    [Fact]
    public void A_surveyed_root_tailwind_does_not_register_is_TWP001() {
        var rows = Clean();
        rows.Add(new LedgerRow("no-such-root-*", "functional", "", []));

        Assert.Contains("TWP001", Codes(rows));
    }

    /// <summary>A functional root Tailwind registers that no row surveys.</summary>
    [Fact]
    public void A_registered_root_no_row_surveys_is_TWP002() {
        var rows = Clean();
        rows.RemoveAt(1);

        Assert.Contains("TWP002", Codes(rows));
    }

    /// <summary>A class the ledger measures Vixen against that Tailwind compiles to nothing.</summary>
    /// <remarks>
    ///     ⚠ This is the finding that costs something rather than the one that is merely untidy.
    ///     <c>ParityLedger.Derive</c> demotes a row to <c>partial</c> on any listed class that does
    ///     not resolve, so a class Tailwind does not ship is a permanent demotion of a root that may
    ///     be finished — and it demotes in the pessimistic direction, which <c>ParityLedger</c>'s own
    ///     remarks call the kind nothing catches.
    /// </remarks>
    [Fact]
    public void A_listed_class_tailwind_refuses_is_TWP003() {
        var rows = Clean();
        rows.Add(new LedgerRow("top-*", "functional", "", ["refused-class"]));

        Assert.Contains("TWP003", Codes(rows));
    }

    /// <summary>A class added to the ledger since the snapshot was taken.</summary>
    /// <remarks>
    ///     ⚠ The reason the snapshot records what it was <i>asked</i> and not only what it refused.
    ///     A snapshot holding refusals alone answers "is this real?" with silence for a name it has
    ///     never seen, so a row written after the snapshot would pass without anything looking.
    /// </remarks>
    [Fact]
    public void A_class_the_snapshot_was_never_asked_about_is_TWP004() {
        var rows = Clean();
        rows.Add(new LedgerRow("top-*", "functional", "", ["never-asked-class"]));

        Assert.Contains("TWP004", Codes(rows));
    }

    /// <summary>A static utility neither the ledger nor the unlisted file names.</summary>
    [Fact]
    public void A_static_utility_nobody_names_is_TWP005() {
        List<LedgerRow> rows = [new("top-*", "functional", "top-0", ["sr-only"]), new("left-*", "functional", "", [])];

        Assert.Contains("TWP005", Codes(rows));
        Assert.DoesNotContain("TWP005", Codes(rows, "static"));
    }

    /// <summary>An unlisted-file entry a row has since listed, so the file can only shrink.</summary>
    [Fact]
    public void An_exemption_a_row_now_lists_is_TWP006() {
        Assert.Contains("TWP006", Codes(Clean(), "sr-only"));
    }

    /// <summary>An unlisted-file entry that is not a Tailwind static utility at all.</summary>
    [Fact]
    public void An_exemption_tailwind_does_not_register_is_TWP007() {
        Assert.Contains("TWP007", Codes(Clean(), "not-a-tailwind-utility"));
    }

    /// <summary>A snapshot with no roots in it would agree with every ledger, so it is refused.</summary>
    [Fact]
    public void An_empty_registry_is_refused_rather_than_believed() {
        var path = Path.GetTempFileName();

        try {
            File.WriteAllText(
                path,
                """{"package":"tailwindcss","version":"4.3.3","staticRoots":[],"functionalRoots":[]}"""
            );

            var thrown = Assert.Throws<InvalidOperationException>(() => TailwindRegistry.Read(path));

            Assert.Contains("can prove nothing", thrown.Message, StringComparison.Ordinal);
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>A ledger with a header and no rows would too.</summary>
    [Fact]
    public void A_ledger_with_no_rows_is_refused_rather_than_believed() {
        var path = Path.GetTempFileName();

        try {
            File.WriteAllText(path, "category\troot\tkind\texample\tclasses\n");

            var thrown = Assert.Throws<InvalidOperationException>(() => ParityLedgerTable.Read(path));

            Assert.Contains("can prove nothing", thrown.Message, StringComparison.Ordinal);
        } finally {
            File.Delete(path);
        }
    }

    /// <summary>The snapshot is a measurement and says which measurement it is.</summary>
    [Fact]
    public void The_committed_snapshot_names_the_package_version_it_was_taken_from() {
        var registry = TailwindRegistry.Read(Files.Registry);
        var rows = ParityLedgerTable.Read(Files.Ledger);

        Assert.Equal("tailwindcss", registry.Package);
        Assert.Matches(@"^\d+\.\d+\.\d+$", registry.Version);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", registry.Taken);

        // ⚠ Counts as well as membership, which is exit criterion 3's guard: an enumeration that
        // returns nothing passes every membership test ever written over it. The snapshot has to
        // have been asked about at least as many classes as the ledger names, or TWP004 is the only
        // thing it can say and TWP003 can never fire.
        var named = rows.SelectMany(row => row.Classes).Concat(rows.Select(row => row.Example))
            .Where(name => name.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(registry.Checked.Length >= named.Count, $"{named.Count} named, {registry.Checked.Length} checked");
        Assert.True(registry.Variants.Length > 80, $"only {registry.Variants.Length} variants were recorded");
    }
}
