// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Vixen.Ui.Styling.Utilities.Tests;

/// <summary>What kind of thing a refusal is waiting on.</summary>
enum ExpiryKind {
    /// <summary>Another ledger root, which must still be refused.</summary>
    /// <remarks>
    ///     Written <c>[expires-with &lt;root&gt;]</c>. The cited root's state is a <i>computed</i>
    ///     column, so this needs no foresight from whoever writes it and cannot be spelt around: the
    ///     run in which the cited root stops being refused is the run this fails on.
    /// </remarks>
    With,

    /// <summary>A symbol that does not exist yet, and whose arrival reverses the refusal.</summary>
    /// <remarks>
    ///     Written <c>[expires-on &lt;Namespace.Type&gt;.&lt;Member&gt;]</c>. Weaker than
    ///     <see cref="With" /> — see <see cref="RefusalExpiryTests" /> for why, and for why it is here
    ///     anyway.
    /// </remarks>
    On,

    /// <summary>A CSS property nothing reads, whose exemption this refusal rests on.</summary>
    /// <remarks>
    ///     <para>
    ///         Written <c>[expires-when-read &lt;css-property&gt;]</c>, and it is the other file's half
    ///         of #288: a note that says <i>"the width is read; the logical colour is not —
    ///         InertProperties.txt #21"</i> is a refusal resting on an allow-list line, one dependency
    ///         edge out from that file's own expiry. Nothing carried the verdict across, so the run
    ///         that deleted the exemption left the ledger's sentence standing.
    ///     </para>
    ///     <para>
    ///         As exact as <see cref="With" /> and for the same reason: the condition is
    ///         <i>measured</i> — <c>UtilityConsumptionProbe</c> runs the frame and reports which
    ///         properties moved a channel — so nobody predicts anything and nobody can spell around
    ///         it. It differs from <see cref="With" /> only in what it names: a property rather than a
    ///         ledger root, which is what a <c>partial</c> row's gap is usually about.
    ///     </para>
    /// </remarks>
    WhenRead
}

/// <summary>One refusal's expiry condition, as a ledger note or a prose refusal declares it.</summary>
/// <param name="Root">The ledger root whose note carries the clause, or the file the prose is in.</param>
/// <param name="Kind">Which sort of condition it is.</param>
/// <param name="Anchor">The root or symbol named, verbatim.</param>
/// <param name="Prose">
///     Whether it was found in a <c>README.md</c> or a doc comment rather than in the ledger. The
///     difference the suite makes of it is one assertion: a ledger clause is also checked against its
///     own row's state — a refusal that stopped being refused wants deleting — and a prose refusal has
///     no row to be in a state.
/// </param>
sealed record ExpiryClause(string Root, ExpiryKind Kind, string Anchor, bool Prose = false)
    : IComparable<ExpiryClause> {
    /// <summary>The census line for this clause.</summary>
    public string Line => $"{Root}\t{Spelling(Kind)}\t{Anchor}";

    /// <summary>How a kind is written in a note and in the census.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The clause word.</returns>
    public static string Spelling(ExpiryKind kind) =>
        kind switch {
            ExpiryKind.With => "expires-with",
            ExpiryKind.On => "expires-on",
            _ => "expires-when-read"
        };

    public int CompareTo(ExpiryClause? other) =>
        other is null ? 1 : string.CompareOrdinal(Line, other.Line);
}

/// <summary>Reads the expiry conditions the parity ledger's refusals declare, and the census of them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The clause lives in the <c>note</c> column rather than in a fifteenth column or a
///         second file, and that is the design decision the whole thing turns on.</b> The failure being
///         prevented is that nobody writes the condition down as a condition. Anything that makes
///         recording one a separate act — another column to fill, another file to open — loses to the
///         thing it is competing with, which is finishing the sentence you were already writing. A
///         bracketed clause at the end of the note costs one clause, and it sits against the prose it
///         formalises, so a reviewer reads the reason and the condition in one place.
///     </para>
///     <para>
///         ⚠ <b>Detecting an <i>undeclared</i> citation was tried first and abandoned, with a
///         measurement.</b> The obvious mechanism is to notice a refusal's note mentioning another
///         root's name and require it to be declared. Swept over the 47 refusal-shaped notes, that
///         finds 106 mentions across 42 rows — and almost all of them are English. The roots include
///         <c>list-*</c>, <c>from-*</c>, <c>text</c>, <c>block-*</c>, <c>mask</c>, <c>scroll</c>,
///         <c>transform</c>, <c>border</c>, <c>display</c> and <c>flex</c>, so "the draw list", "read
///         back from the target" and "the border box" all match. A gate with that false-positive rate
///         is a gate nobody keeps. The vocabulary collides with English by construction — Part 0 says
///         so about <c>bg</c>, <c>border</c>, <c>text</c> and <c>transition</c> — so the declaration
///         has to be written, and what a machine can do is check it and refuse to let the set of them
///         drift.
///     </para>
/// </remarks>
static partial class RefusalExpiry {
    /// <summary>The states in which a root still counts as refused.</summary>
    /// <remarks>
    ///     ⚠ <b><c>partial</c> is not on this list, and that is deliberate.</b> A root that is half
    ///     landed is a root somebody has been inside recently, and a refusal resting on it is exactly
    ///     the one to re-read — <c>origin-*</c>'s premise expired while the transform it named went to
    ///     <c>partial</c>, not to <c>works</c>. <c>unknown</c> is not on it either: a state nobody can
    ///     name is not a foundation to stand a refusal on.
    /// </remarks>
    public static readonly string[] Refusing = ["absent", "inert"];

    /// <summary>The states in which a root may still rest a gap on a condition of its own.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>partial</c> <i>is</i> on this list, and the difference from
    ///         <see cref="Refusing" /> is the point rather than an inconsistency.</b> An
    ///         <c>expires-with</c> says "this root is refused because that one is", which is prose
    ///         about a state a root that half works has already left. An <c>expires-when-read</c> says
    ///         "this longhand of mine is emitted and read by nothing" — <c>border-s-*</c>'s width is
    ///         read and its colour is not — and that is the commonest shape a <c>partial</c> takes.
    ///         Refusing the clause there would leave the 29 most expensive rows in the ledger with no
    ///         way to record a condition at all. <c>works</c> is what it excludes: a root with no gap
    ///         left has nothing to rest on anything.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>expires-on</c> now takes this list too, and the case that forced it is the one
    ///         the whole suite exists for.</b> <c>hyphens</c> is <c>partial</c> because two of CSS's
    ///         three keywords landed and <c>auto</c> is refused. That refusal named two blockers — no
    ///         Liang pattern set, and nothing carrying a language — and the second one closed when
    ///         #600 put <c>UiElement.ResolvedLanguage</c> on the element and through to the shaper.
    ///         Nothing went red, because the row is <c>partial</c> and only <c>expires-when-read</c>
    ///         reached a <c>partial</c> row; the sentence sat in the ledger, in
    ///         <c>UtilityFamilies</c> and in <c>HyphenMode</c>'s remarks saying the input was missing
    ///         for as long as anyone cared to read it. A refusal that lives inside one keyword of a
    ///         half-landed root is *more* likely to rot than one on an <c>absent</c> row, not less,
    ///         because the row's own state stops moving once the other keywords ship. The carrier's
    ///         state and the <i>anchor's</i> state are separate questions, and only the anchor's is
    ///         what <see cref="Refusing" /> is really about.
    ///     </para>
    /// </remarks>
    public static readonly string[] Gapped = ["absent", "inert", "partial"];

    /// <summary>Every clause the ledger's notes declare, sorted.</summary>
    /// <param name="rows">The ledger.</param>
    /// <returns>The clauses.</returns>
    public static List<ExpiryClause> Declared(IEnumerable<ParityRow> rows) =>
        Read(rows, Clause());

    /// <summary>Every clause the ledger's notes only <i>mention</i>, sorted.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Written <c>[~expires-… …]</c>, and it exists because the grammar had no way to
    ///         spell a mention that is not a declaration</b> (#1325). The <c>select</c> row's note
    ///         narrates the commit that gave it a second clause and the merge that took it away again
    ///         — which is the kind of sentence this column is for — and writing that clause in the
    ///         ordinary way <i>declared a second real one</i>: two identical census rows off one cell.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A quotation is not held to anything, and that is the point rather than a hole.</b>
    ///         It resolves no anchor and expires on no condition, because the commonest thing to quote
    ///         is a clause that has been <i>deleted</i> — an anchor that no longer resolves, a
    ///         condition that came due and was acted on. What it is held to is being well formed: it
    ///         is counted by <see cref="Opened" /> with everything else, so a mistyped quotation is a
    ///         red test rather than a line the sweep silently reads as prose.
    ///     </para>
    /// </remarks>
    /// <param name="rows">The ledger.</param>
    /// <returns>The quotations.</returns>
    public static List<ExpiryClause> Quoted(IEnumerable<ParityRow> rows) =>
        Read(rows, Quotation());

    /// <summary>Reads one of the two forms out of every note.</summary>
    /// <param name="rows">The ledger.</param>
    /// <param name="form">Which form to read.</param>
    /// <returns>What it found, sorted.</returns>
    static List<ExpiryClause> Read(IEnumerable<ParityRow> rows, Regex form) {
        var clauses = new List<ExpiryClause>();

        foreach (var row in rows) {
            foreach (Match match in form.Matches(row.Note)) {
                clauses.Add(Parse(match, row.Root));
            }
        }

        clauses.Sort();

        return clauses;
    }

    /// <summary>Turns one match of either form into a clause.</summary>
    /// <param name="match">The match.</param>
    /// <param name="root">The ledger root or the file it was found in.</param>
    /// <param name="prose">Whether it came from the prose sweep.</param>
    /// <returns>The clause.</returns>
    static ExpiryClause Parse(Match match, string root, bool prose = false) {
        var kind = match.Groups["kind"].Value switch {
            "with" => ExpiryKind.With,
            "on" => ExpiryKind.On,
            _ => ExpiryKind.WhenRead
        };

        return new ExpiryClause(root, kind, match.Groups["anchor"].Value.Trim(), prose);
    }

    /// <summary>Census lines that more than one clause produced.</summary>
    /// <remarks>
    ///     ⚠ <b>The census cannot ask this of itself, which is why it is asked here</b> (#1325). It is
    ///     a list equality against a list the same sweep produced, so a repeated line is consistent
    ///     with itself; <c>No_refusal_outlives_the_condition_it_names</c> evaluates the repeat twice
    ///     and agrees with itself both times; and the opening count balances, because two openings
    ///     parse to two clauses. Nothing in the shape of "derive a set and hold it against a committed
    ///     copy" can see a set that contains the same member twice.
    /// </remarks>
    /// <param name="clauses">The clauses.</param>
    /// <returns>The repeated lines, sorted, once each.</returns>
    public static List<string> Duplicates(IEnumerable<ExpiryClause> clauses) =>
        [
            .. clauses
                .GroupBy(clause => clause.Line, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .Order(StringComparer.Ordinal)
        ];

    /// <summary>How many clause-shaped things the notes contain, well formed or not.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument's own check, and the reason it is counted separately from the
    ///         parse.</b> A regex that fails to match a malformed clause does not report anything — it
    ///         returns one fewer row, and one fewer row in a sweep is indistinguishable from a clause
    ///         nobody wrote. So the opening bracket is counted with a pattern that cannot be fooled by
    ///         the contents, and the two numbers have to agree. A typo inside a clause is then a red
    ///         test rather than a silent exemption, which is the difference between this and every
    ///         allow-list that has rotted here.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This counts the quotation form too, and it has to.</b> <c>[~expires-</c> does not
    ///         contain <c>[expires-</c>, so a pattern that only looked for the declaration would leave
    ///         the whole quoting half of the grammar unmeasured — a mistyped quotation reading as
    ///         prose, which is the exact failure this method exists to refuse one form of. Balance is
    ///         therefore against <see cref="Declared" /> <i>plus</i> <see cref="Quoted" />.
    ///     </para>
    /// </remarks>
    /// <param name="rows">The ledger.</param>
    /// <returns>The count.</returns>
    public static int Opened(IEnumerable<ParityRow> rows) =>
        rows.Sum(row => Opening().Count(row.Note));

    /// <summary>The repository root, found through the ledger's own walk.</summary>
    /// <returns>Its path.</returns>
    public static string Root() =>
        Directory.GetParent(Path.GetDirectoryName(ParityLedger.Locate())!)!.Parent!.FullName;

    /// <summary>Finds the committed census, beside the tests that own it.</summary>
    /// <returns>Its path.</returns>
    public static string Locate() =>
        // The ledger's own walk finds the repository root; the census sits with the suite rather than
        // with the document, for the reason `InertProperties.txt` does — it is the test's record of
        // what it is holding the document to, and it is regenerated, so it must be the file in the
        // tree and not a copy in `bin`.
        Path.Combine(Root(), "Core", "Vixen.Ui.Styling.Utilities.Tests", "RefusalExpiry.txt");

    /// <summary>The three files that define the clause grammar, whose clauses are specimens.</summary>
    /// <remarks>
    ///     ⚠ <b>A file that teaches the grammar cannot also be read by it.</b> These three spell the
    ///     three kinds out — <c>[expires-with &lt;root&gt;]</c> — and one of them deliberately writes a
    ///     <i>malformed</i> clause to explain why the opening bracket is counted separately. Swept as
    ///     prose their specimens and their one deliberate parse failure would make the instrument's own
    ///     documentation the first thing to turn it red. Nothing else is exempt: the exemption is the
    ///     definition of the language, not a place refusals are allowed to hide. ⚠ The count of them
    ///     used to be written here and is not any more — it was already wrong, which is this file's own
    ///     lesson about prose beside a derived list, and the quotation form added specimens to all
    ///     three.
    /// </remarks>
    public static readonly string[] Grammar = [
        "Core/Vixen.Ui.Styling.Utilities.Tests/RefusalExpiry.cs",
        "Core/Vixen.Ui.Styling.Utilities.Tests/RefusalExpiryTests.cs",
        "docs/plan/43-web-styling-parity.md"
    ];

    /// <summary>Directories the prose sweep does not descend into.</summary>
    /// <remarks>
    ///     ⚠ <b><c>.claude</c> is the one that is not obvious and the one that matters.</b> It holds a
    ///     whole checkout per agent worktree, so a walk from the repository root that did not skip it
    ///     would sweep every parallel agent's copy of these files and record their clauses in this
    ///     tree's census — a suite whose verdict depends on who else is working today. The rest are
    ///     build output, where the same file appears again as a copy.
    /// </remarks>
    static readonly string[] Skipped = [
        ".git", ".claude", ".vs", ".idea", "bin", "obj", "artifacts", "node_modules", "TestResults"
    ];

    /// <summary>Every <c>.md</c> and <c>.cs</c> file a prose refusal could be written in.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>Their paths, repository-relative and with forward slashes.</returns>
    public static List<string> ProseFiles(string root) {
        var files = new List<string>();
        var stack = new Stack<string>();

        stack.Push(root);

        while (stack.Count != 0) {
            var directory = stack.Pop();

            foreach (var child in Directory.EnumerateDirectories(directory)) {
                if (!Skipped.Contains(Path.GetFileName(child), StringComparer.Ordinal)) {
                    stack.Push(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory)) {
                if (Path.GetExtension(file) is not (".cs" or ".md")) {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');

                if (!Grammar.Contains(relative, StringComparer.Ordinal)) {
                    files.Add(relative);
                }
            }
        }

        files.Sort(StringComparer.Ordinal);

        return files;
    }

    /// <summary>Every clause a README or a doc comment declares, sorted.</summary>
    /// <remarks>
    ///     ⚠ <b>The four refusals this was widened for carried no clause at all</b>, so this does not
    ///     retroactively catch them and nothing here pretends it does — what it removes is the reason
    ///     they could not have carried one. A refusal in a <c>README.md</c> or a doc comment is the
    ///     same object as a refusal in the ledger's note column: a verdict plus a condition. Until
    ///     this sweep existed the condition had nowhere to go except a sentence, and #674's four are
    ///     what a sentence is worth.
    ///     <para>
    ///         ⚠ <b>"A quoted clause is indistinguishable from a declared one and is meant to be" is
    ///         what this paragraph used to say, and it was wrong.</b> The argument was that a comment
    ///         narrating an old clause records it here once and the census diff is where a reviewer
    ///         says so — the same review step the ledger's clauses get — and that the alternative, a
    ///         second syntax for quoting, is a grammar nobody would remember. Both halves failed
    ///         against a real note (#1325). The narration that exposed it was in the <i>same cell</i>
    ///         as the live clause it was narrating, so what the census recorded was not one row with a
    ///         reviewable provenance but <b>two identical rows</b>, which every test in this suite
    ///         agrees with: an equality against a derived list, a condition evaluated twice, and an
    ///         opening count that balances two for two. And the cost of not having the second syntax
    ///         was paid anyway — the note was reworded to name the clause without its brackets, which
    ///         is a footgun rather than a grammar. <see cref="Quoted" /> is that syntax.
    ///     </para>
    /// </remarks>
    /// <param name="root">The repository root.</param>
    /// <returns>The clauses.</returns>
    public static List<ExpiryClause> DeclaredInProse(string root) => Sweep(root).Clauses;

    /// <summary>Every clause a README or a doc comment only mentions, sorted.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The quotations.</returns>
    public static List<ExpiryClause> QuotedInProse(string root) => Sweep(root).Quotations;

    /// <summary>How many clause-shaped things the prose contains, well formed or not.</summary>
    /// <param name="root">The repository root.</param>
    /// <returns>The count.</returns>
    public static int OpenedInProse(string root) => Sweep(root).Opened;

    /// <summary>Reads every prose file once and remembers what it found.</summary>
    /// <remarks>
    ///     Four tests ask for this and the sweep is five thousand files, so it is read once per run
    ///     rather than once per question. Keyed on the root only because there is one repository; a
    ///     second key would be a cache nothing exercises.
    /// </remarks>
    /// <param name="root">The repository root.</param>
    /// <returns>The clauses, the quotations, and the count of opening brackets.</returns>
    static (List<ExpiryClause> Clauses, List<ExpiryClause> Quotations, int Opened) Sweep(string root) {
        lock (Swept) {
            if (Swept.TryGetValue(root, out var already)) {
                return already;
            }

            var clauses = new List<ExpiryClause>();
            var quotations = new List<ExpiryClause>();
            var opened = 0;

            foreach (var file in ProseFiles(root)) {
                var text = File.ReadAllText(Path.Combine(root, file));

                // ⚠ `expires-` and not `[expires-`: a file whose only clause-shaped thing is a
                // quotation opens with `[~expires-`, which does not contain the longer needle. Skipping
                // such a file would leave a mistyped quotation uncounted and therefore unreported —
                // the cheap early-out silently exempting exactly the form it was not updated for.
                if (!text.Contains("expires-", StringComparison.Ordinal)) {
                    continue;
                }

                opened += Opening().Count(text);

                foreach (Match match in Clause().Matches(text)) {
                    clauses.Add(Parse(match, file, prose: true));
                }

                foreach (Match match in Quotation().Matches(text)) {
                    quotations.Add(Parse(match, file, prose: true));
                }
            }

            clauses.Sort();
            quotations.Sort();
            Swept[root] = (clauses, quotations, opened);

            return (clauses, quotations, opened);
        }
    }

    static readonly Dictionary<string, (List<ExpiryClause> Clauses, List<ExpiryClause> Quotations, int Opened)>
        Swept = new(StringComparer.Ordinal);

    /// <summary>Every clause in the ledger and in the prose, together and sorted.</summary>
    /// <param name="rows">The ledger.</param>
    /// <param name="root">The repository root.</param>
    /// <returns>The clauses.</returns>
    public static List<ExpiryClause> All(IEnumerable<ParityRow> rows, string root) {
        List<ExpiryClause> clauses = [.. Declared(rows), .. DeclaredInProse(root)];

        clauses.Sort();

        return clauses;
    }

    /// <summary>Every quotation in the ledger and in the prose, together and sorted.</summary>
    /// <param name="rows">The ledger.</param>
    /// <param name="root">The repository root.</param>
    /// <returns>The quotations.</returns>
    public static List<ExpiryClause> AllQuoted(IEnumerable<ParityRow> rows, string root) {
        List<ExpiryClause> quotations = [.. Quoted(rows), .. QuotedInProse(root)];

        quotations.Sort();

        return quotations;
    }

    /// <summary>Reads the census, ignoring its prose.</summary>
    /// <param name="path">Where it is.</param>
    /// <returns>The lines that are clauses.</returns>
    public static List<string> ReadCensus(string path) =>
        [
            .. File.ReadAllLines(path)
                .Select(line => line.TrimEnd())
                .Where(line => line.Length != 0 && !line.StartsWith('#'))
        ];

    /// <summary>Writes the census back, keeping the prose header the file opens with.</summary>
    /// <param name="path">Where it is.</param>
    /// <param name="clauses">What to record.</param>
    public static void WriteCensus(string path, IEnumerable<ExpiryClause> clauses) {
        var header = File.ReadAllLines(path)
            .TakeWhile(line => line.Length == 0 || line.StartsWith('#'))
            .ToList();

        var text = new StringBuilder();

        foreach (var line in header) {
            text.Append(line).Append('\n');
        }

        foreach (var clause in clauses) {
            text.Append(clause.Line).Append('\n');
        }

        File.WriteAllText(path, text.ToString());
    }

    /// <summary>The type an <c>expires-on</c> anchor names, or null if no loaded assembly has it.</summary>
    /// <param name="anchor">The whole anchor, <c>Namespace.Type.Member</c>.</param>
    /// <returns>The type and the member name.</returns>
    public static (Type? Type, string Member) Resolve(string anchor) {
        var split = anchor.LastIndexOf('.');

        if (split <= 0) {
            return (null, anchor);
        }

        var typeName = anchor[..split];
        var member = anchor[(split + 1)..];

        // ⚠ <b>Named assemblies first, and `AppDomain.GetAssemblies()` is not enough on its own.</b>
        // This was written as a sweep of the loaded assemblies and it failed on the first anchor for a
        // reason worth keeping: a .NET assembly is loaded lazily, on the first execution that needs it,
        // so which ones are loaded depends on which tests have already run. Under the whole suite
        // `Vixen.Ui` is there because the probe builds documents; under a filter that runs only this
        // file, nothing has touched it and `Vixen.Ui.DrawCommand` resolves to null — which this
        // suite reads as "the anchor is misspelt". An instrument whose verdict depends on the test
        // filter is the same defect as one that reports success on the day it does not run, and it was
        // the anti-typo guard that caught it. Reaching through a type forces the load.
        //
        // ⚠ <b>`Vixen.Ui.Controls` is the fourth and it was load-order-dependent until it was named
        // here.</b> `object`'s anchor is a member of `Image`, and the run that resolved it was
        // resolving it by accident: the `expires-when-read` clauses two branches up call
        // `UtilityConsumptionProbe.Take()`, whose scenes build an `Icon`, so the assembly happened to
        // be loaded by the time this clause was reached. Delete the last `expires-when-read` clause
        // from the ledger and the anchor silently stops resolving — which this suite reads as a typo,
        // on a clause nobody touched. That is the same defect this paragraph describes, one
        // assembly further out.
        var assemblies = new[] {
                typeof(UiDocument).Assembly, typeof(StyleEngine).Assembly, typeof(UtilityFamilies).Assembly,
                typeof(Vixen.Ui.Controls.Image).Assembly
            }
            .Concat(AppDomain.CurrentDomain.GetAssemblies())
            .Distinct();

        var type = assemblies
            .Select(assembly => assembly.GetType(typeName, throwOnError: false))
            .FirstOrDefault(candidate => candidate is not null);

        return (type, member);
    }

    /// <summary>Whether a type has a member of that name, however it is declared.</summary>
    /// <param name="type">The type.</param>
    /// <param name="member">The name.</param>
    /// <returns>Whether it is there.</returns>
    public static bool Has(Type type, string member) =>
        type.GetMember(
            member,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
            | BindingFlags.FlattenHierarchy
        ).Length != 0;

    [GeneratedRegex(@"\[expires-(?<kind>with|on|when-read)\s+(?<anchor>[^\]]+)\]")]
    private static partial Regex Clause();

    [GeneratedRegex(@"\[~expires-(?<kind>with|on|when-read)\s+(?<anchor>[^\]]+)\]")]
    private static partial Regex Quotation();

    [GeneratedRegex(@"\[~?expires-")]
    private static partial Regex Opening();
}
