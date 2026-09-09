// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using Vixen.Editor.Testing;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>
///     Which of the editor sheets' <c>A &gt; B</c> rules the running editor actually builds, read off
///     the tree rather than inferred from a file.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Increment (2) of #531's own list, and the half of the domain every previous pass
///         could not see.</b> <c>Core/Vixen.Ui.Styling.Tests/CombinatorPairs.txt</c> commits 88
///         pairings with a bare type on both sides; <b>84 of them come from editor sheets</b>, and
///         <c>LiveCombinatorPairTests</c> — the runtime oracle that settled the controls — lives in
///         <c>Vixen.Ui.Controls.Advanced.Tests</c> and cannot see an editor at all. So the sweep that
///         proved nine pairings there was working on the four per cent of the question that was
///         reachable from where it stood.
///     </para>
///     <para>
///         ⚠ <b>Five source-reading models were tried before this and each reported a larger residue
///         than the last</b> — 14 of 88, then 29 of 72, 39 of 75, 37 of 70 — because the editor's
///         parents are built in C# and joined to their children through a third file. The three
///         pairings that defeated all five are the same shape: a presenter constructed with the
///         parent element as its <c>host</c> argument. <c>editor-shell &gt; menu-bar</c> is
///         <c>EditorShell.cs</c> creating the shell into <c>chrome</c>, passing <c>chrome</c> to
///         <c>new MenuPresenter(…)</c>, and <c>MenuPresenter</c> doing <c>host.Add&lt;MenuBar&gt;()</c>
///         three files away. A running editor answers that in one line, exactly, and needs no model
///         of construction whatsoever.
///     </para>
///     <para>
///         ⚠ <b>This file is a set of PROOFS and says nothing about the rest of the domain.</b> A
///         pairing missing from the census is <i>unjudged</i>, never dead — the editor builds panels
///         on demand and this fixture opens a stated few. The loud direction is a row <b>leaving</b>,
///         because that is the moment a presenter stops building a part while the sheet's rule for it
///         still stands, which is <c>compositor-editor &gt; node-canvas</c> exactly (#525).
///     </para>
///     <para>
///         ⚠ <b>Confined to pairings a committed sheet declares, unlike the controls' sweep.</b> A
///         whole editor grows thousands of parent→child pairs and almost none of them is anybody's
///         rule; a census of all of them would be a diff nobody reads, and it would move on every
///         unrelated change to a panel. Intersecting with the domain keeps every row load-bearing and
///         keeps the file's two directions meaningful.
///     </para>
/// </remarks>
public class EditorCombinatorPairTests {
    /// <summary>The domain: every pairing a committed sheet declares, with a bare type on both sides.</summary>
    const string DomainFile = "Core/Vixen.Ui.Styling.Tests/CombinatorPairs.txt";

    /// <summary>The proofs: the subset of that domain a running editor builds.</summary>
    const string CensusFile = "Editor/Vixen.Editor.App.Tests/EditorCombinatorPairs.txt";

    /// <summary>The same, for an editor with every panel it registers opened.</summary>
    const string OpenedCensusFile = "Editor/Vixen.Editor.App.Tests/OpenedEditorCombinatorPairs.txt";

    /// <summary>The same again, with a document of every registered asset-editor kind open too.</summary>
    const string DocumentCensusFile = "Editor/Vixen.Editor.App.Tests/DocumentEditorCombinatorPairs.txt";

    /// <summary>Set <c>VIXEN_REGENERATE=1</c> to write the census back instead of asserting it.</summary>
    static bool Regenerating => Environment.GetEnvironmentVariable("VIXEN_REGENERATE") is "1";

    /// <summary>
    ///     The three pairings no source-reading pass reached, named so that a reversed walk cannot
    ///     pass.
    /// </summary>
    /// <remarks>
    ///     Each is a presenter given the parent element as its <c>host</c>, which is the shape #531's
    ///     own comments identified and could not close. They are asserted by name <i>and the right
    ///     way round</i>: a walk that recorded a child as its own parent keeps every count exactly
    ///     right and reverses every row, which is the bug a floor cannot see.
    /// </remarks>
    static readonly string[] Presenter = ["editor-shell > menu-bar", "mode-bar > toolbar", "viewport-bar > toolbar"];

    /// <summary>
    ///     Two pairings only the opening sweep reaches, one from each of two panels the default
    ///     arrangement does not show.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named rather than counted, and from two panels rather than one. The failure worth
    ///     catching is an <see cref="EditorSession.Open" /> that stopped building a panel's contents:
    ///     the opening sweep would then degrade to the bare one, and a census regenerated on that day
    ///     would agree with it perfectly. A count of "more than the bare sweep" is satisfied by one
    ///     panel still working, which is why two panels are named.
    /// </remarks>
    static readonly string[] Reached = ["debugger-body > tree-view", "profiler-view > data-grid"];

    /// <summary>
    ///     Three pairings only an open <em>document</em> reaches, one from each of three different
    ///     asset editors.
    /// </summary>
    /// <remarks>
    ///     ⚠ Named rather than counted, and from three editors rather than one, for
    ///     <see cref="Reached" />'s reason turned up a level: if opening a document stopped building
    ///     its view — a factory that throws into a load-diagnostics placeholder, an asset database
    ///     that never sees the file — the third sweep would degrade to the second and a census
    ///     regenerated that day would agree with it perfectly. Each of these is a part only one
    ///     asset editor builds, so one working editor cannot stand in for the rest.
    /// </remarks>
    static readonly string[] Authored =
        ["animation-stage > timeline", "font-body > font-atlas", "mixer-strip > slider"];

    /// <summary>How many declared pairings the editor is expected to prove, at least.</summary>
    /// <remarks>
    ///     Under the measured number rather than at it, so that a panel gaining a part is not a
    ///     failing test. Its job is the day the walk stops walking: a sweep that reads an empty tree
    ///     observes nothing, and an empty observation agrees with an empty census perfectly.
    /// </remarks>
    const int Floor = 16;

    /// <summary>Every parent→child tag pairing a started editor grows, done once for the class.</summary>
    static IReadOnlySet<string> Observed => observed ??= Sweep(Depth.Started);

    static IReadOnlySet<string>? observed;

    /// <summary>The same for an editor with every registered panel opened, done once for the class.</summary>
    static IReadOnlySet<string> Opened => opened ??= Sweep(Depth.Panels);

    static IReadOnlySet<string>? opened;

    /// <summary>The same again with a document of every registered kind open, done once for the class.</summary>
    static IReadOnlySet<string> Documents => documents ??= Sweep(Depth.Documents);

    static IReadOnlySet<string>? documents;

    /// <summary>How many assets the document sweep created and asked the editor to open.</summary>
    static int authored;

    /// <summary>How many pairings the walk saw altogether, declared or not.</summary>
    static int walked;

    /// <summary>How many panels the opening sweep asked the workspace for.</summary>
    static int asked;

    /// <summary>The premise every assertion below rests on: an editor was built and its tree read.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three claims and not one floor.</b> The walk covered a real tree; the three
    ///         presenter pairings are present by name; and their mirrors are absent. This repository
    ///         has twice had a floor eaten by success — a gate that reports "passed" on the day it
    ///         does not run.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The mirror denial is not what catches a reversed walk <i>here</i>, and the
    ///         difference is worth writing down.</b> The controls' sweep records every pairing, so
    ///         reversing its walk keeps the count exactly right and turns every row round; this one
    ///         intersects with the domain afterwards, so a fully reversed walk collapses to
    ///         <i>zero</i> declared pairings and the floor above is what goes red — measured. What
    ///         the mirror denial still covers is the variant a floor cannot see: a walk that records
    ///         both directions, whose count only grows.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_editor_sweep_actually_ran() {
        Assert.True(Observed.Count >= Floor, $"the walk proved only {Observed.Count} declared pairings.");

        Assert.True(walked >= 120, $"the walk saw only {walked} pairings altogether, which is not an editor.");

        foreach (var pair in Presenter) {
            Assert.Contains(pair, Observed, StringComparer.Ordinal);

            var halves = pair.Split(" > ");

            Assert.DoesNotContain($"{halves[1]} > {halves[0]}", Observed, StringComparer.Ordinal);
        }
    }

    /// <summary>The proofs are exactly what is committed, in both directions.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A row leaving is the loud one.</b> It means a presenter or a panel has stopped
    ///         building a child while a committed sheet still declares the rule — a selector that
    ///         matches nothing, which draws nothing and reports nothing. That is the defect this
    ///         issue was opened for and it has shipped here before.
    ///     </para>
    ///     <para>
    ///         A row arriving is ordinary: it means the editor now builds a pairing a sheet already
    ///         declared, and regenerating is the right response. Read the diff either way — a
    ///         regeneration that swallowed a departure is the one mistake this file cannot survive.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_declared_pairing_the_editor_builds_is_in_the_committed_census() {
        var root = Root();
        var path = Path.Combine(root, CensusFile);

        if (Regenerating) {
            Write(path, Observed.Order(StringComparer.Ordinal));
        }

        var census = Census(path, CensusFile);

        var arrived = Observed.Where(pair => !census.Contains(pair)).Order(StringComparer.Ordinal).ToList();
        var departed = census.Where(pair => !Observed.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The editor's census of proved pairings is out of date.

             Built and not in {CensusFile} — regenerate once you have read them:
             {Lines(arrived)}

             ⚠ In {CensusFile} and NO LONGER BUILT — a sheet still declares each of these and the
             editor stopped growing it:
             {Lines(departed)}

             Re-run with VIXEN_REGENERATE=1 to write this back, after reading the second list.
             """
        );
    }

    /// <summary>The same census for an editor with every panel it registers opened.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A second census rather than more rows in the first, so that no existing row
    ///         changes meaning.</b> The bare sweep's standing is "this is what a started editor
    ///         builds with no state a fixture put it in", and that is the sentence a departure from
    ///         it is read against. Opening panels is state a fixture put it in — weaker — so it gets
    ///         a file of its own, exactly as the controls' seeded sweep is kept apart from its bare
    ///         one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>But it is much stronger than seeding, and the difference is worth stating.</b>
    ///         The controls' seeded sweep has to refuse the elements it nested, because
    ///         <c>A &gt; B</c> is trivially "provable" by putting a B under an A. Nothing here nests
    ///         anything: the fixture asks the workspace to open a panel <em>the editor itself
    ///         registered</em>, and every element under it is built by that panel's own factory. So
    ///         there is no harness-built pairing to refuse, and each row is still the editor's own
    ///         construction.
    ///     </para>
    ///     <para>
    ///         The decision, stated once because it is one decision: <b>every panel the workspace has
    ///         registered, opened, and nothing else poked.</b> A hand-written list would go stale the
    ///         day a panel is added and would say nothing about it; the registry is the editor's own
    ///         answer to "what panels are there".
    ///     </para>
    ///     <para>
    ///         ⚠ Still a set of proofs. A pairing absent from here is unjudged — most of what the
    ///         domain declares lives under an asset editor, which exists only once a document of that
    ///         kind has been opened, and none of that is reached by opening a panel.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_declared_pairing_an_opened_editor_builds_is_in_the_committed_census() {
        var path = Path.Combine(Root(), OpenedCensusFile);

        if (Regenerating) {
            Write(path, Opened.Order(StringComparer.Ordinal));
        }

        var census = Census(path, OpenedCensusFile);

        var arrived = Opened.Where(pair => !census.Contains(pair)).Order(StringComparer.Ordinal).ToList();
        var departed = census.Where(pair => !Opened.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The opened editor's census of proved pairings is out of date.

             Built and not in {OpenedCensusFile} — regenerate once you have read them:
             {Lines(arrived)}

             ⚠ In {OpenedCensusFile} and NO LONGER BUILT — a sheet still declares each of these and
             the editor stopped growing it, with its panel open:
             {Lines(departed)}

             Re-run with VIXEN_REGENERATE=1 to write this back, after reading the second list.
             """
        );
    }

    /// <summary>Opening the panels actually reached parts the default arrangement does not build.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument, and the failure it exists for is the quiet one.</b> If
    ///         <see cref="EditorSession.Open" /> stopped building a panel's contents — a factory that
    ///         throws and is swallowed, a workspace that returns the descriptor without running it —
    ///         the opened sweep would degrade to the bare one and its census would still be exactly
    ///         satisfied by whatever was regenerated on that day. So the claim asserted is the
    ///         <em>difference</em>: the opened sweep is a strict superset, and it proves at least one
    ///         pairing by name that the bare sweep demonstrably does not.
    ///     </para>
    ///     <para>
    ///         ⚠ A superset and not merely a larger set. Opening a panel must not lose a pairing —
    ///         a docked tab that is not in front keeps its elements and only loses its size — so a
    ///         row that is in the bare census and not here is a panel whose contents were torn down
    ///         by something else being opened, which is a finding rather than an update.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_every_registered_panel_proves_more_than_the_default_arrangement() {
        var lost = Observed.Where(pair => !Opened.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        // ⚠ After the line above and not before it: `asked` is written by the sweep, and the sweep is
        // lazy. Asserting on it first reads a zero that means "nothing has run yet" and prints it as
        // "this editor registers no panels" — an instrument reporting on itself.
        Assert.True(asked >= 5, $"the sweep asked for {asked} panels, which is not this editor's registry.");

        Assert.True(lost.Count == 0, $"opening panels LOST pairings the default arrangement builds:\n{Lines(lost)}");

        var gained = Opened.Where(pair => !Observed.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        Assert.True(gained.Count > 0, "opening every registered panel proved nothing the default arrangement did not.");

        // By name and from two different panels, because a count is what a sweep that opened one
        // extra panel and a sweep that opened all of them both satisfy. Neither the profiler nor the
        // debugger is in the default arrangement, and each is the whole of one panel's contribution.
        foreach (var pair in Reached) {
            Assert.Contains(pair, Opened, StringComparer.Ordinal);
            Assert.DoesNotContain(pair, Observed, StringComparer.Ordinal);
        }
    }

    /// <summary>The same census again, for an editor holding a document of every registered kind.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The third census, and where the domain actually lives.</b> Six passes of this
    ///         question reached 14 of 88 at best from source, and the two runtime sweeps before this
    ///         one reach 38 of the domain's 89 — because <b>most of what the editor sheets declare is
    ///         under an asset editor, which does not exist until a document of that kind is open.</b>
    ///         The opened census says exactly that in its own header and stops there; this is the
    ///         sweep that goes on, and it proves <b>77</b>. Of the eleven it still leaves unjudged,
    ///         one is proved by the controls' own sweep and the rest want an asset with content in it
    ///         or a menu somebody has to open.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Weaker standing than the second census, and it is the same ladder.</b> The bare
    ///         sweep is what a started editor builds; the opened one is what one builds after a
    ///         fixture asked the workspace for every panel; this one is what one builds after a
    ///         fixture also put a file of every registered extension on disk and opened it. Three
    ///         files rather than one so that no existing row changes meaning.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Still nothing nested by the harness.</b> The fixture writes an empty file and
    ///         asks the editor to open it — every element under the resulting panel is built by the
    ///         asset editor's own factory, exactly as a panel's contents are. There is no
    ///         harness-built pairing to refuse, which is what separates this from the controls'
    ///         seeded sweep.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And still a set of PROOFS.</b> Every file is empty, so a part an editor builds
    ///         only for content that is there — a sprite sheet's rectangles, a palette's rows — is
    ///         unproved and stays unjudged. A row leaving is the loud direction, for the reason the
    ///         first census gives.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_declared_pairing_an_editor_holding_every_document_kind_builds_is_in_the_committed_census() {
        var path = Path.Combine(Root(), DocumentCensusFile);

        if (Regenerating) {
            Write(path, Documents.Order(StringComparer.Ordinal));
        }

        var census = Census(path, DocumentCensusFile);

        var arrived = Documents.Where(pair => !census.Contains(pair)).Order(StringComparer.Ordinal).ToList();
        var departed = census.Where(pair => !Documents.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        Assert.True(
            arrived.Count == 0 && departed.Count == 0,
            $"""
             The document-holding editor's census of proved pairings is out of date.

             Built and not in {DocumentCensusFile} — regenerate once you have read them:
             {Lines(arrived)}

             ⚠ In {DocumentCensusFile} and NO LONGER BUILT — a sheet still declares each of these and
             the editor stopped growing it, with a document of its kind open:
             {Lines(departed)}

             Re-run with VIXEN_REGENERATE=1 to write this back, after reading the second list.
             """
        );
    }

    /// <summary>Opening a document of every kind reached parts no panel builds.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument for the third sweep, and its quiet failure is the one worth
    ///         naming.</b> If <c>OpenAsset</c> stopped building views — a factory that throws into a
    ///         load-diagnostics placeholder, an asset database that stopped seeing new files — this
    ///         sweep would degrade to the opened one and its census would be exactly satisfied by
    ///         whatever was regenerated that day. So what is asserted is the <em>difference</em>,
    ///         and by name from three separate asset editors, because one editor still working
    ///         satisfies any count.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A strict superset, and a lost row is a finding rather than an update.</b> An
    ///         open document must not tear a panel's contents down; a row in the opened census and
    ///         not here means something did.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Opening_a_document_of_every_kind_proves_more_than_opening_every_panel() {
        var lost = Opened.Where(pair => !Documents.Contains(pair)).Order(StringComparer.Ordinal).ToList();

        // After the line above and not before it, for `asked`'s reason: `authored` is written by the
        // sweep and the sweep is lazy, so reading it first reports "this editor registers no asset
        // editors" when what happened is that nothing has run yet.
        Assert.True(authored >= 25, $"the sweep opened {authored} documents, which is not this editor's registry.");

        Assert.True(lost.Count == 0, $"opening documents LOST pairings an opened editor builds:\n{Lines(lost)}");

        foreach (var pair in Authored) {
            Assert.Contains(pair, Documents, StringComparer.Ordinal);
            Assert.DoesNotContain(pair, Opened, StringComparer.Ordinal);

            var halves = pair.Split(" > ");

            Assert.DoesNotContain($"{halves[1]} > {halves[0]}", Documents, StringComparer.Ordinal);
        }
    }

    /// <summary>Every row is a pairing some committed sheet actually declares.</summary>
    /// <remarks>
    ///     ⚠ The confinement is what makes the file worth reading, and it is asserted rather than
    ///     assumed: without this a walk that stopped intersecting with the domain would commit a
    ///     thousand rows of editor plumbing and the two directions above would stop meaning anything.
    /// </remarks>
    [Fact]
    public void Every_proved_pairing_is_one_a_sheet_declares() {
        var domain = Domain(Path.Combine(Root(), DomainFile));
        var stray = Observed.Concat(Opened).Concat(Documents)
            .Where(pair => !domain.Contains(pair))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(stray.Count == 0, $"proved but declared by no sheet:\n{Lines(stray)}");

        // And the join has a second way of coming out empty: a domain read as a header, or two sets
        // that have stopped talking about the same tags.
        Assert.True(domain.Count >= 60, $"the domain is {domain.Count} rows, which is not the committed one.");
    }

    /// <summary>Starts an editor and reads the tree that grew, with nothing poked.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>No panel is opened, and that is measured rather than a preference.</b> The first
    ///         version of this called <c>fixture.Open("scene")</c> so that the viewport's chrome would
    ///         exist; deleting the call changed not one row, because the default layout already opens
    ///         the scene. So the census is what a started editor builds, with no state a fixture put
    ///         it in — the stronger standing of the two, and the one the controls' bare sweep is
    ///         careful to keep separate from its seeded neighbour.
    ///     </para>
    ///     <para>
    ///         Nothing here nests anything, which is what makes every row the editor's own
    ///         construction rather than the harness's: a walk of a tree somebody else built cannot
    ///         accidentally prove <c>A &gt; B</c> by putting a B under an A.
    ///     </para>
    /// </remarks>
    static HashSet<string> Sweep(Depth depth) {
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        using (var fixture = EditorSession.Start()) {
            fixture.Settle();

            if (depth != Depth.Started) {
                // ⚠ Read before the loop, because opening a panel can register another one — an
                // asset document panel is registered the moment the browser opens it — and
                // enumerating a collection the loop is growing is the difference between a census
                // and an exception.
                var registered = fixture.Shell.Workspace.Panels.Select(static panel => panel.Id).ToList();

                asked = registered.Count;

                foreach (var id in registered) {
                    fixture.Open(id);
                }
            }

            if (depth == Depth.Documents) {
                Author(fixture);
            }

            Walk(fixture.Document.Root, pairs);
        }

        if (depth == Depth.Started) {
            walked = pairs.Count;
        }

        var domain = Domain(Path.Combine(Root(), DomainFile)).ToHashSet(StringComparer.Ordinal);

        pairs.IntersectWith(domain);

        return pairs;
    }

    /// <summary>How much of the editor a sweep has woken up before it reads the tree.</summary>
    /// <remarks>
    ///     Three states rather than two booleans, because they are ordered: each does everything the
    ///     one before it does and then more, which is what makes the "nothing was lost" assertions
    ///     between the three censuses mean anything.
    /// </remarks>
    enum Depth {
        /// <summary>A started editor, with nothing a fixture put it in.</summary>
        Started,

        /// <summary>Every panel the workspace registers, opened.</summary>
        Panels,

        /// <summary>That, and a document of every asset-editor kind the editor registers.</summary>
        Documents,
    }

    /// <summary>
    ///     Creates one asset for every extension the editor's own registry claims, and opens each in
    ///     whatever editor claims it.
    /// </summary>
    /// <param name="fixture">The running editor.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The registered registry, not a second one built the same way.</b>
    ///         <c>StandardEditors.CreateWorldless()</c> would enumerate the same names and would
    ///         still agree with itself on the day this editor stopped registering one of them — the
    ///         same argument the opening sweep makes for reading the workspace's own panel list.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Empty files, and that is a limit on what the census proves rather than a
    ///         shortcut.</b> Every document here loads its own defaults, so a part an editor only
    ///         builds once its asset has content in it — a sprite's rectangles, a palette's rows —
    ///         is unproved by this sweep and stays unjudged. What an empty file buys is that the
    ///         editor's <em>own</em> factory builds the whole view, with nothing the fixture nested.
    ///     </para>
    ///     <para>
    ///         One file per <em>extension</em> rather than per factory: an editor is chosen by
    ///         extension, and two extensions on one factory are two chances for it to build a
    ///         different view — the markup editor's <c>.vxml</c> and <c>.vcss</c> are exactly that.
    ///     </para>
    /// </remarks>
    static void Author(EditorSession fixture) {
        var made = 0;

        foreach (var factory in fixture.Editor.Editors.Editors) {
            foreach (var extension in factory.Extensions) {
                var path = Path.Combine(fixture.Project.Paths.Assets, "CombinatorProbe" + made + extension);

                File.WriteAllText(path, string.Empty);
                fixture.Project.Assets.Scan();

                // Loud rather than skipped: a file the database does not see is a sweep that opened
                // fewer editors than it says it did, and `authored` below is what reports the count.
                Assert.True(
                    fixture.Project.Assets.TryGetByPath(fixture.Project.Paths.Relative(path), out var entry),
                    $"the asset database did not see the '{extension}' probe it was just handed."
                );

                fixture.Editor.OpenAsset(entry!.Guid);
                fixture.Settle();

                made++;
            }
        }

        authored = made;
    }

    static void Walk(UiElement element, HashSet<string> into) {
        foreach (var child in element.Children) {
            into.Add($"{element.Tag} > {child.Tag}");
            Walk(child, into);
        }
    }

    static List<string> Domain(string path) => Rows(path, DomainFile).Select(static row => row.Split('\t')[0].Trim()).ToList();

    static HashSet<string> Census(string path, string name) => Rows(path, name).ToHashSet(StringComparer.Ordinal);

    static List<string> Rows(string path, string name) {
        var lines = File.ReadAllLines(path);

        // "It has rows" cannot stand in for "it was read": a truncated file and an editor that builds
        // nothing are both zero rows, and only one of them still has the header.
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

    static string Lines(IEnumerable<string> rows) {
        var joined = string.Join("\n", rows.Select(static row => $"  {row}"));

        return joined.Length == 0 ? "  (none)" : joined;
    }

    /// <summary>
    ///     The working tree's root, found by a directory only it has.
    /// </summary>
    /// <remarks>
    ///     ⚠ Walked up from <see cref="AppContext.BaseDirectory" /> and never from a
    ///     <c>[CallerFilePath]</c>: CI sets <c>ContinuousIntegrationBuild</c>, which turns on
    ///     <c>DeterministicSourcePaths</c>, which rewrites every compiled source path to <c>/_/…</c>
    ///     — so a repository walk anchored on this file's own path fails on all three runners at once
    ///     and passes on a developer's machine.
    /// </remarks>
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
