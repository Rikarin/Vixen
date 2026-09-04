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
    On
}

/// <summary>One refusal's expiry condition, as the ledger's note column declares it.</summary>
/// <param name="Root">The ledger root whose note carries the clause.</param>
/// <param name="Kind">Which sort of condition it is.</param>
/// <param name="Anchor">The root or symbol named, verbatim.</param>
sealed record ExpiryClause(string Root, ExpiryKind Kind, string Anchor) : IComparable<ExpiryClause> {
    /// <summary>The census line for this clause.</summary>
    public string Line => $"{Root}\t{(Kind == ExpiryKind.With ? "expires-with" : "expires-on")}\t{Anchor}";

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

    /// <summary>Every clause the ledger's notes declare, sorted.</summary>
    /// <param name="rows">The ledger.</param>
    /// <returns>The clauses.</returns>
    public static List<ExpiryClause> Declared(IEnumerable<ParityRow> rows) {
        var clauses = new List<ExpiryClause>();

        foreach (var row in rows) {
            foreach (Match match in Clause().Matches(row.Note)) {
                var kind = match.Groups["kind"].Value == "with" ? ExpiryKind.With : ExpiryKind.On;

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
        var assemblies = new[] {
                typeof(UiDocument).Assembly, typeof(StyleEngine).Assembly, typeof(UtilityFamilies).Assembly
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

    [GeneratedRegex(@"\[expires-(?<kind>with|on)\s+(?<anchor>[^\]]+)\]")]
    private static partial Regex Clause();

    [GeneratedRegex(@"\[expires-")]
    private static partial Regex Opening();
}
