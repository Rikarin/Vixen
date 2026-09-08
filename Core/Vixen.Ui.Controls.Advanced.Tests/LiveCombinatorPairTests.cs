// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>
///     Which of the <c>A &gt; B</c> pairings the stylesheets declare are built by a control that
///     assembles its own parts — observed by building every control, not inferred from its source.
/// </summary>
/// <remarks>
///     <para>
///         <b>The verdict half of <c>Rikarin/Vixen#531</c>, for the part of the domain a runtime can
///         decide.</b> <c>Vixen.Ui.Styling.Tests.CombinatorPairTests</c> commits the <i>domain</i>:
///         88 pairings that some sheet declares between two bare type selectors. It says on its face
///         that it decides nothing about whether a pairing is live, and the reason a pairing being
///         dead matters is <c>compositor-editor &gt; node-canvas</c> — a rule whose two tags were
///         both real, whose pairing never occurred, and which drew the compositor's graph at zero
///         width for as long as it stood.
///     </para>
///     <para>
///         ⚠ <b>Five audits tried to decide that by reading source and all five stopped, each one
///         reporting that the residue it could not explain was larger than the last.</b> The reason
///         is in the domain: markup nesting proves 3 of the 88, and the type→tag map everybody
///         recommends takes it to 14, because the other 74 have parents built in C# — most of them a
///         control assembling its own parts through <c>Part("…")</c>, which no scan of markup can
///         ever see and no scan of C# can join to the sheet without a model of construction.
///     </para>
///     <para>
///         ⚠ <b>So this builds them instead, and the result is exact rather than inferred.</b> Every
///         public element type in the two control assemblies is constructed, added to a document with
///         both themes installed, and laid out; the parent→child tags of the tree it grows are read
///         off the elements. A pairing that appears here is live by construction — an element with
///         the parent tag is holding a child with the child tag, in this process, now.
///     </para>
///     <para>
///         ⚠ <b>What this does NOT say is that anything else is dead, and no assertion here may ever
///         be read that way.</b> A control that builds a part only once it has an item, a pairing
///         assembled by the editor rather than by a control, and every pairing whose parent tag
///         belongs to an assembly this one cannot see are all outside the sweep and unjudged. The
///         committed file is a set of proofs, not a verdict on the rest of the domain.
///     </para>
///     <para>
///         ⚠ <b>With one bounded exception, which is the verdict half and lives in a second file.</b>
///         Where the sweep constructed an element with a rule's parent tag and watched it assemble
///         its children, a child the sheet names and the element never built is not unjudged — it is
///         a question, and <see cref="Every_rule_whose_parent_was_built_is_proved_or_explained" /> is
///         where it must be answered in writing. Eleven of the 88 pairings are in that reach and
///         nine are proved; the two that are not have the same answer, a lazily-built
///         <c>icon</c> part. Everything else stays unjudged, and the whole of the difference between
///         the two files is whether the parent was built.
///     </para>
///     <para>
///         <b>Bare controls, deliberately.</b> Seeding each type — a tab, a row, an option — would
///         raise the count and make every row depend on a fixture decision that nothing else states.
///         What a control builds with nothing done to it is a property of the control; what it builds
///         after a fixture has poked it is a property of the poke, and the first is the one a
///         stylesheet's <c>A &gt; B</c> can rely on.
///     </para>
/// </remarks>
[Collection(SharedCatalogue.Name)]
public class LiveCombinatorPairTests {
    /// <summary>The domain: every pairing a committed sheet declares.</summary>
    const string DomainFile = "Core/Vixen.Ui.Styling.Tests/CombinatorPairs.txt";

    /// <summary>The proofs: the subset of that domain the controls actually build.</summary>
    const string CensusFile = "Core/Vixen.Ui.Controls.Advanced.Tests/LiveCombinatorPairs.txt";

    /// <summary>The suspicions: rules whose parent was built and whose child never turned up.</summary>
    const string SuspectFile = "Core/Vixen.Ui.Controls.Advanced.Tests/SuspectCombinatorPairs.txt";

    /// <summary>What a regenerated row says until somebody explains it.</summary>
    const string Unexplained = "unexplained";

    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the census back instead of asserting it.</summary>
    static bool Regenerating =>
        Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>How many element types the sweep is expected to build, at least.</summary>
    /// <remarks>
    ///     111 today, and the floor is under it rather than at it so that adding a control is not a
    ///     failing test. Its whole job is the day the filter stops matching: a sweep that built
    ///     nothing observes nothing, and an empty observation agrees with an empty census perfectly.
    /// </remarks>
    const int Elements = 100;

    /// <summary>Every parent→child tag pairing the built controls grew, done once.</summary>
    public static IReadOnlySet<string> Observed => observed ??= Sweep();

    static IReadOnlySet<string>? observed;

    /// <summary>How many types the sweep built to get there.</summary>
    public static int Built => built;

    static int built;

    /// <summary>The premise every assertion below rests on: the sweep built controls and read trees.</summary>
    /// <remarks>
    ///     ⚠ <b>Three claims rather than one floor, because a floor is what this repository has twice
    ///     had eaten by success.</b> The types were built; enough pairings came out of them to be a
    ///     real tree walk; and three pairings a person has traced to the <c>Part("…")</c> call that
    ///     makes them are present <i>by name and the right way round</i>. The last is what a count
    ///     cannot give: a walk that recorded the child as the parent keeps the count exactly right
    ///     and reverses every row.
    /// </remarks>
    [Fact]
    public void The_control_sweep_actually_ran() {
        _ = Observed;

        Assert.True(Built >= Elements, $"the sweep built only {Built} element types, which is not these two assemblies");

        Assert.True(
            Observed.Count >= 140,
            $"the sweep observed only {Observed.Count} parent-child tag pairings across {Built} controls, "
            + "against 168 measured — so it built the elements and did not walk them"
        );

        // `ScrollView.cs:426`, `SplitView.cs:149` and `Tabs.cs:162` — three `Part("…")` calls, each
        // in a different control, each the far end of a rule in a committed sheet. They are the
        // orientation control: `scroll-content > scroll-view` is the same walk with the two ends
        // swapped, and it would satisfy every count above.
        Assert.Contains("scroll-view > scroll-content", Observed, StringComparer.Ordinal);
        Assert.Contains("split-view > split-bar", Observed, StringComparer.Ordinal);
        Assert.Contains("tabs > tab-panels", Observed, StringComparer.Ordinal);

        Assert.DoesNotContain("scroll-content > scroll-view", Observed, StringComparer.Ordinal);
    }

    /// <summary>The pairings proved live by construction are exactly the committed ones.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Exact and in both directions, and the direction that matters is the one that
    ///         loses a row.</b> A pairing here has been proved live; if it stops being observed,
    ///         either the control stopped building that part — in which case the sheet's rule has
    ///         just gone dead and nothing else in this repository would say so — or the sweep
    ///         stopped seeing it. Both want a person, and both are silent under a floor.
    ///     </para>
    ///     <para>
    ///         A row arriving is the cheerful direction and still moves a line: it means a rule
    ///         somebody was entitled to doubt is now proved, and the file is where that is recorded
    ///         so the next audit does not re-derive it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The domain is read off disk too, and a pairing that leaves it leaves this file
    ///         as well.</b> That is deliberate: a proof about a rule no sheet declares any more is a
    ///         proof about nothing, and keeping it would turn this census into a record of what the
    ///         controls build — which is a different question with several hundred answers.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_pairing_the_controls_build_is_in_the_committed_census() {
        var root = Root();
        var path = Path.Combine(root, CensusFile);
        var domain = Domain(Path.Combine(root, DomainFile));
        var proved = domain.Where(pair => Observed.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        if (Regenerating) {
            Write(path, proved);
        }

        var census = Census(path);

        var arrived = proved.Where(pair => !census.Contains(pair)).ToList();
        var departed = census.Where(pair => !proved.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The census of pairings the controls build is out of date.

             Built by a control and not in {CensusFile} — a rule that was in doubt is now proved:
             {Lines(arrived)}

             In {CensusFile} and built by nothing any more:
             {Lines(departed)}

             ⚠ A departed row is the loud one. It means either the control stopped building that
             part — in which case the sheet's rule is now dead, which is exactly the defect
             `compositor-editor > node-canvas` was — or this sweep stopped seeing it. Read the diff
             rather than regenerating past it; re-run with VIXEN_REGENERATE=1 once it is what you
             meant.
             """
        );
    }

    /// <summary>
    ///     A rule whose parent this sweep watched assemble its parts either has its child among them
    ///     or has a written reason why not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The verdict half of <c>Rikarin/Vixen#531</c>, and the only negative claim in the
    ///         three files.</b> The domain census says what the sheets declare; the census beside
    ///         this one says which of those a control was seen to build. Neither can say a rule is
    ///         dead, which is what <c>compositor-editor &gt; node-canvas</c> needed somebody to say —
    ///         both tags real, the pairing never occurring, the compositor's graph drawn at zero
    ///         width. This says it, for exactly the pairings where the runtime has standing to: the
    ///         sweep constructed an element with the parent tag and read the children it built, so a
    ///         child the sheet names and the element never built is a real question rather than a
    ///         gap in a model.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which is why this is not the source-reading gate five audits abandoned.</b>
    ///         Those needed an exemption per pairing the model could not explain, and the reason on
    ///         every one of them would have been "this is live, the model is blind" — a list nobody
    ///         can keep honest. Blindness is not available here: a parent tag the sweep never built
    ///         is not in this file at all and is suspected of nothing. The reasons on the rows that
    ///         are here are findings about controls — a part built only once there is an item, a
    ///         child that arrives from a template — and each is a sentence somebody checked.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An arriving row is the loud direction, and it is the same event as a row leaving
    ///         the proofs.</b> A control that stops building a part while the sheet still styles it
    ///         is the defect; here it shows up as a new suspicion with no reason on it, and an
    ///         <c>unexplained</c> row fails on its own so that regenerating cannot quietly accept
    ///         one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_rule_whose_parent_was_built_is_proved_or_explained() {
        var root = Root();
        var path = Path.Combine(root, SuspectFile);
        var domain = Domain(Path.Combine(root, DomainFile));
        var parents = Observed.Select(static pair => pair.Split(" > ")[0]).ToHashSet(StringComparer.Ordinal);

        var reachable = domain.Where(pair => parents.Contains(pair.Split(" > ")[0])).ToList();
        var suspect = reachable.Where(pair => !Observed.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        // The sweep's own guard is `The_control_sweep_actually_ran`; this is the guard for the JOIN,
        // which has a second way of coming out empty. If the domain's parent tags and the observed
        // ones stop overlapping — a sweep that built nothing, a domain file read as a header, a tag
        // renamed on both sides — then `reachable` is empty, every set below is empty, and this test
        // agrees with an empty census exactly. There are 11 today, nine of them proved.
        Assert.True(
            reachable.Count >= 8,
            $"only {reachable.Count} of the {domain.Count} declared pairings have a parent this sweep built, "
            + "against 11 measured — the domain and the observation are no longer talking about the same tags."
        );

        var reasons = SuspectReasons(path);

        if (Regenerating) {
            WriteSuspects(path, suspect.Select(pair => (pair, reasons.GetValueOrDefault(pair, Unexplained))));
            reasons = SuspectReasons(path);
        }

        var arrived = suspect.Where(pair => !reasons.ContainsKey(pair)).ToList();
        var departed = reasons.Keys.Where(pair => !suspect.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The census of rules whose parent was built without their child is out of date.

             Newly suspected — the control builds this parent and no longer builds this child:
             {Lines(arrived)}

             In {SuspectFile} and no longer suspected — the control builds it now, or the rule is gone:
             {Lines(departed)}

             ⚠ An arriving row is the loud one, and it is what `compositor-editor > node-canvas` would
             have looked like the day it broke. Read the diff before regenerating.
             """
        );

        var silent = reasons.Where(entry => entry.Value == Unexplained).Select(entry => entry.Key).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            silent.Count == 0,
            $"""
             These rows say `{Unexplained}`, which is what regeneration writes and not an answer:
             {Lines(silent)}

             The sweep built the parent and watched it assemble its children, so "the model is blind"
             is not available. Either the control builds this part only in some state the sweep does
             not put it in — say which — or the rule is dead and wants deleting.
             """
        );
    }

    /// <summary>Builds every element type in the two control assemblies and reads the trees.</summary>
    /// <remarks>
    ///     ⚠ <b>A fixture apiece.</b> A control that has been laid out once beside a dozen others is
    ///     a control whose parts may have been built against somebody else's width, and the question
    ///     here is what this one builds on its own.
    /// </remarks>
    static HashSet<string> Sweep() {
        var make = typeof(LiveCombinatorPairTests)
            .GetMethod(nameof(Make), BindingFlags.NonPublic | BindingFlags.Static)!;

        var pairs = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;

        var types = new[] { typeof(Button).Assembly, typeof(DataGrid).Assembly }
            .SelectMany(static assembly => assembly.GetTypes())
            .Where(static type => type.IsPublic && !type.IsAbstract && typeof(UiElement).IsAssignableFrom(type))
            .Where(static type => type.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(static type => type.FullName, StringComparer.Ordinal);

        foreach (var type in types) {
            using var ui = new AdvancedFixture();

            var element = (UiElement)make.MakeGenericMethod(type).Invoke(null, [ui.Document.Root])!;

            ui.Update();
            count++;
            Walk(element, pairs);
        }

        built = count;
        return pairs;
    }

    static UiElement Make<T>(UiElement parent) where T : UiElement, new() => parent.Add<T>();

    static void Walk(UiElement element, HashSet<string> into) {
        foreach (var child in element.Children) {
            into.Add($"{element.Tag} > {child.Tag}");
            Walk(child, into);
        }
    }

    /// <summary>The pairings the sheets declare, read from the domain census.</summary>
    /// <remarks>
    ///     ⚠ Its absence throws rather than yielding an empty domain — the answer to "what does this
    ///     print on the day it does not run" has to be a failure and not a pass over two empty sets.
    /// </remarks>
    static List<string> Domain(string path) {
        var rows = Rows(path, DomainFile).Select(static row => row.Split('\t')[0].Trim()).ToList();

        Assert.True(
            rows.Count >= 60,
            $"{DomainFile} yielded only {rows.Count} pairings, against 88 measured — it is not the domain."
        );

        return rows;
    }

    static HashSet<string> Census(string path) => Rows(path, CensusFile).ToHashSet(StringComparer.Ordinal);

    /// <summary>The suspected pairings and the reason written beside each.</summary>
    /// <remarks>
    ///     ⚠ A row with no second column reads as <see cref="Unexplained" /> rather than as an empty
    ///     reason, so deleting the text off a row is the same failure as adding a row without one.
    /// </remarks>
    static Dictionary<string, string> SuspectReasons(string path) {
        var reasons = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in Rows(path, SuspectFile)) {
            var tab = row.IndexOf('\t');
            var pair = (tab < 0 ? row : row[..tab]).Trim();
            var reason = tab < 0 ? string.Empty : row[(tab + 1)..].Trim();

            reasons[pair] = reason.Length == 0 ? Unexplained : reason;
        }

        return reasons;
    }

    static List<string> Rows(string path, string name) {
        var lines = File.ReadAllLines(path);

        // "It has rows" cannot stand in for "it was read": a truncated file and a repository whose
        // controls build nothing are both zero rows, and only one of them still has the header.
        Assert.True(
            lines.Count(static line => line.StartsWith('#')) >= 5,
            $"{name} has lost its header, so it was emptied rather than answered."
        );

        var rows = new List<string>();

        foreach (var line in lines) {
            var text = line.Trim();

            if (text.Length != 0 && !text.StartsWith('#')) {
                rows.Add(text);
            }
        }

        return rows;
    }

    static void Write(string path, IEnumerable<string> rows) {
        var text = new StringBuilder();

        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith('#') && line.Trim().Length != 0) {
                break;
            }

            text.AppendLine(line);
        }

        foreach (var row in rows) {
            text.AppendLine(row);
        }

        File.WriteAllText(path, text.ToString());
    }

    /// <summary>Writes the suspicions back, keeping each row's reason and the file's header.</summary>
    static void WriteSuspects(string path, IEnumerable<(string Pair, string Reason)> rows) {
        var text = new StringBuilder();

        foreach (var line in File.ReadLines(path)) {
            if (!line.StartsWith('#') && line.Trim().Length != 0) {
                break;
            }

            text.AppendLine(line);
        }

        foreach (var (pair, reason) in rows) {
            text.Append(pair).Append('\t').AppendLine(reason);
        }

        File.WriteAllText(path, text.ToString());
    }

    static string Lines(IEnumerable<string> rows) {
        var joined = string.Join("\n", rows.Select(static row => $"  {row}"));

        return joined.Length == 0 ? "  (none)" : joined;
    }

    /// <summary>The working tree's root, found by a directory only it has.</summary>
    static string Root() {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent) {
            if (Directory.Exists(Path.Combine(directory.FullName, "Raven", "Library"))) {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException($"the repository root was not found above '{AppContext.BaseDirectory}'.");
    }
}
