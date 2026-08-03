// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Editor.Core.Tests;

/// <summary>Doc 36 § D2: one registry, three producers, and a consumer that cannot tell them apart.</summary>
public class EditorRegistryTests {
    sealed record Tool(string Id);

    sealed record Panel(string Id);

    [Fact]
    public void Contributions_come_back_by_kind_in_the_order_they_arrived() {
        var registry = new EditorRegistry();

        registry.Add(new Tool("first"));
        registry.Add(new Panel("elsewhere"));
        registry.Add(new Tool("second"));

        Assert.Equal(["first", "second"], registry.All<Tool>().Select(static tool => tool.Id));
        Assert.Equal(["elsewhere"], registry.All<Panel>().Select(static panel => panel.Id));

        // A kind nobody has contributed is empty rather than absent, so a consumer written before its
        // producer exists reads as "nothing yet" instead of throwing.
        Assert.Empty(registry.All<EditorRegistryTests>());
    }

    [Fact]
    public void Adding_hands_back_the_removal() {
        var registry = new EditorRegistry();
        var scope = registry.Add(new Tool("sculpt"));

        Assert.Single(registry.All<Tool>());

        scope.Dispose();
        Assert.Empty(registry.All<Tool>());

        // ⚠ Disposing twice is what a plugin unloaded after a panel already withdrew its tool does.
        scope.Dispose();
        Assert.Empty(registry.All<Tool>());
    }

    [Fact]
    public void The_same_contribution_twice_is_two_contributions() {
        var registry = new EditorRegistry();
        var tool = new Tool("sculpt");

        var first = registry.Add(tool);

        registry.Add(tool);

        // Not deduplicated: two plugins contributing equal records are two contributions, and one
        // withdrawing must not take the other's with it. Records compare by value, which is exactly
        // why removal is by scope rather than by equality.
        Assert.Equal(2, registry.All<Tool>().Count);

        first.Dispose();
        Assert.Single(registry.All<Tool>());
    }

    [Fact]
    public void A_consumer_hears_which_kind_changed_and_not_that_something_did() {
        var registry = new EditorRegistry();
        List<Type> heard = [];

        registry.Changed += heard.Add;

        var scope = registry.Add(new Tool("sculpt"));

        registry.Add(new Panel("elsewhere"));
        scope.Dispose();

        // A menu bar rebuilding because a settings page arrived is a menu bar that rebuilds for
        // everything, which on a plugin-heavy start-up is every registration in the process.
        Assert.Equal([typeof(Tool), typeof(Panel), typeof(Tool)], heard);
    }

    [Fact]
    public void Removing_something_already_gone_says_nothing() {
        var registry = new EditorRegistry();
        var scope = registry.Add(new Tool("sculpt"));

        scope.Dispose();

        var heard = 0;

        registry.Changed += _ => heard++;
        scope.Dispose();

        Assert.Equal(0, heard);
    }

    [Fact]
    public void Enumerating_survives_a_producer_that_registers_while_it_runs() {
        var registry = new EditorRegistry();

        registry.Add(new Tool("first"));

        // A custom inspector whose construction registers a drawer is the real case. The alternative
        // is an InvalidOperationException thrown from inside somebody else's Activate.
        foreach (var tool in registry.All<Tool>()) {
            registry.Add(new Tool(tool.Id + "-derived"));
        }

        Assert.Equal(2, registry.All<Tool>().Count);
    }

    [Fact]
    public void Clearing_tells_every_consumer_that_its_kind_is_gone() {
        var registry = new EditorRegistry();

        registry.Add(new Tool("sculpt"));
        registry.Add(new Panel("elsewhere"));

        List<Type> heard = [];

        registry.Changed += heard.Add;
        registry.Clear();

        Assert.Empty(registry.All<Tool>());
        Assert.Empty(registry.All<Panel>());
        // Sorted, because which kind is announced first is a dictionary's business and not a claim
        // this test has any reason to make.
        Assert.Equal([typeof(Panel), typeof(Tool)], heard.Order(Comparer<Type>.Create(Named)));

        static int Named(Type left, Type right) => string.CompareOrdinal(left.Name, right.Name);
    }
}
