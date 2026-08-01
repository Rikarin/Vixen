// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Ecs;
using Vixen.Engine.Behaviors;
using Xunit;

namespace Vixen.Engine.Tests;

/// <summary>That a described behaviour declares itself, and that only a described one does.</summary>
/// <remarks>
///     ⚠ <b>What this makes possible is a behaviour somebody can <i>author</i>.</b> Until it existed
///     a <see cref="Behavior" /> could only be attached in code: the editor's Add Component menu is
///     built from a registry, a scene entry is an alias resolved against one, and behaviours were in
///     neither. The behaviours themselves are in <c>RegistrationBehaviors.cs</c>.
/// </remarks>
public sealed class BehaviorRegistrationTests {
    [Fact]
    public void ADescribedBehaviourIsRegisteredWithoutBeingAskedFor() {
        Assert.True(SceneBehaviorRegistry.TryGet(typeof(Patrol), out var binder));

        Assert.Equal("RegistrationTestPatrol", binder.Name);
        Assert.Equal(typeof(Patrol), binder.BehaviorType);
    }

    /// <remarks>
    ///     The exclusion that keeps the menu honest. Most behaviours are code attached in code, and a
    ///     project whose every helper class appeared in Add Component would be one nobody could find
    ///     anything in.
    /// </remarks>
    [Fact]
    public void ABehaviourWithNoContractIsNotRegistered() {
        Assert.False(SceneBehaviorRegistry.TryGet(typeof(RegistrationTestCodeOnly), out _));
    }

    /// <remarks>A described base is how members reach its subclasses; a scene names the concrete one.</remarks>
    [Fact]
    public void AnAbstractBehaviourIsNotRegisteredAndItsSubclassIs() {
        Assert.False(SceneBehaviorRegistry.TryGet(typeof(RegistrationTestWeapon), out _));
        Assert.True(SceneBehaviorRegistry.TryGet(typeof(RegistrationTestSword), out _));
    }

    /// <remarks>
    ///     ⚠ Described data that is not a behaviour goes to the component registry or nowhere. The two
    ///     registries answer the same question about two different kinds of thing, and a type in both
    ///     would be one a scene entry could mean either of.
    /// </remarks>
    [Fact]
    public void DescribedDataThatIsNotABehaviourIsNotRegistered() {
        Assert.False(SceneBehaviorRegistry.TryGet(typeof(RegistrationTestSettings), out _));
        Assert.False(SceneBehaviorRegistry.TryGet(typeof(Shield), out _));
    }

    /// <summary>
    ///     ⚠ <b>The binder closes the generic once, and that is what keeps the store's buckets
    ///     monomorphic.</b> A type-erased <c>Add&lt;Behavior&gt;</c> would put every behaviour in the
    ///     project into one bucket and undo the arrangement <see cref="BehaviorStore" /> exists for.
    /// </summary>
    [Fact]
    public void TheBinderAttachesReadsAndRemovesWithoutNamingTheType() {
        using var world = new World("Behaviours");

        var store = new BehaviorStore(world);
        var entity = world.Create();

        Assert.True(SceneBehaviorRegistry.TryGet(typeof(Patrol), out var binder));
        Assert.Null(binder.Attached(store, entity));

        var made = binder.Create();

        Assert.IsType<Patrol>(made);

        binder.AttachTo(store, entity, made);

        Assert.Same(made, binder.Attached(store, entity));

        // Through the store's own typed lookup as well, which is what the update loop walks — an
        // attach that only the binder could find would be one no frame ever runs.
        Assert.Same(made, store.Get<Patrol>(entity));

        Assert.True(binder.RemoveFrom(store, entity));
        Assert.Null(binder.Attached(store, entity));
        Assert.False(binder.RemoveFrom(store, entity));
    }

    /// <summary>
    ///     ⚠ <b>Attaching replaces rather than adding a second.</b> The store is happy to hold two of
    ///     one type on one entity; everything above treats a behaviour the way it treats a component,
    ///     where an entity has one or none.
    /// </summary>
    [Fact]
    public void AttachingTwiceLeavesOneBehaviour() {
        using var world = new World("Behaviours");

        var store = new BehaviorStore(world);
        var entity = world.Create();

        SceneBehaviorRegistry.TryGet(typeof(Patrol), out var binder);

        binder!.AttachTo(store, entity, binder.Create());

        var second = binder.Create();

        binder.AttachTo(store, entity, second);

        Assert.Same(second, binder.Attached(store, entity));
        Assert.Single(store.AllOn(entity).ToArray());
    }
}
