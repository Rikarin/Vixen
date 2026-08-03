// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>A move set is a table, and the filter box on it is a query.</summary>
public class MoveSetTests {
    [Fact]
    public void EveryEditIsOneUndoEntry() {
        using var project = new EditorFixture();
        var document = Open(project);

        var walk = document.Add(new() { Name = "walk", Clip = "walk.vxanim", Speed = 1.4f });

        document.SetFacets(walk, "role=loop style=neutral");
        document.SetField(walk, "Set Speed", static row => row.Speed, static (row, speed) => row.Speed = speed, 1.6f);

        Assert.Equal("walk", Assert.Single(document.Set.Entries).Name);
        Assert.Equal(1.6f, walk.Speed, 3);
        Assert.Equal(2, walk.Facets.Count);

        document.Stack.Undo();
        Assert.Equal(1.4f, walk.Speed, 3);

        document.Stack.Undo();
        Assert.Empty(walk.Facets);

        document.Stack.Undo();
        Assert.Empty(document.Set.Entries);
    }

    /// <summary>⚠ Half-parsed facets would leave a row carrying some of what somebody typed.</summary>
    [Fact]
    public void AMalformedFacetIsRefusedWholesale() {
        using var project = new EditorFixture();
        var document = Open(project);

        var walk = document.Add(new() { Name = "walk" });

        Assert.True(document.SetFacets(walk, "role=loop style=injured"));
        Assert.Equal(2, walk.Facets.Count);

        Assert.False(document.SetFacets(walk, "role=loop style="));
        Assert.Equal(2, walk.Facets.Count);

        Assert.False(document.SetFacets(walk, "role=loop nonsense"));
        Assert.Equal(2, walk.Facets.Count);
    }

    /// <summary>⚠ Order is the whole of what a rule list means, so reordering is an undoable edit.</summary>
    [Fact]
    public void ReorderingARuleIsAnEditAndNotATidyUp() {
        using var project = new EditorFixture();
        var document = Open(project);

        var first = document.AddRule(new() { Duration = 0.1f });
        var second = document.AddRule(new() { Duration = 0.2f });

        Assert.True(document.MoveRule(second, -1));
        Assert.Same(second, document.Set.Rules[0]);

        document.Stack.Undo();
        Assert.Same(first, document.Set.Rules[0]);

        Assert.False(document.MoveRule(first, -1), "there is nowhere above the first rule");
    }

    // ── The query ────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ <b>The same query the runtime would build, not a similar one.</b> The whole claim of the
    ///     filter box is that what it shows is what the game would pick.
    /// </summary>
    [Fact]
    public void TheFilterBoxIsAQuery() {
        var query = Assert.NotNull(MoveQueryText.Parse("role=loop style=injured*2 speed:3.5 turn:-0.4"));

        Assert.True(query.Required.Contains(Facet.Of("role", "loop")));
        Assert.False(query.Required.Contains(Facet.Of("style", "injured")), "a weighted facet is a preference");

        Assert.NotNull(query.Preferred);

        var preferred = Assert.Single(query.Preferred);

        Assert.Equal(Facet.Of("style", "injured"), preferred.Facet);
        Assert.Equal(2f, preferred.Weight, 3);
        Assert.Equal(3.5f, query.Numeric.Speed, 3);
        Assert.Equal(-0.4f, query.Numeric.TurnRate, 3);
    }

    /// <summary>An empty box is not an empty query — nothing was asked, so nothing is filtered.</summary>
    [Fact]
    public void AnEmptyBoxAsksNothingRatherThanAskingForNothing() {
        Assert.Null(MoveQueryText.Parse(string.Empty));
        Assert.Null(MoveQueryText.Parse("   "));
        Assert.Null(MoveQueryText.Parse("garbage with no pairs"));
    }

    [Fact]
    public void TheTableRanksAndExplainsWhatTheQueryWouldPick() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        Walk(document);

        var view = harness.Ui.Document.Root.Add<MoveSetView>();
        view.Show(document);

        harness.Ui.Frame();

        view.Filter.Value = "role=loop speed:3.4";
        harness.Ui.Frame();

        Assert.NotEmpty(view.Ranked);

        // The run is what a query for 3.4 m/s should reach for, and the idle is filtered out.
        Assert.Equal("run", view.Ranked[0].Name);
        Assert.True(view.Ranked[0].Eligible);
        Assert.Contains(view.Ranked, entry => entry is { Name: "idle", Eligible: false });
    }

    /// <summary>
    ///     The breakdown, which is the single most valuable thing in the panel — and its silence,
    ///     which means as much: a move with no terms is one nothing counted against.
    /// </summary>
    [Fact]
    public void TheBreakdownNamesEveryTermAndSaysNothingWhenNothingCounted() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        Walk(document);

        var view = harness.Ui.Document.Root.Add<MoveSetView>();
        view.Show(document);

        // 3.4 m/s is inside the run's own retiming range, so the run is a clean match and there is
        // genuinely nothing to say about it. An empty breakdown here is the honest answer.
        view.Filter.Value = "role=loop speed:3.4";
        harness.Ui.Frame();

        Assert.Empty(Assert.Single(view.Ranked, entry => entry.Name == "run").Terms);

        // The walk cannot be stretched anywhere near it, and the panel says so in metres a second.
        var walk = Assert.Single(view.Ranked, entry => entry.Name == "walk");
        var penalty = Assert.Single(walk.Terms);

        Assert.True(penalty.Amount < 0f);
        Assert.Contains("m/s", penalty.Reason, StringComparison.Ordinal);

        // And a preference is a term with its own weight, on the plus side.
        view.Filter.Value = "role=loop style=neutral*2 speed:3.4";
        harness.Ui.Frame();

        Assert.Contains(
            view.Ranked,
            entry => entry.Terms.Any(static term => term.Amount > 0f)
        );
    }

    /// <summary>
    ///     ⚠ <b>A base row this set replaces is shown struck through, not hidden.</b> It is still in
    ///     somebody's file, and hiding it is how an author edits a row that has no effect.
    /// </summary>
    [Fact]
    public void AnOverriddenBaseRowIsShownAndMarked() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        var baseline = new MoveSetContent {
            Name = "locomotion",
            Entries = [
                new() { Name = "walk", Clip = "walk.vxanim", Speed = 1.4f },
                new() { Name = "run", Clip = "run.vxanim", Speed = 3.6f }
            ]
        };

        document.Set.Bases.Add("Assets/locomotion.vxmoveset");
        document.Resolve = _ => baseline;

        // This set replaces the walk and leaves the run alone.
        document.Add(new() { Name = "walk", Clip = "limp.vxanim", Speed = 0.9f });

        var view = harness.Ui.Document.Root.Add<MoveSetView>();
        view.Show(document);

        harness.Ui.Frame();

        // Header, the two base rows, and this set's own.
        Assert.Equal(4, view.Table.Children.Count);

        var walk = view.Table.Children[1];
        var run = view.Table.Children[2];

        Assert.True(walk.HasClass("overridden"), "the base walk is replaced by this set's");
        Assert.True(walk.HasClass("inherited"));
        Assert.False(run.HasClass("overridden"), "nothing here replaces the run");
        Assert.Contains("locomotion", walk.Children[0].Text, StringComparison.Ordinal);
    }

    /// <summary>Coverage: the regions of the query space this set has nothing for.</summary>
    [Fact]
    public void TheCoverageSweepNamesWhatTheSetCannotAnswer() {
        using var harness = new ViewHarness();
        var document = Open(harness.Project);

        // A set with a loop and nothing else: every other role falls back.
        document.Add(new() { Name = "walk", Clip = "walk.vxanim", Speed = 1.4f, MinRate = 0.9f, MaxRate = 1.1f });
        document.SetFacets(document.Set.Entries[0], "role=loop");

        var view = harness.Ui.Document.Root.Add<MoveSetView>();
        view.Show(document);

        view.Coverage.IsChecked = true;
        harness.Ui.Frame();

        // A header plus a row for every region that falls back, and there are plenty.
        Assert.True(view.Table.Children.Count > 1, "a one-move set does not cover the query space");
        Assert.Contains(view.Table.Children.Skip(1), row => row.HasClass("missing"));
    }

    static void Walk(MoveSetDocument document) {
        var idle = document.Add(new() { Name = "idle", Clip = "idle.vxanim" });
        var walk = document.Add(new() { Name = "walk", Clip = "walk.vxanim", Speed = 1.4f, MinRate = 0.85f, MaxRate = 1.15f });
        var run = document.Add(new() { Name = "run", Clip = "run.vxanim", Speed = 3.6f, MinRate = 0.8f, MaxRate = 1.2f });

        document.SetFacets(idle, "role=idle");
        document.SetFacets(walk, "role=loop style=neutral");
        document.SetFacets(run, "role=loop style=neutral");
    }

    static MoveSetDocument Open(EditorFixture project) {
        var path = project.WriteAsset("Assets/locomotion.vxmoveset", string.Empty);

        return new(project.Project, AssetId.New(), path);
    }
}
