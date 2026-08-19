// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Gameplay.Items;
using Xunit;

namespace Vixen.Gameplay.Content.Tests;

/// <summary>A module whose code names a tag no file will ever mention.</summary>
/// <remarks>
///     ⚠ <b>The shape of every real one.</b> <c>QuestModule</c> declares <c>Event.Kill</c> because it
///     is the verb a Kill objective counts, and no quest file mentions it anywhere — so the only way
///     it can reach the tag table is the composition.
/// </remarks>
sealed class CodeOnlyTagModule : IGameplayModule {
    /// <summary>The tag nothing authors.</summary>
    public const string CodeOnly = "Event.Kill";

    /// <inheritdoc />
    public string Name => "CodeOnlyTag";

    /// <inheritdoc />
    public void Configure(GameplayModuleBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Tag(CodeOnly);
    }
}

/// <summary>The tags a game's code declares, and the load path that has to bake them.</summary>
/// <remarks>
///     ⚠ <b>Every assertion here is about silence.</b> A tag missing from the table does not throw and
///     is not a problem in <see cref="DefinitionLoad.Problems" /> — it resolves to
///     <c>GameplayTag.None</c>, and every rule naming it matches nothing for ever. So the test for the
///     fix is the test for the bug: the same content, loaded both ways.
/// </remarks>
public class DefinitionContentCompositionTests {
    static GameplayComposition Composed() =>
        new GameplayConfig().Use<GameplayKernelModule>().Use<CodeOnlyTagModule>().Build();

    static ItemDefinition Item(string name) => new() { DisplayName = name, Slot = "Slot.Weapon" };

    [Fact]
    public async Task AComposedLoadBakesATagNoDefinitionMentions() {
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Build();

        var load = await DefinitionContent.LoadAsync(
            shipped.Assets,
            Composed(),
            TestContext.Current.CancellationToken
        );

        Assert.Empty(load.Problems);
        Assert.True(load.Catalog.Tags.TryResolve(CodeOnlyTagModule.CodeOnly, out var tag));
        Assert.True(tag.IsSome);
    }

    [Fact]
    public async Task TheOverloadWithoutACompositionLosesItSilently() {
        // ⚠ The bug, pinned. Nothing throws, nothing is reported, and the catalog is otherwise
        // identical — which is exactly why this was worth an overload rather than a note in a README.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Build();

        var load = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.Empty(load.Problems);
        Assert.Equal(1, load.Catalog.Count);
        Assert.False(load.Catalog.Tags.TryResolve(CodeOnlyTagModule.CodeOnly, out _));
    }

    [Fact]
    public async Task ACodeOnlyTagIsAPrefixMatchAndNotJustAName() {
        // The asymmetry that makes seeding matter: a tag id is a bake and a prefix test is two
        // integer comparisons, so the parent has to be in the same table for the child to match it.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Build();

        var load = await DefinitionContent.LoadAsync(
            shipped.Assets,
            Composed(),
            TestContext.Current.CancellationToken
        );

        var kill = load.Catalog.Tags.Resolve(CodeOnlyTagModule.CodeOnly);

        Assert.True(load.Catalog.Tags.Matches(kill, load.Catalog.Tags.Resolve("Event")));
    }

    [Fact]
    public async Task SeedingChangesTheBuildHashBecauseBothEndsMustAgreeAboutTheTable() {
        // ⚠ Two peers that disagree about the tag table cannot exchange a tag index, so a realm that
        // seeded and a client that did not must not compare equal. That is what makes the difference
        // a refused connection rather than a silent desync.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"), DefinitionContent.Label)
            .Build();

        var seeded = await DefinitionContent.LoadAsync(
            shipped.Assets,
            Composed(),
            TestContext.Current.CancellationToken
        );

        var bare = await DefinitionContent.LoadAsync(shipped.Assets, TestContext.Current.CancellationToken);

        Assert.NotEqual(seeded.Catalog.BuildHash, bare.Catalog.BuildHash);
    }

    [Fact]
    public async Task LoadFromAsyncTakesACompositionToo() {
        // The by-address overload is what a game with a hand-written list uses, and it needed the same
        // seam: the trap is a property of the catalog, not of how the addresses were found.
        var shipped = new Shipped()
            .Definition("items/sword", Item("A sword"))
            .Build();

        var load = await DefinitionContent.LoadFromAsync(
            shipped.Assets,
            Composed(),
            ["items/sword"],
            TestContext.Current.CancellationToken
        );

        Assert.Equal(1, load.Catalog.Count);
        Assert.True(load.Catalog.Tags.TryResolve(CodeOnlyTagModule.CodeOnly, out _));
    }

    [Fact]
    public async Task ANullCompositionIsRefusedRatherThanTreatedAsNoTags() {
        var shipped = new Shipped().Build();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await DefinitionContent.LoadAsync(
                shipped.Assets,
                composition: null!,
                TestContext.Current.CancellationToken
            )
        );
    }
}
