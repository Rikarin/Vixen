// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Ui.Styling;
using Xunit;

namespace Vixen.Ui.Controls.Advanced.Tests;

/// <summary>The arrangement, the elements that show it, and the round trip between them.</summary>
public class DockingTests {
    const float Tolerance = 0.5f;

    /// <summary>What the theme puts between a guide handle's edge and the marking inside it.</summary>
    /// <remarks>Its one-pixel border and its three of padding, which is why the room inside is 20 of 28.</remarks>
    const float Inset = 4f;

    [Fact]
    public void A_layout_round_trips_through_serialisation() {
        var layout = new DockLayout {
            Root = new DockSplitNode(
                Orientation.Horizontal,
                new DockGroupNode("hierarchy"),
                new DockSplitNode(
                    Orientation.Vertical,
                    new DockGroupNode("scene", "game") { Selected = 1 },
                    new DockGroupNode("console", "assets"),
                    0.7f
                ),
                0.25f
            )
        };

        layout.AddFloating(new DockFloat(new DockGroupNode("inspector"), 40f, 60f, 300f, 400f));

        var text = layout.Save();
        var reloaded = DockLayout.Load(text);

        // ⚠ The exit criterion doc 14 names for 4e, and it is asserted as a *fixed point* rather
        // than field by field: writing what was read has to give the same text back, which catches
        // everything a hand-written comparison would forget to look at.
        Assert.Equal(text, reloaded.Save());

        var split = Assert.IsType<DockSplitNode>(reloaded.Root);
        Assert.Equal(Orientation.Horizontal, split.Orientation);
        Assert.Equal(0.25f, split.Ratio, 0.001f);

        var inner = Assert.IsType<DockSplitNode>(split.Second);
        Assert.Equal(1, Assert.IsType<DockGroupNode>(inner.First).Selected);

        var floated = Assert.Single(reloaded.Floating);
        Assert.Equal(300f, floated.Width, 0.001f);
        Assert.Equal("inspector", Assert.Single(floated.Group.Panels));
    }

    [Fact]
    public void A_stale_layout_loads_as_far_as_it_goes() {
        // Two groups, one of which names nothing. It is what a saved arrangement becomes when a
        // plugin is uninstalled, and it must not be an exception in front of somebody opening a
        // project.
        var layout = DockLayout.Load("""
            root:
              orientation: horizontal
              ratio: 0.3
              first:
                panels: []
                selected: 0
              second:
                panels: [scene]
                selected: 0
            """);

        // The empty group vanished and the split collapsed into the half that survived.
        var group = Assert.IsType<DockGroupNode>(layout.Root);
        Assert.Equal("scene", Assert.Single(group.Panels));
    }

    [Fact]
    public void Nonsense_loads_as_an_empty_arrangement() {
        var layout = DockLayout.Load("not a mapping");

        Assert.Null(layout.Root);
        Assert.Empty(layout.Floating);
        Assert.Empty(layout.Groups());
    }

    [Fact]
    public void A_file_that_is_not_yaml_is_the_one_thing_that_throws() {
        // The line above this one is the contrast the rule turns on: "not a mapping" *parses*, and a
        // document that parses into something this does not understand is the emptiest stale layout
        // there is. Text no parser will read is not a stale layout at all — there is nothing in it to
        // salvage — and returning the same empty arrangement would tell a caller that the user's
        // panels were legitimately gone. This type has no diagnostics list to say otherwise, so the
        // exception is how the corruption reaches somebody who can report it.
        Assert.Throws<YamlParseException>(() => DockLayout.Load("root: [unclosed"));
    }

    [Fact]
    public void Docking_to_a_side_wraps_the_target_in_a_split() {
        var target = new DockGroupNode("scene");
        var layout = new DockLayout { Root = target };

        layout.Dock("hierarchy", target, DockZone.Left);

        var split = Assert.IsType<DockSplitNode>(layout.Root);
        Assert.Equal(Orientation.Horizontal, split.Orientation);
        Assert.Equal("hierarchy", Assert.Single(Assert.IsType<DockGroupNode>(split.First).Panels));
        Assert.Same(target, split.Second);
    }

    [Fact]
    public void Docking_below_splits_the_other_way_round() {
        var target = new DockGroupNode("scene");
        var layout = new DockLayout { Root = target };

        layout.Dock("console", target, DockZone.Bottom);

        var split = Assert.IsType<DockSplitNode>(layout.Root);
        Assert.Equal(Orientation.Vertical, split.Orientation);
        Assert.Same(target, split.First);
    }

    [Fact]
    public void Docking_into_the_middle_makes_it_another_tab() {
        var target = new DockGroupNode("scene");
        var layout = new DockLayout { Root = target };

        layout.Dock("game", target, DockZone.Center);

        Assert.Equal(["scene", "game"], target.Panels);
        Assert.Equal(1, target.Selected);
    }

    [Fact]
    public void Dropping_a_panel_back_where_it_came_from_does_not_duplicate_it() {
        var target = new DockGroupNode("scene", "game");
        var layout = new DockLayout { Root = target };

        layout.Dock("game", target, DockZone.Center);

        // ⚠ The commonest gesture in a docking host is the one that ends where it started, and the
        // removal-before-insertion is what makes it a no-op rather than a duplication.
        Assert.Equal(["scene", "game"], target.Panels);
    }

    [Fact]
    public void Moving_the_last_panel_out_of_a_group_collapses_the_split() {
        var left = new DockGroupNode("hierarchy");
        var right = new DockGroupNode("scene");
        var layout = new DockLayout { Root = new DockSplitNode(Orientation.Horizontal, left, right) };

        layout.Dock("hierarchy", right, DockZone.Center);

        // The left group emptied, so the split has one half and is therefore not a split.
        Assert.Same(right, layout.Root);
        Assert.Equal(["scene", "hierarchy"], right.Panels);
    }

    [Fact]
    public void Dropping_onto_a_group_that_the_move_dissolved_still_places_the_panel() {
        var only = new DockGroupNode("scene");
        var layout = new DockLayout { Root = only };

        // The panel being moved is the only thing in the group it is being dropped on, so the
        // removal takes the target out of the tree before the insertion runs.
        layout.Dock("scene", only, DockZone.Right);

        var group = Assert.IsType<DockGroupNode>(layout.Root);
        Assert.Equal("scene", Assert.Single(group.Panels));
    }

    [Fact]
    public void Floating_a_panel_takes_it_out_of_the_docked_tree() {
        var group = new DockGroupNode("scene", "console");
        var layout = new DockLayout { Root = group };

        layout.Float("console", 100f, 50f);

        Assert.Equal(["scene"], group.Panels);
        Assert.Equal("console", Assert.Single(Assert.Single(layout.Floating).Group.Panels));
    }

    [Fact]
    public void The_nearest_edge_decides_the_zone() {
        var bounds = new Rectangle(0f, 0f, 100f, 100f);

        Assert.Equal(DockZone.Center, DockingHost.ZoneOf(bounds, 50f, 50f));
        Assert.Equal(DockZone.Left, DockingHost.ZoneOf(bounds, 5f, 50f));
        Assert.Equal(DockZone.Right, DockingHost.ZoneOf(bounds, 95f, 50f));
        Assert.Equal(DockZone.Top, DockingHost.ZoneOf(bounds, 50f, 5f));
        Assert.Equal(DockZone.Bottom, DockingHost.ZoneOf(bounds, 50f, 95f));

        // ⚠ A corner is inside two margins. A chain of ifs would always answer with whichever was
        // written first, so dragging into the top-left corner would dock left however clearly the
        // pointer was aiming up.
        Assert.Equal(DockZone.Top, DockingHost.ZoneOf(bounds, 10f, 3f));
        Assert.Equal(DockZone.Left, DockingHost.ZoneOf(bounds, 3f, 10f));
    }

    [Fact]
    public void A_host_builds_a_group_per_node_and_a_tab_per_panel() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        host.AddPanel("game", "Game");
        fixture.Update();

        var group = Assert.Single(host.Groups);

        // ⚠ The tabs are in `Tabs`, which is inside `Strip` alongside the two overflow arrows. The
        // strip is the row; the list is the part of it that scrolls.
        Assert.Equal(2, group.Tabs.Children.Count);
        Assert.Equal(3, group.Strip.Children.Count);

        var tabs = group.Tabs.Children.OfType<DockTab>().ToList();
        Assert.Equal(["scene", "game"], tabs.Select(static tab => tab.PanelId));
        Assert.Equal("Scene", tabs[0].Label);
    }

    [Fact]
    public void Only_the_selected_panel_has_a_size() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var scene = host.AddPanel("scene", "Scene");
        var game = host.AddPanel("game", "Game");

        scene.Add("div").SetStyle("height", "40px");
        game.Add("div").SetStyle("height", "40px");

        fixture.Update();

        // ⚠ The *last* panel registered is the one showing. Adding a panel selects it, which is
        // what a caller means by adding one at run time — and what it means at start-up is that
        // the arrangement decides, which is the case the layout-before-panels test covers.
        Assert.True(game.Height > 0f);
        Assert.Equal(0f, scene.Height);

        var tabs = host.Groups[0].Tabs.Children.OfType<DockTab>().ToList();
        fixture.Click(tabs[0]);

        Assert.True(scene.Height > 0f);
        Assert.Equal(0f, game.Height);
    }

    [Fact]
    public void A_panel_moved_between_groups_keeps_what_was_in_it() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var scene = host.AddPanel("scene", "Scene");
        host.AddPanel("console", "Console");

        var field = scene.Add<TextBox>();
        field.Value = "half typed";
        fixture.Update();

        var target = host.Layout.Find("console")!.Value.Group;
        host.Dock("scene", target, DockZone.Bottom);
        fixture.Update();

        // ⚠ The whole reason `Reparent` exists. Same instance, same value — a host that rebuilt the
        // panel would pass every structural test in this file and lose the user's work.
        Assert.Same(scene, host.Panels["scene"]);
        Assert.Same(field, scene.Children[0]);
        Assert.Equal("half typed", field.Value);
    }

    [Fact]
    public void A_split_gives_each_half_the_ratio_it_asked_for() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("left");
        host.AddPanel("right");
        fixture.Update();

        var target = host.Layout.Find("left")!.Value.Group;
        host.Dock("right", target, DockZone.Right);
        fixture.Update();

        var split = Assert.IsType<DockSplitNode>(host.Layout.Root);
        split.Ratio = 0.25f;

        var first = host.Groups[0];
        var second = host.Groups[1];

        DockSplitterView.Apply(first, second, 0.25f);
        fixture.Update();

        // The splitter is six pixels of the eight hundred, and the rest is shared a quarter to
        // three quarters.
        var span = 800f - 6f;
        Assert.Equal(span * 0.25f, first.Width, 1f);
        Assert.Equal(span * 0.75f, second.Width, 1f);
    }

    [Fact]
    public void Dragging_a_splitter_moves_it_without_rebuilding_anything() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("left");
        host.AddPanel("right");

        var target = host.Layout.Find("left")!.Value.Group;
        host.Dock("right", target, DockZone.Right);
        fixture.Update();

        var first = host.Groups[0];
        var splitter = Assert.Single(host.Surface.Children[0].Children.OfType<DockSplitterView>());

        fixture.Press(splitter.Bounds.X + 3f, 300f);
        fixture.Move(200f, 300f);
        fixture.Release(200f, 300f);

        var split = Assert.IsType<DockSplitNode>(host.Layout.Root);
        Assert.Equal(200f / (800f - 6f), split.Ratio, 0.01f);
        Assert.Equal(200f, first.Width, 2f);

        // Nothing was rebuilt, so the element the drag started on is still the element it moved.
        Assert.Same(first, host.Groups[0]);
    }

    [Fact]
    public void A_splitter_cannot_be_dragged_to_nothing() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("left");
        host.AddPanel("right");

        var target = host.Layout.Find("left")!.Value.Group;
        host.Dock("right", target, DockZone.Right);
        fixture.Update();

        var splitter = Assert.Single(host.Surface.Children[0].Children.OfType<DockSplitterView>());

        fixture.Press(splitter.Bounds.X + 3f, 300f);
        fixture.Move(-500f, 300f);
        fixture.Release(-500f, 300f);

        // A half dragged to nothing is a panel with no way back, because the splitter that would
        // bring it back is against the edge.
        Assert.Equal(DockSplitNode.MinimumRatio, Assert.IsType<DockSplitNode>(host.Layout.Root).Ratio, 0.001f);
    }

    [Fact]
    public void Dragging_a_tab_shows_a_preview_and_drops_the_panel_where_it_pointed() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        host.AddPanel("console", "Console");
        fixture.Update();

        var tab = host.Groups[0].Tabs.Children.OfType<DockTab>().First(static t => t.PanelId == "console");
        var bounds = host.Groups[0].Bounds;

        fixture.Press(tab.Bounds.X + 4f, tab.Bounds.Y + 4f);
        fixture.Move(bounds.X + (bounds.Width * 0.5f), bounds.Bottom - 10f);
        fixture.Move(bounds.X + (bounds.Width * 0.5f), bounds.Bottom - 10f);

        Assert.False(host.Preview.HasClass("hidden"));

        fixture.Release(bounds.X + (bounds.Width * 0.5f), bounds.Bottom - 10f);

        Assert.True(host.Preview.HasClass("hidden"));

        var split = Assert.IsType<DockSplitNode>(host.Layout.Root);
        Assert.Equal(Orientation.Vertical, split.Orientation);
        Assert.Equal("console", Assert.Single(Assert.IsType<DockGroupNode>(split.Second).Panels));
    }

    [Fact]
    public void A_tab_is_dragged_by_its_title_and_not_only_by_its_padding() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        host.AddPanel("console", "Console");
        fixture.Update();

        var group = host.Groups[0];
        var tab = group.Tabs.Children.OfType<DockTab>().First(static t => t.PanelId == "console");
        var (x, y) = AdvancedFixture.Centre(tab.Children[0]);

        // ⚠ The whole of this fixture. A press lands on the deepest element under it, and a tab's
        // title is a child element — it has to be, or a tab could not also have an icon — so the
        // drag is reported from the label rather than from the tab. A host that asked whether the
        // source *was* a tab left only the few pixels of padding around the words draggable, which
        // is what users report as docking being broken.
        Assert.Same(tab.Children[0], fixture.Document.HitTest(x, y));

        var bounds = group.Bounds;
        var dropX = bounds.X + (bounds.Width * 0.5f);
        var dropY = bounds.Bottom - 10f;

        fixture.Press(x, y);
        fixture.Move(dropX, dropY);
        fixture.Move(dropX, dropY);

        Assert.False(host.Preview.HasClass("hidden"));

        fixture.Release(dropX, dropY);

        var split = Assert.IsType<DockSplitNode>(host.Layout.Root);
        Assert.Equal(Orientation.Vertical, split.Orientation);
        Assert.Equal("console", Assert.Single(Assert.IsType<DockGroupNode>(split.Second).Panels));
    }

    [Fact]
    public void A_drag_begun_on_the_close_button_moves_nothing() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        host.AddPanel("console", "Console");
        fixture.Update();

        var group = host.Groups[0];
        var tab = group.Tabs.Children.OfType<DockTab>().First(static t => t.PanelId == "console");
        var (x, y) = AdvancedFixture.Centre(tab.CloseButton!);

        var bounds = group.Bounds;
        var dropX = bounds.X + (bounds.Width * 0.5f);
        var dropY = bounds.Bottom - 10f;

        fixture.Press(x, y);
        fixture.Move(dropX, dropY);
        fixture.Move(dropX, dropY);

        // ⚠ The close button is inside the tab, so the walk that finds the tab from the title would
        // find it from here too. It stops instead: a press on the button that wandered a few pixels
        // before letting go would dock the panel somewhere rather than do the one thing its icon
        // promises.
        Assert.True(host.Preview.HasClass("hidden"));

        fixture.Release(dropX, dropY);

        // And the drag was not a tap either, so the panel is neither moved nor closed.
        Assert.Equal(["scene", "console"], Assert.IsType<DockGroupNode>(host.Layout.Root).Panels);
        Assert.Contains("console", host.Panels.Keys);
    }

    [Fact]
    public void The_guides_sit_in_the_middle_and_beat_the_edge_they_are_nowhere_near() {
        var bounds = new Rectangle(0f, 0f, 400f, 300f);

        Assert.Equal(DockZone.Center, DockingHost.GuideAt(bounds, 200f, 150f));
        Assert.Equal(DockZone.Left, DockingHost.GuideAt(bounds, 200f - 32f, 150f));
        Assert.Equal(DockZone.Right, DockingHost.GuideAt(bounds, 200f + 32f, 150f));
        Assert.Equal(DockZone.Top, DockingHost.GuideAt(bounds, 200f, 150f - 32f));
        Assert.Equal(DockZone.Bottom, DockingHost.GuideAt(bounds, 200f, 150f + 32f));

        // The gaps between the handles are misses, not near-misses rounded to the nearest one.
        Assert.Null(DockingHost.GuideAt(bounds, 200f - 16f, 150f - 16f));
        Assert.Null(DockingHost.GuideAt(bounds, 10f, 10f));

        // ⚠ The point of having them: the middle of a pane means "stack it here" by proximity, and
        // a handle in the middle of the pane says something else. Aiming at one is never the same
        // gesture as aiming at an edge, which is why they can be believed over it.
        Assert.Equal(DockZone.Center, DockingHost.ZoneOf(bounds, 232f, 150f));
        Assert.Equal(DockZone.Right, DockingHost.ZoneAt(bounds, 232f, 150f));

        // And a pane with no room for the cluster is docked into exactly as it was before the
        // handles existed, rather than being offered handles hanging over its neighbours.
        var narrow = new Rectangle(0f, 0f, 60f, 60f);

        Assert.Null(DockingHost.GuideAt(narrow, 30f, 30f));
        Assert.Equal(DockingHost.ZoneOf(narrow, 30f, 30f), DockingHost.ZoneAt(narrow, 30f, 30f));
    }

    [Fact]
    public void Dragging_over_a_group_offers_guides_and_the_one_aimed_at_wins() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        host.AddPanel("console", "Console");
        fixture.Update();

        Assert.True(host.Guides.HasClass("hidden"));

        var group = host.Groups[0];
        var tab = group.Tabs.Children.OfType<DockTab>().First(static t => t.PanelId == "console");
        var (x, y) = AdvancedFixture.Centre(tab.Children[0]);

        var handle = DockingHost.GuideBounds(group.Bounds, DockZone.Right);
        var overX = handle.X + (handle.Width * 0.5f);
        var overY = handle.Y + (handle.Height * 0.5f);

        fixture.Press(x, y);
        fixture.Move(overX, overY);
        fixture.Move(overX, overY);

        Assert.False(host.Guides.HasClass("hidden"));

        // Drawn where the arithmetic that answers for the drop says it is. Nothing hit-tests these
        // — the pointer is captured by the tab for the whole drag — so a cluster drawn anywhere
        // else would be a set of handles that lie about what aiming at them does.
        var lit = Assert.Single(host.Guides.Children, static guide => guide.HasClass("active"));

        Assert.True(lit.HasClass("right"));
        Assert.Equal(handle.X, lit.AbsoluteLeft, Tolerance);
        Assert.Equal(handle.Y, lit.AbsoluteTop, Tolerance);

        // ⚠ And every handle's marking is *inside* it. A handle's width is its border box, so a
        // hint sized as though that width were the room inside overhangs the border along the
        // bottom and the right — which nothing clips, and which reads as a cluster with all five
        // markings nudged up and to the left.
        foreach (var guide in host.Guides.Children) {
            var hint = Assert.Single(guide.Children);

            Assert.True(hint.AbsoluteLeft >= guide.AbsoluteLeft + Inset - Tolerance, "a hint starts left of its handle");
            Assert.True(hint.AbsoluteTop >= guide.AbsoluteTop + Inset - Tolerance, "a hint starts above its handle");
            Assert.True(
                hint.AbsoluteLeft + hint.Width <= guide.AbsoluteLeft + guide.Width - Inset + Tolerance,
                "a hint runs past the right of its handle"
            );
            Assert.True(
                hint.AbsoluteTop + hint.Height <= guide.AbsoluteTop + guide.Height - Inset + Tolerance,
                "a hint runs past the bottom of its handle"
            );
        }

        fixture.Release(overX, overY);

        Assert.True(host.Guides.HasClass("hidden"));

        // The pointer was in the middle of the group, where proximity would have said "stack it
        // here". It went to the right because that is the handle it was over.
        var split = Assert.IsType<DockSplitNode>(host.Layout.Root);

        Assert.Equal(Orientation.Horizontal, split.Orientation);
        Assert.Equal("console", Assert.Single(Assert.IsType<DockGroupNode>(split.Second).Panels));
    }

    [Fact]
    public void A_strip_with_more_tabs_than_fit_gets_arrows_and_scrolls() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();

        for (var i = 0; i < 12; i++) {
            host.AddPanel($"panel{i}", $"A Panel Named {i}");
        }

        fixture.Update();

        var group = Assert.Single(host.Groups);

        // ⚠ A group is how many panels somebody stacked into one place, and that is unbounded.
        // Without somewhere for the tabs to go, flexbox either shrinks every one until none of the
        // titles can be read or pushes the last of them out of the box — and in both cases the panels
        // on the end are ones the user cannot get back to.
        Assert.True(group.Overflows, "twelve titled tabs fit in the strip, so this fixture proves nothing");
        Assert.False(group.Previous.HasClass("hidden"));
        Assert.False(group.Next.HasClass("hidden"));

        // ⚠ Wound back first, because the strip does *not* start at zero: the last panel registered
        // is the selected one and its tab has already been scrolled into view. Asserting from here
        // rather than from wherever that left it is what keeps this fixture about the arrows.
        group.ScrollTo(0f);

        Assert.True(group.Previous.Disabled);
        Assert.False(group.Next.Disabled);

        fixture.Click(group.Next);

        Assert.True(group.ScrollLeft > 0f);
        Assert.False(group.Previous.Disabled);

        // And it stops at the end rather than scrolling into empty space.
        for (var press = 0; press < 20; press++) {
            fixture.Click(group.Next);
        }

        Assert.Equal(group.MaximumScroll, group.ScrollLeft, 2);
        Assert.True(group.Next.Disabled);
    }

    [Fact]
    public void A_strip_whose_tabs_fit_has_no_arrows() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        fixture.Update();

        var group = Assert.Single(host.Groups);

        Assert.False(group.Overflows);
        Assert.True(group.Previous.HasClass("hidden"));
        Assert.True(group.Next.HasClass("hidden"));
    }

    [Fact]
    public void Selecting_a_panel_scrolls_its_tab_into_view() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();

        for (var i = 0; i < 12; i++) {
            host.AddPanel($"panel{i}", $"A Panel Named {i}");
        }

        fixture.Update();

        var group = Assert.Single(host.Groups);

        Assert.True(group.Overflows);

        // The last panel registered is the selected one, and its tab is the one off the end. A strip
        // that showed the selected panel's body while its tab sat past the edge reads as the
        // selection having been lost.
        Assert.True(group.ScrollLeft > 0f, "the selected tab was not scrolled into view");

        var tab = group.Tabs.Children.OfType<DockTab>().Last();

        Assert.True(tab.AbsoluteLeft >= group.Tabs.Parent!.AbsoluteLeft - Tolerance);
        Assert.True(
            tab.AbsoluteLeft + tab.Width <= group.Tabs.Parent!.AbsoluteLeft + group.Tabs.Parent!.Width + Tolerance
        );
    }

    [Fact]
    public void Closing_a_tab_takes_the_panel_out_of_the_document() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene", "Scene");
        var console = host.AddPanel("console", "Console");
        fixture.Update();

        var tab = host.Groups[0].Tabs.Children.OfType<DockTab>().First(static t => t.PanelId == "console");
        fixture.Click(tab.CloseButton!);

        Assert.True(console.IsRemoved);
        Assert.DoesNotContain("console", host.Panels.Keys);
        Assert.Null(host.Layout.Find("console"));
    }

    [Fact]
    public void A_panel_that_may_not_be_closed_has_no_close_button() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var panel = host.AddPanel("scene", "Scene");

        panel.CanClose = false;
        host.Rebuild();

        var tab = Assert.Single(host.Groups[0].Tabs.Children.OfType<DockTab>());
        Assert.Null(tab.CloseButton);
    }

    [Fact]
    public void A_layout_loaded_before_the_panels_exist_still_places_them() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();

        // The order every application does it in: the arrangement comes off disk long before the
        // code that builds the panels has run.
        host.Load("""
            root:
              orientation: horizontal
              ratio: 0.3
              first:
                panels: [hierarchy]
                selected: 0
              second:
                panels: [scene]
                selected: 0
            """);

        host.AddPanel("scene", "Scene");
        host.AddPanel("hierarchy", "Hierarchy");
        fixture.Update();

        Assert.Equal(2, host.Groups.Count);
        Assert.Equal("hierarchy", host.Groups[0].Node!.Panels[0]);
        Assert.Equal("scene", host.Groups[1].Node!.Panels[0]);
    }

    [Fact]
    public void A_floating_group_is_positioned_and_sized_where_it_was_told() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene");
        host.AddPanel("inspector");

        host.Float("inspector", 120f, 80f, 240f, 160f);
        fixture.Update();

        var window = host.Children.Single(static child => child.Tag == "dock-float");

        Assert.Equal(120f, window.AbsoluteLeft, Tolerance);
        Assert.Equal(80f, window.AbsoluteTop, Tolerance);
        Assert.Equal(240f, window.Width, Tolerance);
        Assert.Equal(160f, window.Height, Tolerance);
    }

    [Fact]
    public void The_host_reports_every_change_once_it_has_been_made() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        var changes = 0;

        host.LayoutChanged += _ => changes++;
        host.AddPanel("scene");

        Assert.Equal(1, changes);

        // Reported after the rebuild, so a handler that saves the layout saves the one on screen.
        host.LayoutChanged += saved => Assert.NotEmpty(saved.Save());
        host.AddPanel("game");
    }

    [Fact]
    public void The_selected_tab_is_checked_so_the_theme_can_see_it() {
        using var fixture = new AdvancedFixture();

        var host = fixture.Add<DockingHost>();
        host.AddPanel("scene");
        host.AddPanel("game");
        fixture.Update();

        var tabs = host.Groups[0].Tabs.Children.OfType<DockTab>().ToList();

        Assert.Equal(ElementState.None, tabs[0].State & ElementState.Checked);
        Assert.True((tabs[1].State & ElementState.Checked) != 0);
    }
}
