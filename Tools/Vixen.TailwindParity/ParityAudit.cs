// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.TailwindParity;

/// <summary>One disagreement between the ledger and the committed registry snapshot.</summary>
/// <param name="Code">The diagnostic id, so a failure can be grepped for.</param>
/// <param name="Subject">The root or class name the finding is about.</param>
/// <param name="Text">What is wrong, in a sentence.</param>
sealed record ParityFinding(string Code, string Subject, string Text) {
    /// <inheritdoc />
    public override string ToString() => $"{Code} {Subject}: {Text}";
}

/// <summary>Checks doc 43's ledger against what tailwindcss actually registers.</summary>
/// <remarks>
///     <para>
///         <b>What this can and cannot answer.</b> It compares two vocabularies, so every finding is
///         about a <i>name</i> — a root the ledger surveys that v4 does not have, a root v4 has that
///         the ledger never surveyed, a class the ledger measures Vixen against that v4 refuses to
///         compile. Whether Vixen's implementation of a root is any good is the other half of the
///         cross product and is measured by <c>ParityLedger</c> in
///         <c>Vixen.Ui.Styling.Utilities.Tests</c>, on every test run, against the engine.
///     </para>
///     <para>
///         ⚠ <b>A wrong class in the <c>classes</c> column is not cosmetic — it moves a row's
///         state.</b> <c>ParityLedger.Derive</c> demotes a row from <c>works</c> to <c>partial</c>
///         when any listed class fails to resolve, and never promotes on the strength of the column.
///         So a class Tailwind does not ship, listed under a root, is a permanent demotion of a root
///         that may be finished — the pessimistic error that <c>ParityLedger</c>'s own remarks
///         identify as the expensive kind, arriving through the one column nothing in the tree could
///         read. <c>backdrop-blur-2</c> and <c>backdrop-blur-4</c> were exactly that.
///     </para>
///     <para>
///         ⚠ <b>The static roots are a shrinking list and not a check.</b> v4 registers 890 static
///         utilities and the survey's <c>classes</c> column names about three quarters of them; the
///         remainder are enumerated in <c>docs/plan/43-web-styling-unlisted.txt</c> rather than
///         waved through, and an entry there that has since been listed is a failure too, so the
///         file can only shrink. That is <c>CheckWhitespace</c>'s exemption shape, for
///         <c>CheckWhitespace</c>'s reason: a list that can grow silently is a list that records
///         nothing.
///     </para>
/// </remarks>
static class ParityAudit {
    /// <summary>Compares the ledger with the registry snapshot.</summary>
    /// <param name="registry">The committed snapshot.</param>
    /// <param name="rows">The ledger's rows.</param>
    /// <param name="unlisted">Static roots the survey knowingly does not name.</param>
    /// <returns>Every disagreement, in a stable order.</returns>
    public static IReadOnlyList<ParityFinding> Run(
        TailwindRegistry registry,
        IReadOnlyList<LedgerRow> rows,
        IReadOnlyCollection<string> unlisted
    ) {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(unlisted);

        var findings = new List<ParityFinding>();
        var functional = new HashSet<string>(registry.FunctionalRoots, StringComparer.Ordinal);
        var statics = new HashSet<string>(registry.StaticRoots, StringComparer.Ordinal);
        var asked = new HashSet<string>(registry.Checked, StringComparer.Ordinal);
        var refused = new HashSet<string>(registry.Refused, StringComparer.Ordinal);
        var surveyed = new HashSet<string>(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var row in rows) {
            foreach (var name in row.Classes) {
                named.Add(name);
            }

            if (row.Example.Length > 0) {
                named.Add(row.Example);
            }

            // ⚠ A `static` row's root is a survey heading and not a Tailwind name — `pointer-events`
            // and `container (max-w+w)` are two of eighty-six — so only the functional roots can be
            // compared as roots at all. The static half is compared through the classes a row names,
            // which is where its real Tailwind spellings live.
            if (!string.Equals(row.Kind, "functional", StringComparison.Ordinal)) {
                continue;
            }

            var root = row.Root.EndsWith("-*", StringComparison.Ordinal) ? row.Root[..^2] : row.Root;
            surveyed.Add(root);

            if (!functional.Contains(root)) {
                findings.Add(
                    new ParityFinding(
                        "TWP001",
                        row.Root,
                        $"the ledger surveys this functional root, and {registry.Package}@{registry.Version}"
                        + " has no such root"
                    )
                );
            }
        }

        foreach (var root in registry.FunctionalRoots) {
            // The negative spelling of a root is generated from the root, so it is never a survey
            // subject of its own — `-inset` is `inset` with a minus, and the row for `inset-*` is
            // the row for both.
            if (root.StartsWith('-') || surveyed.Contains(root)) {
                continue;
            }

            findings.Add(
                new ParityFinding(
                    "TWP002",
                    root,
                    $"{registry.Package}@{registry.Version} registers this functional root and no ledger row"
                    + " surveys it"
                )
            );
        }

        foreach (var name in named) {
            if (!asked.Contains(name)) {
                findings.Add(
                    new ParityFinding(
                        "TWP004",
                        name,
                        "the ledger names this class and the committed snapshot was never asked about it —"
                        + " re-take it with Tools/Vixen.TailwindParity/snapshot.mjs"
                    )
                );

                continue;
            }

            if (refused.Contains(name)) {
                findings.Add(
                    new ParityFinding(
                        "TWP003",
                        name,
                        $"{registry.Package}@{registry.Version} compiles this class to nothing, so measuring"
                        + " Vixen against it can only demote the row"
                    )
                );
            }
        }

        var exempt = new SortedSet<string>(unlisted, StringComparer.Ordinal);

        foreach (var root in registry.StaticRoots) {
            if (root.StartsWith('-') || named.Contains(root) || exempt.Contains(root)) {
                continue;
            }

            findings.Add(
                new ParityFinding(
                    "TWP005",
                    root,
                    $"{registry.Package}@{registry.Version} registers this static utility, no ledger row names"
                    + " it, and it is not in docs/plan/43-web-styling-unlisted.txt"
                )
            );
        }

        foreach (var root in exempt) {
            if (named.Contains(root)) {
                findings.Add(
                    new ParityFinding(
                        "TWP006",
                        root,
                        "this is listed by a ledger row now, so it must come out of"
                        + " docs/plan/43-web-styling-unlisted.txt — that list can only shrink"
                    )
                );

                continue;
            }

            if (!statics.Contains(root)) {
                findings.Add(
                    new ParityFinding(
                        "TWP007",
                        root,
                        $"{registry.Package}@{registry.Version} has no such static utility, so this line of"
                        + " docs/plan/43-web-styling-unlisted.txt excuses nothing"
                    )
                );
            }
        }

        return findings;
    }

    /// <summary>Reads the shrinking list of static roots the survey does not name.</summary>
    /// <param name="path">The list.</param>
    /// <returns>One entry per non-comment line.</returns>
    public static IReadOnlyList<string> ReadUnlisted(string path) {
        ArgumentNullException.ThrowIfNull(path);

        return
        [
            .. File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
        ];
    }
}
