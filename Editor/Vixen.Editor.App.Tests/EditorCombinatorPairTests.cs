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

    /// <summary>How many declared pairings the editor is expected to prove, at least.</summary>
    /// <remarks>
    ///     Under the measured number rather than at it, so that a panel gaining a part is not a
    ///     failing test. Its job is the day the walk stops walking: a sweep that reads an empty tree
    ///     observes nothing, and an empty observation agrees with an empty census perfectly.
    /// </remarks>
    const int Floor = 16;

    /// <summary>Every parent→child tag pairing a started editor grows, done once for the class.</summary>
    static IReadOnlySet<string> Observed => observed ??= Sweep();

    static IReadOnlySet<string>? observed;

    /// <summary>How many pairings the walk saw altogether, declared or not.</summary>
    static int walked;

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

        var census = Census(path);

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

    /// <summary>Every row is a pairing some committed sheet actually declares.</summary>
    /// <remarks>
    ///     ⚠ The confinement is what makes the file worth reading, and it is asserted rather than
    ///     assumed: without this a walk that stopped intersecting with the domain would commit a
    ///     thousand rows of editor plumbing and the two directions above would stop meaning anything.
    /// </remarks>
    [Fact]
    public void Every_proved_pairing_is_one_a_sheet_declares() {
        var domain = Domain(Path.Combine(Root(), DomainFile));
        var stray = Observed.Where(pair => !domain.Contains(pair)).Order(StringComparer.Ordinal).ToList();

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
    static HashSet<string> Sweep() {
        var pairs = new HashSet<string>(StringComparer.Ordinal);

        using (var fixture = EditorSession.Start()) {
            fixture.Settle();

            Walk(fixture.Document.Root, pairs);
        }

        walked = pairs.Count;

        var domain = Domain(Path.Combine(Root(), DomainFile)).ToHashSet(StringComparer.Ordinal);

        pairs.IntersectWith(domain);

        return pairs;
    }

    static void Walk(UiElement element, HashSet<string> into) {
        foreach (var child in element.Children) {
            into.Add($"{element.Tag} > {child.Tag}");
            Walk(child, into);
        }
    }

    static List<string> Domain(string path) => Rows(path, DomainFile).Select(static row => row.Split('\t')[0].Trim()).ToList();

    static HashSet<string> Census(string path) => Rows(path, CensusFile).ToHashSet(StringComparer.Ordinal);

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
