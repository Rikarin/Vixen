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

/// <summary>One refusal's expiry condition, as the ledger's note column declares it.</summary>
/// <param name="Root">The ledger root whose note carries the clause.</param>
/// <param name="Kind">Which sort of condition it is.</param>
/// <param name="Anchor">The root or symbol named, verbatim.</param>
sealed record ExpiryClause(string Root, ExpiryKind Kind, string Anchor) : IComparable<ExpiryClause> {
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

    /// <summary>The states in which a root may still rest a gap on an unread property.</summary>
    /// <remarks>
    ///     ⚠ <b><c>partial</c> <i>is</i> on this list, and the difference from <see cref="Refusing" />
    ///     is the point rather than an inconsistency.</b> An <c>expires-with</c> says "this root is
    ///     refused because that one is", which is prose about a state a root that half works has
    ///     already left. An <c>expires-when-read</c> says "this longhand of mine is emitted and read by
    ///     nothing" — <c>border-s-*</c>'s width is read and its colour is not — and that is the
    ///     commonest shape a <c>partial</c> takes. Refusing the clause there would leave the 29 most
    ///     expensive rows in the ledger with no way to record a condition at all. <c>works</c> is what
    ///     it excludes: a root with no gap left has nothing to rest on anything.
    /// </remarks>
    public static readonly string[] Gapped = ["absent", "inert", "partial"];

    /// <summary>Every clause the ledger's notes declare, sorted.</summary>
    /// <param name="rows">The ledger.</param>
    /// <returns>The clauses.</returns>
    public static List<ExpiryClause> Declared(IEnumerable<ParityRow> rows) {
        var clauses = new List<ExpiryClause>();

        foreach (var row in rows) {
            foreach (Match match in Clause().Matches(row.Note)) {
                var kind = match.Groups["kind"].Value switch {
                    "with" => ExpiryKind.With,
                    "on" => ExpiryKind.On,
                    _ => ExpiryKind.WhenRead
                };

                clauses.Add(new ExpiryClause(row.Root, kind, match.Groups["anchor"].Value.Trim()));
            }
        }

        clauses.Sort();

        return clauses;
    }

    /// <summary>How many clause-shaped things the notes contain, well formed or not.</summary>
    /// <remarks>
    ///     ⚠ <b>The instrument's own check, and the reason it is counted separately from the parse.</b>
    ///     A regex that fails to match a malformed clause does not report anything — it returns one
    ///     fewer row, and one fewer row in a sweep is indistinguishable from a clause nobody wrote. So
    ///     the opening bracket is counted with a pattern that cannot be fooled by the contents, and the
    ///     two numbers have to agree. A typo inside a clause is then a red test rather than a silent
    ///     exemption, which is the difference between this and every allow-list that has rotted here.
    /// </remarks>
    /// <param name="rows">The ledger.</param>
    /// <returns>The count.</returns>
    public static int Opened(IEnumerable<ParityRow> rows) =>
        rows.Sum(row => Opening().Count(row.Note));

    /// <summary>Finds the committed census, beside the tests that own it.</summary>
    /// <returns>Its path.</returns>
    public static string Locate() {
        // The ledger's own walk finds the repository root; the census sits with the suite rather than
        // with the document, for the reason `InertProperties.txt` does — it is the test's record of
        // what it is holding the document to, and it is regenerated, so it must be the file in the
        // tree and not a copy in `bin`.
        var root = Directory.GetParent(Path.GetDirectoryName(ParityLedger.Locate())!)!.Parent!.FullName;

        return Path.Combine(
            root, "Core", "Vixen.Ui.Styling.Utilities.Tests", "RefusalExpiry.txt"
        );
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

    [GeneratedRegex(@"\[expires-")]
    private static partial Regex Opening();
}
