// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Core;
using Xunit;

namespace Vixen.Ai.Tests;

/// <summary>An action built from a layout, which remembers the key it resolved.</summary>
/// <remarks>
///     The point of the fixture: an action whose construction needs a <see cref="BlackboardKey" /> is
///     the ordinary case — <c>MoveToTask</c>, every sensor-driven task in the village sample — and it
///     is exactly the case a parameterless factory cannot express.
/// </remarks>
sealed class KeyedAction(BlackboardKey key) : IAgentAction {
    public BlackboardKey Key => key;

    public void Start(in AgentContext context, Span<byte> state) { }

    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) => ActionStatus.Succeeded;

    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>
///     <see cref="AgentDeclarations" /> — the route by which a host that never ran a project's boot
///     path can still assemble that project's registry.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The store is process-wide, so every test here names its keys and actions with a prefix
///         of its own and evicts in a <c>finally</c>.</b> Reading a count off
///         <see cref="AgentDeclarations.Actions" /> would be reading whatever else the assembly had in
///         flight; every assertion below is about named entries.
///     </para>
///     <para>
///         The origin is a real, unrelated assembly per test rather than this one, so that an evict at
///         the end of one test cannot take another's declarations with it. Any <see cref="Assembly" />
///         works — the registry only ever compares them.
///     </para>
/// </remarks>
public class AgentDeclarationsTests {
    [Fact]
    public void AHostBuildsTheLayoutAndTheRegistryWithoutRunningTheProject() {
        var origin = typeof(AgentDeclarationsTests).Assembly;

        try {
            // What a project's [ModuleInitializer] does. Nothing here is a host and no Game has run.
            Assert.True(AgentDeclarations.DeclareKey("build.target", BlackboardValueType.Entity, origin));
            Assert.True(AgentDeclarations.DeclareKey("build.refuge", BlackboardValueType.Vector3, origin));

            Assert.True(AgentDeclarations.DeclareAction(
                "build.chase",
                layout => new KeyedAction(layout.Key("build.target")),
                stateSize: 8,
                origin));

            // What the host does, in the order it has to: every key first, then the actions that
            // resolve them.
            var layout = AgentDeclarations.BuildLayout();
            var registry = AgentDeclarations.BuildRegistry(layout);

            Assert.True(layout.TryGetKey(Symbol.Intern("build.target"), out var target));
            Assert.True(layout.TryGetKey(Symbol.Intern("build.refuge"), out _));

            Assert.True(registry.TryGetIndex(Symbol.Intern("build.chase"), out var chase));
            Assert.Equal(8, registry.StateSize(chase));

            // ⚠ The assertion that matters: the action was constructed against the host's layout, not
            // against one the project built for itself. A factory that ignored its argument would
            // still produce an action and still register — and would resolve to the wrong slot.
            Assert.Equal(target, Assert.IsType<KeyedAction>(registry[chase]).Key);
        } finally {
            AgentDeclarations.Evict(origin);
        }
    }

    [Fact]
    public void TheFirstDeclarationOfANameWins() {
        var origin = typeof(BlackboardLayout).Assembly;

        try {
            Assert.True(AgentDeclarations.DeclareKey("first.k", BlackboardValueType.Float, origin));
            Assert.False(AgentDeclarations.DeclareKey("first.k", BlackboardValueType.Vector3, origin));

            Assert.True(AgentDeclarations.DeclareAction("first.a", _ => new KeyedAction(default), 4, origin));
            Assert.False(AgentDeclarations.DeclareAction("first.a", _ => new KeyedAction(default), 64, origin));

            var key = Assert.Single(AgentDeclarations.Keys, entry => entry.Name == "first.k");
            var action = Assert.Single(AgentDeclarations.Actions, entry => entry.Name == "first.a");

            // The second declaration lost, and lost completely: neither its type nor its state size
            // survives. An assembly loaded twice into two contexts is the case this exists for.
            Assert.Equal(BlackboardValueType.Float, key.Type);
            Assert.Equal(4, action.StateSize);
        } finally {
            AgentDeclarations.Evict(origin);
        }
    }

    /// <summary>
    ///     ⚠ Name order, never declaration order: which assembly's module initialiser runs first is a
    ///     property of the run.
    /// </summary>
    [Fact]
    public void DeclarationsAreOrderedByNameAndNotByArrival() {
        var origin = typeof(Symbol).Assembly;

        try {
            AgentDeclarations.DeclareAction("order.z", _ => new KeyedAction(default), 0, origin);
            AgentDeclarations.DeclareAction("order.a", _ => new KeyedAction(default), 0, origin);
            AgentDeclarations.DeclareAction("order.m", _ => new KeyedAction(default), 0, origin);

            var names = AgentDeclarations.Actions
                .Where(entry => entry.Name.StartsWith("order.", StringComparison.Ordinal))
                .Select(entry => entry.Name)
                .ToArray();

            // The count is part of the assertion: a filter that matched nothing would leave the
            // sequence equality below true and say nothing at all.
            Assert.Equal(3, names.Length);
            Assert.Equal(["order.a", "order.m", "order.z"], names);
        } finally {
            AgentDeclarations.Evict(origin);
        }
    }

    /// <summary>
    ///     An unloaded project's declarations go, and its neighbour's stay — the half that would
    ///     otherwise pin a collectible context through the factory delegate.
    /// </summary>
    [Fact]
    public void EvictingAnAssemblyForgetsItsDeclarationsAndOnlyItsOwn() {
        var leaving = typeof(AgentActionRegistry).Assembly;
        var staying = typeof(FactAttribute).Assembly;

        try {
            AgentDeclarations.DeclareKey("evict.gone", BlackboardValueType.Float, leaving);
            AgentDeclarations.DeclareAction("evict.gone", _ => new KeyedAction(default), 0, leaving);
            AgentDeclarations.DeclareKey("evict.kept", BlackboardValueType.Float, staying);

            Assert.Equal(2, AgentDeclarations.Evict(leaving));

            Assert.DoesNotContain(AgentDeclarations.Keys, entry => entry.Name == "evict.gone");
            Assert.DoesNotContain(AgentDeclarations.Actions, entry => entry.Name == "evict.gone");
            Assert.Contains(AgentDeclarations.Keys, entry => entry.Name == "evict.kept");

            // Nothing left to forget, so a second evict is a no-op rather than a throw — Unload can
            // be reached twice.
            Assert.Equal(0, AgentDeclarations.Evict(leaving));
        } finally {
            AgentDeclarations.Evict(leaving);
            AgentDeclarations.Evict(staying);
        }
    }

    [Fact]
    public void ADeclarationIsCheckedWhenItIsMadeRatherThanWhenItIsBuilt() {
        var origin = typeof(AgentDeclarationsTests).Assembly;

        Assert.Throws<ArgumentException>(() => AgentDeclarations.DeclareKey("", BlackboardValueType.Float, origin));
        Assert.Throws<ArgumentNullException>(() => AgentDeclarations.DeclareAction("null.factory", null!, 0, origin));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            AgentDeclarations.DeclareAction("negative", _ => new KeyedAction(default), -1, origin));
        Assert.Throws<ArgumentNullException>(() => AgentDeclarations.BuildRegistry(null!));
    }

    /// <summary>A factory that hands back nothing is named, rather than becoming a null in the table.</summary>
    [Fact]
    public void AFactoryThatReturnsNullIsRefusedByName() {
        var origin = typeof(Assembly).Assembly;

        try {
            AgentDeclarations.DeclareAction("null.action", _ => null!, 0, origin);

            var failure = Assert.Throws<InvalidOperationException>(() =>
                AgentDeclarations.BuildRegistry(BlackboardLayout.Empty));

            Assert.Contains("null.action", failure.Message, StringComparison.Ordinal);
        } finally {
            AgentDeclarations.Evict(origin);
        }
    }
}
