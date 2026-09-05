// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Ui.Styling;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>One row of <c>docs/plan/43-web-styling-parity.tsv</c>.</summary>
/// <remarks>
///     ⚠ <b>Three of the fourteen columns are computed and the rest are not, and which is which is the
///     whole design.</b> <see cref="Emits" />, <see cref="Reads" /> and <see cref="State" /> are what
///     the engine does, so they are measured on every run and a hand edit to them is a test failure.
///     Everything else — what Tailwind is, which family answers a root, whether the emitted value is
///     faithful — is either a fact about a package that is not installed here or a judgement, and both
///     are kept by hand in the file where a reviewer sees them change.
/// </remarks>
sealed class ParityRow {
    public required string[] Cells { get; init; }

    public string Category => Cells[0];
    public string Root => Cells[1];
    public string Example => Cells[3];
    public string Css => Cells[4];
    public string Families => Cells[5];
    public string Emits { get => Cells[6]; set => Cells[6] = value; }
    public string Reads { get => Cells[7]; set => Cells[7] = value; }
    public string State { get => Cells[9]; set => Cells[9] = value; }
    public string ValueGap => Cells[11];

    /// <summary>The reason column, which is prose plus whatever expiry clauses it declares.</summary>
    /// <remarks>
    ///     ⚠ <b>Read by <see cref="RefusalExpiry" /> and by nothing else, and until that landed it was
    ///     read by nothing at all.</b> Four of the fourteen columns had no accessor here; this is the
    ///     one that mattered, because a refusal's whole content is in it. A reason no test can see is
    ///     a reason that expires unobserved, which is the finding <c>#288</c> is.
    /// </remarks>
    public string Note => Cells[12];

    public string Classes => Cells[13];
}

/// <summary>Re-derives the parity ledger's measured columns from the consumption probe.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists: the table drifted, silently, in the direction that costs the most.</b>
///         <c>docs/plan/43-web-styling-parity.tsv</c> is the thing anyone reads to answer "how close to
///         Tailwind are we". It was generated once by a script that is not in the tree — its own Part 0
///         says so — and then hand-maintained through a week of work in which eleven grid families,
///         <c>space-*</c>, <c>divide-*</c>, the per-edge borders, the per-corner radii, the gradients
///         and the ring all landed. None of that reached the file. Twenty roots sat at
///         <c>absent</c> while the feature was finished and shipped.
///     </para>
///     <para>
///         ⚠ <b>A ledger wrong in the pessimistic direction is the expensive kind, and it is the kind
///         nothing catches.</b> An optimistic error is found by the next person who tries the feature
///         and it does not work. A pessimistic one quietly sizes work that is already done, and the
///         only person who could notice is the one who would have to do it. Re-syncing the file by hand
///         fixes today and guarantees tomorrow; the same argument closed the three hand-copied
///         <c>ui-box.frag</c> files with a test rather than with a re-sync.
///     </para>
///     <para>
///         <b>The split of what is measured from what is declared.</b> Two of the three axes in Part 0's
///         cross product cannot be measured here — what Tailwind is needs <c>tailwindcss</c> installed,
///         and which Vixen family answers a Tailwind root is a judgement about two vocabularies that
///         collide on names (<c>bg</c>, <c>border</c>, <c>text</c> and <c>transition</c> mean different
///         things on either side; <c>block</c> and <c>inline</c> used to and now honestly answer both
///         roots apiece). Those stay in the file as reviewed data. What the engine <i>does</i> with
///         what the family emits is measurable, is the part that rotted, and is what this computes.
///     </para>
///     <para>
///         ⚠ <b>And a completeness guard, because the failure being prevented is an omission rather than
///         a wrong value.</b> A family that lands and is written into no row would leave every column
///         here agreeing with itself while the ledger went on calling the root absent — the exact drift
///         of the last week, reproduced under a green test. So every name in the registry has to appear
///         in some row's <c>vixen_family</c>, and a new family fails the run until somebody says which
///         Tailwind root it answers. See <c>UtilityConsumptionProbe</c>'s own remark on scenes for the
///         same lesson learned six times: the gate's logic is rarely what is wrong.
///     </para>
/// </remarks>
static class ParityLedger {
    public const int Columns = 14;

    /// <summary>The states a row can be in.</summary>
    /// <remarks>
    ///     ⚠ <b>There were six and there are five, because the sixth described a row rather than a
    ///     state.</b> <c>unknown</c> existed for one row: an aggregate the original script left behind,
    ///     eight static classes from unrelated Tailwind roots under one descriptive name, of which two
    ///     resolved and six did not. Recording that was better than picking the state that flattered —
    ///     and better still was giving the eight classes the roots that actually own them, which the
    ///     row's own note asked for. <c>snap-mandatory</c> and <c>snap-proximity</c> are v4's
    ///     strictness half of <c>scroll-snap-type</c>, <c>space-x-px</c> and <c>space-y-px</c> are
    ///     values of the functional roots that already emit them, and the four <c>*-reverse</c>
    ///     switches are static roots of their own, on two rows that read <c>absent</c> and say why.
    ///     What is left of the aggregate is measured like every other row, at the <c>absent</c> the
    ///     engine reports for it. Nothing here can produce <c>unknown</c> any more, so nothing here
    ///     offers it — a state nothing can produce is a state somebody finds a use for — and
    ///     <c>Bucket</c>, the predicate that recognised the one row, went with it out of
    ///     <see cref="Derive" />.
    /// </remarks>
    public static readonly string[] States = ["works", "partial", "inert", "absent", "composed"];

    /// <summary>Finds the ledger, walking up from the test binary to the repository root.</summary>
    public static string Locate() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null) {
            var candidate = Path.Combine(dir.FullName, "docs", "plan", "43-web-styling-parity.tsv");

            if (File.Exists(candidate)) {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "docs/plan/43-web-styling-parity.tsv was not found above " + AppContext.BaseDirectory
        );
    }

    public static (string[] Header, List<ParityRow> Rows) Read(string path) {
        var lines = File.ReadAllLines(path);
        var header = lines[0].Split('\t');
        var rows = new List<ParityRow>();

        for (var i = 1; i < lines.Length; i++) {
            if (lines[i].Length == 0) {
                continue;
            }

            var cells = lines[i].Split('\t');

            if (cells.Length != Columns) {
                throw new InvalidOperationException(
                    $"line {i + 1} of the ledger has {cells.Length} columns, not {Columns}"
                );
            }

            rows.Add(new ParityRow { Cells = cells });
        }

        return (header, rows);
    }

    public static void Write(string path, string[] header, IEnumerable<ParityRow> rows) {
        var text = new StringBuilder();
        text.Append(string.Join('\t', header)).Append('\n');

        foreach (var row in rows) {
            text.Append(string.Join('\t', row.Cells)).Append('\n');
        }

        File.WriteAllText(path, text.ToString());
    }

    /// <summary>What the engine does with everything the families can emit.</summary>
    public sealed record Measurement(
        IReadOnlyDictionary<string, IReadOnlyList<string>> ByFamily,
        IReadOnlyDictionary<string, string> Verdicts,
        IReadOnlySet<string> Registered,
        IReadOnlySet<string> Resolvable
    );

    /// <summary>Takes the measurement the ledger's computed columns are derived from.</summary>
    /// <param name="listed">Every class name the ledger mentions, so keyword coverage can be checked.</param>
    /// <remarks>
    ///     ⚠ <b>Keyword coverage is a lower bound and is used only to demote, never to promote.</b> The
    ///     <c>classes</c> column is Tailwind's static set for a root as the original survey transcribed
    ///     it, and for several roots it is short of what v4 actually ships — the <c>display</c> row
    ///     lists eight of the twenty-one its own note counts. So "one of these does not resolve" is
    ///     sound evidence of a gap, and "all of them resolve" is not evidence of completeness. A row is
    ///     therefore demoted from <c>works</c> to <c>partial</c> when a listed class fails, and never
    ///     promoted because they all pass.
    /// </remarks>
    public static Measurement Measure(IEnumerable<string> listed) {
        var tokens = ThemeTokens.Parse(UtilityConsumptionProbe.ProbeTheme);
        var surface = UtilityFamilies.Surface(tokens);
        var family = new Dictionary<string, string>(StringComparer.Ordinal);
        var registered = new HashSet<string>(StringComparer.Ordinal);

        foreach (var utility in surface) {
            var name = UtilityFamilies.SplitName(utility).Name;
            family[utility] = name;
            registered.Add(name);
        }

        var byFamily = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var (property, _, utility) in UtilityConsumptionProbe.Emissions()) {
            if (!family.TryGetValue(utility, out var name)) {
                continue;
            }

            if (!byFamily.TryGetValue(name, out var properties)) {
                byFamily[name] = properties = new SortedSet<string>(StringComparer.Ordinal);
            }

            properties.Add(property);
        }

        var ledger = UtilityConsumptionProbe.Take();
        var verdicts = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var property in ledger.Emitted) {
            verdicts[property] = ledger.Consumers.ContainsKey(property) ? "read"
                : ledger.Composed.ContainsKey(property) ? "composed"
                : "inert";
        }

        var resolvable = new HashSet<string>(StringComparer.Ordinal);
        var declarations = new List<UtilityDeclaration>();

        foreach (var name in listed) {
            declarations.Clear();

            if (UtilityParser.TryParse(name, out var parsed)
                && UtilityFamilies.TryResolve(parsed, tokens, declarations)) {
                resolvable.Add(name);
            }
        }

        return new Measurement(
            byFamily.ToDictionary(p => p.Key, p => (IReadOnlyList<string>)[.. p.Value], StringComparer.Ordinal),
            verdicts,
            registered,
            resolvable
        );
    }

    /// <summary>What one row's three computed columns should say.</summary>
    public static (string Emits, string Reads, string State) Derive(ParityRow row, Measurement measured) {
        var families = Split(row.Families);
        var properties = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var name in families) {
            if (measured.ByFamily.TryGetValue(name, out var emitted)) {
                properties.UnionWith(emitted);
            }
        }

        var reads = properties.Where(p => Verdict(measured, p) == "read").ToList();

        // A `--tw-*` fragment is half a declaration and moves nothing on its own, so it is judged by
        // whatever assembles it rather than on its own account — the reasoning is written out on
        // `UtilityConsumptionProbe.Ledger.Composed`. Excluding them here is what makes `translate-x-*`
        // read as working: the fragment is composed, the `translate` it lands in is read, and a rule
        // that counted the fragment as unread would call a finished family half-done.
        var real = properties.Where(p => Verdict(measured, p) != "composed").ToList();

        var state = properties.Count == 0 ? "absent"
            : real.Count == 0 ? "composed"
            : real.All(p => Verdict(measured, p) == "read") ? "works"
            : reads.Count == 0 ? "inert"
            : "partial";

        if (state == "works") {
            var listed = Split(row.Classes, ' ');

            if ((listed.Count > 0 && listed.Exists(c => !measured.Resolvable.Contains(c)))
                || row.ValueGap.Trim().Length > 0) {
                state = "partial";
            }
        }

        return (string.Join(',', properties), string.Join(',', reads), state);
    }

    static string Verdict(Measurement measured, string property) =>
        measured.Verdicts.TryGetValue(property, out var verdict) ? verdict : "inert";

    public static List<string> Split(string cell, char separator = ',') =>
        cell.Length == 0 ? [] : [.. cell.Split(separator, StringSplitOptions.RemoveEmptyEntries)];
}
