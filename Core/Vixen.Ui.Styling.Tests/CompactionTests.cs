// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Ui.Styling.Tests;

/// <summary>
///     Reclaiming the slots removal leaves behind, without breaking what tombstoning protected.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The oracle is a tree that never had the removed elements in it.</b> Compaction is
///         supposed to leave a store indistinguishable from one built without them, so the test
///         builds that store and compares every observable — tags, ids, classes, states, attributes,
///         parents, child order and <c>IndexInParent</c>. Asserting the arrays directly would assert
///         the implementation; asserting against an equivalent tree asserts the claim.
///     </para>
///     <para>
///         What tombstoning protected is one unwritten invariant — <i>a parent's index is lower than
///         its children's</i> — and it has its own test, because it is the thing a free list would
///         have broken and therefore the thing compaction exists to preserve.
///     </para>
/// </remarks>
public class CompactionTests {
    [Fact]
    public void Compacting_an_untouched_tree_changes_nothing_and_still_fills_the_mapping() {
        var fixture = new StyleFixture();
        var root = fixture.Tree.CreateElement("root");
        var first = fixture.Tree.CreateElement("div", root, classNames: "a");
        var second = fixture.Tree.CreateElement("div", root, classNames: "b");

        var remap = new int[fixture.Tree.Count];
        Assert.Equal(3, fixture.Tree.Compact(remap));

        // ⚠ The mapping is filled in even when nothing moved. A caller that has to remap only
        // sometimes is a caller that will one day not.
        Assert.Equal([0, 1, 2], remap);
        Assert.Equal(0, root.Index);
        Assert.Equal(1, first.Index);
        Assert.Equal(2, second.Index);
    }

    [Fact]
    public void A_compacted_tree_answers_like_one_that_never_held_the_removed_elements() {
        var (compacted, survivors) = Removed();
        var equivalent = Equivalent();

        Assert.Equal(equivalent.Tree.Count, compacted.Tree.Count);
        Assert.Equal(0, compacted.Tree.DeadCount);
        Assert.Equal(equivalent.Tree.LiveCount, compacted.Tree.LiveCount);

        for (var i = 0; i < equivalent.Tree.Count; i++) {
            var here = new StyleNodeId(i);
            AssertSame(equivalent, here, compacted, here);
        }

        // ...and the ids the caller was handed back point at those same elements, closed up: the two
        // slots the removed span and its child occupied are gone, so everything after them moved down.
        Assert.Equal([0, 1, 2, 3], survivors.Select(survivor => survivor.Index));
        Assert.Equal(["root", "div", "div", "b"], survivors.Select(compacted.Tree.GetTagName));
        Assert.Equal(["top", "keep-1", "keep-2", "grandchild"], survivors.Select(compacted.Tree.GetId));

        // Both sides of the hole: the classes of something ahead of it and of something behind it.
        Assert.Equal(["alpha", "beta"], compacted.Tree.GetClassNames(survivors[1]).Order());
        Assert.Equal(["eta", "theta"], compacted.Tree.GetClassNames(survivors[3]).Order());
    }

    /// <summary>
    ///     ⚠ The invariant tombstoning was protecting, and the one a free list would have broken: a
    ///     parent's slot is below every one of its children's. Three separate passes rest on it —
    ///     the cascade's ascending walk, the incremental pass's priority, and the bloom sweep's
    ///     early exit.
    /// </summary>
    [Fact]
    public void Compaction_keeps_every_parent_below_its_children() {
        var (fixture, _) = Removed();

        for (var i = 0; i < fixture.Tree.Count; i++) {
            var element = new StyleNodeId(i);

            for (var c = 0; c < fixture.Tree.GetChildCount(element); c++) {
                Assert.True(
                    fixture.Tree.GetChild(element, c).Index > i,
                    $"child {fixture.Tree.GetChild(element, c).Index} of {i} sorts before its parent"
                );
            }
        }
    }

    [Fact]
    public void Compaction_reclaims_the_class_and_child_arenas() {
        var fixture = new StyleFixture();
        var root = fixture.Tree.CreateElement("root");

        for (var i = 0; i < 40; i++) {
            fixture.Tree.CreateElement("div", root, classNames: ["one", "two", "three"]);
        }

        for (var i = 39; i >= 1; i--) {
            fixture.Tree.Remove(fixture.Tree.GetChild(root, i));
        }

        Assert.Equal(39, fixture.Tree.DeadCount);
        fixture.Tree.Compact(new int[fixture.Tree.Count]);

        // The surviving child still has its three classes, and nothing else does.
        var kept = fixture.Tree.GetChild(new StyleNodeId(0), 0);
        Assert.Equal(["one", "two", "three"], fixture.Tree.GetClassNames(kept));
        Assert.Equal(2, fixture.Tree.Count);
    }

    /// <summary>
    ///     ⚠ A style is indexed by slot, so a compaction the updater is not told about leaves every
    ///     element wearing the style of whatever used to be several slots along — an interface that
    ///     is entirely wrong and entirely plausible, because every style in it is one some element
    ///     really has.
    /// </summary>
    [Fact]
    public void The_updater_follows_the_tree() {
        var engine = new StyleEngine();
        engine.Load("#a { color: red } #b { color: green } #c { color: blue }");

        var root = engine.Tree.CreateElement("root");
        var a = engine.Tree.CreateElement("div", root, "a");
        var doomed = engine.Tree.CreateElement("div", root, "gone");
        var b = engine.Tree.CreateElement("div", root, "b");

        var updater = new StyleUpdater(engine);
        updater.ResolveAll();

        var beforeA = updater.StyleOf(a);
        var beforeB = updater.StyleOf(b);

        engine.Tree.Remove(doomed);

        var remap = new int[engine.Tree.Count];
        engine.Tree.Compact(remap);
        updater.Compact(remap);

        Assert.Same(beforeA, updater.StyleOf(new StyleNodeId(remap[a.Index])));
        Assert.Same(beforeB, updater.StyleOf(new StyleNodeId(remap[b.Index])));

        // The tail is cleared, so a slot the tree has not handed out has no style waiting for it.
        Assert.Same(ComputedStyle.Empty, updater.StyleOf(new StyleNodeId(engine.Tree.Count)));
    }

    /// <summary>
    ///     ⚠ Remapped rather than cleared. Clearing would restart every fade on the frame a document
    ///     happened to compact, so deleting one row would visibly jolt the ones transitioning around
    ///     it — a worse bug than the leak, and a rarer one, which is the combination nobody finds.
    /// </summary>
    [Fact]
    public void A_running_transition_survives_a_compaction_where_it_is() {
        var fixture = new CascadeFixture();
        fixture.Load("div { transition: opacity 1s linear; opacity: 0 } .on { opacity: 1 }");

        var root = fixture.Tree.CreateElement("root");
        var doomed = fixture.Tree.CreateElement("div", root);
        var moving = fixture.Tree.CreateElement("div", root);

        var animator = new Animator(
            fixture.Engine.Properties,
            fixture.Engine.Values,
            fixture.Engine.Names,
            fixture.Engine.Keyframes
        );

        var opacity = fixture.Engine.Properties.Lookup("opacity");
        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, moving);
        fixture.Tree.AddClass(moving, "on");

        animator.Observe(moving, before, fixture.Engine.Resolver.Resolve(fixture.Tree, moving), 0f);
        Assert.Equal(1, animator.RunningCount);

        fixture.Tree.Remove(doomed);

        var remap = new int[fixture.Tree.Count];
        fixture.Tree.Compact(remap);
        animator.Compact(remap);

        Assert.Equal(1, animator.RunningCount);

        // Halfway through, and read from the slot the element moved to rather than the one it left.
        Assert.True(
            animator.TryGetCurrent(new StyleNodeId(remap[moving.Index]), opacity, 0.5f, out var value),
            "the transition did not follow the element"
        );

        Assert.Equal(0.5f, value.Number, 1e-3f);

        // And it is not still sitting where the element used to be.
        Assert.False(animator.TryGetCurrent(moving, opacity, 0.5f, out _));
    }

    [Fact]
    public void What_was_running_for_a_removed_element_is_dropped() {
        var fixture = new CascadeFixture();
        fixture.Load("div { transition: opacity 1s linear; opacity: 0 } .on { opacity: 1 }");

        var root = fixture.Tree.CreateElement("root");
        var doomed = fixture.Tree.CreateElement("div", root);

        var animator = new Animator(
            fixture.Engine.Properties,
            fixture.Engine.Values,
            fixture.Engine.Names,
            fixture.Engine.Keyframes
        );

        var before = fixture.Engine.Resolver.Resolve(fixture.Tree, doomed);
        fixture.Tree.AddClass(doomed, "on");
        animator.Observe(doomed, before, fixture.Engine.Resolver.Resolve(fixture.Tree, doomed), 0f);

        Assert.Equal(1, animator.RunningCount);

        fixture.Tree.Remove(doomed);

        var remap = new int[fixture.Tree.Count];
        fixture.Tree.Compact(remap);
        animator.Compact(remap);

        Assert.Equal(0, animator.RunningCount);
    }

    [Fact]
    public void A_remap_too_short_for_the_store_is_refused() {
        var fixture = new StyleFixture();
        fixture.Tree.CreateElement("root");
        fixture.Tree.CreateElement("div");

        Assert.Throws<ArgumentException>(() => fixture.Tree.Compact(new int[1]));
    }

    // --------------------------------------------------------------- Helpers

    /// <summary>A tree with a subtree removed from the middle of it, then compacted.</summary>
    /// <remarks>
    ///     ⚠ <b>Elements with classes and attributes on <i>both sides</i> of the removed subtree</b>,
    ///     and the first version of this had them only before it. The class and attribute arenas are
    ///     rebuilt by compaction, so a range that was not re-pointed still lands on the right run for
    ///     anything ahead of the hole and on the wrong one for everything behind it — which meant two
    ///     sabotages against exactly that broke nothing at all.
    /// </remarks>
    static (StyleFixture Fixture, StyleNodeId[] Survivors) Removed() {
        var fixture = new StyleFixture();

        var root = fixture.Tree.CreateElement("root", id: "top");
        var first = fixture.Tree.CreateElement("div", root, "keep-1", "alpha", "beta");
        var doomed = fixture.Tree.CreateElement("span", root, "gone", "gamma", "delta");
        fixture.Tree.CreateElement("em", doomed, "gone-child", "epsilon");
        var second = fixture.Tree.CreateElement("div", root, "keep-2", "zeta");
        var grandchild = fixture.Tree.CreateElement("b", second, "grandchild", "eta", "theta");

        fixture.Tree.SetAttribute(first, "data-role", "row");
        fixture.Tree.SetAttribute(doomed, "data-role", "doomed");
        fixture.Tree.SetAttribute(grandchild, "data-role", "leaf");
        fixture.Tree.SetState(second, ElementState.Hover);
        fixture.Tree.Remove(doomed);

        var remap = new int[fixture.Tree.Count];
        fixture.Tree.Compact(remap);

        return (
            fixture,
            [
                new StyleNodeId(remap[root.Index]),
                new StyleNodeId(remap[first.Index]),
                new StyleNodeId(remap[second.Index]),
                new StyleNodeId(remap[grandchild.Index])
            ]
        );
    }

    /// <summary>The same tree, built without the elements that were removed from the other one.</summary>
    static StyleFixture Equivalent() {
        var fixture = new StyleFixture();

        var root = fixture.Tree.CreateElement("root", id: "top");
        var first = fixture.Tree.CreateElement("div", root, "keep-1", "alpha", "beta");
        var second = fixture.Tree.CreateElement("div", root, "keep-2", "zeta");
        var grandchild = fixture.Tree.CreateElement("b", second, "grandchild", "eta", "theta");

        fixture.Tree.SetAttribute(first, "data-role", "row");
        fixture.Tree.SetAttribute(grandchild, "data-role", "leaf");
        fixture.Tree.SetState(second, ElementState.Hover);

        return fixture;
    }

    static void AssertSame(StyleFixture expected, StyleNodeId there, StyleFixture actual, StyleNodeId here) {
        Assert.Equal(expected.Tree.GetTagName(there), actual.Tree.GetTagName(here));
        Assert.Equal(expected.Tree.GetId(there), actual.Tree.GetId(here));
        Assert.Equal(expected.Tree.GetClassNames(there), actual.Tree.GetClassNames(here));
        Assert.Equal(expected.Tree.GetState(there), actual.Tree.GetState(here));
        Assert.Equal(expected.Tree.GetParent(there), actual.Tree.GetParent(here));
        Assert.Equal(expected.Tree.GetChildCount(there), actual.Tree.GetChildCount(here));

        // The attribute arena is rebuilt too, so the range has to end up pointing at the same pair.
        //
        // ⚠ Looked up in each tree's own name table. The two fixtures interned their names in
        // different orders, so an id taken from one and used against the other names something else —
        // which is how this assertion first "failed": not because compaction had moved an attribute,
        // but because the test was asking the wrong question of the wrong table.
        Assert.Equal(Attribute(expected, there), Attribute(actual, here));

        for (var i = 0; i < expected.Tree.GetChildCount(there); i++) {
            Assert.Equal(expected.Tree.GetChild(there, i), actual.Tree.GetChild(here, i));
        }
    }

    /// <summary>An element's <c>data-role</c>, as text, through the tree's own name table.</summary>
    static string? Attribute(StyleFixture fixture, StyleNodeId element) {
        var name = fixture.Names.Lookup("data-role");

        return name != NameTable.None && fixture.Tree.TryGetAttribute(element.Index, name, out var value)
            ? fixture.Names.NameOf(value)
            : null;
    }
}
