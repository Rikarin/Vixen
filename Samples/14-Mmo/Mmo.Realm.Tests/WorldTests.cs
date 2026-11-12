// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Gameplay;
using Vixen.Gameplay.Ai;
using Vixen.Samples.Mmo.Rules;
using Xunit;

namespace Vixen.Samples.Mmo.Realms.Tests;

/// <summary>The world a shard keeps turning when nobody is on it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These are about a process, not about <see cref="Spawner" />.</b> The AI library's own
///         tests already cover a camp's arithmetic; what was never tested is that a
///         <c>MmoRealm</c> holds one, ticks it and reports what it did — because until now nothing
///         outside a test called <c>Compose</c> at all, so a running shard's
///         <c>OnRealmUpdate</c> touched no gameplay library.
///     </para>
///     <para>
///         The whole build's worth of camps is driven, because the sample's content does not say
///         which map a camp is on. That is asserted here rather than left implicit: if somebody adds
///         the join, this test is the one that should be changed on purpose.
///     </para>
/// </remarks>
public sealed class WorldTests {
    static SpawnLibrary Camps(int tables) {
        var builder = new DefinitionCatalogBuilder();

        for (var index = 0; index < tables; index++) {
            builder.Add(
                $"spawns/camp-{index}",
                new SpawnTableDefinition {
                    DisplayName = $"Camp {index}",
                    Cap = 3,
                    RespawnSeconds = 10f,
                    RespawnJitter = 0f,
                    Entries = [new() { Creature = "creatures/wolf", Weight = 1f, Minimum = 1, Maximum = 1 }]
                }
            );
        }

        return SpawnLibrary.Compile(builder.Build());
    }

    [Fact]
    public void EveryCampFillsOnTheFirstTickAndTheCounterSaysSo() {
        var world = new WorldSpawns(Camps(4), 7);

        Assert.Equal(4, world.Camps);
        Assert.Equal(0, world.Alive);
        Assert.Equal(0, world.Issued);

        // Four camps of three, all due immediately, is twelve orders on the tick that notices.
        Assert.Equal(12, world.Tick(0f));
        Assert.Equal(12, world.Alive);
        Assert.Equal(12, world.Issued);
    }

    [Fact]
    public void AFullWorldIssuesNothingAndThatIsTheCheapPath() {
        // ⚠ The property that makes ticking every camp every frame affordable at all: a camp nothing
        // has killed does no work. A shard with no players costs nothing here.
        var world = new WorldSpawns(Camps(4), 7);

        world.Tick(0f);

        Assert.Equal(0, world.Tick(1f));
        Assert.Equal(0, world.Tick(600f));
        Assert.Equal(12, world.Issued);
    }

    [Fact]
    public void SomethingThatDiedComesBackOnItsTableClockAndNotBefore() {
        var world = new WorldSpawns(Camps(1), 7);

        world.Tick(0f);

        Assert.True(world.Died(0, 1, 5f));
        Assert.Equal(2, world.Alive);

        // Ten seconds after the death, not after the tick that noticed it — so a server that fell
        // behind does not repopulate faster than one that did not.
        Assert.Equal(0, world.Tick(14f));
        Assert.Equal(1, world.Tick(15f));
        Assert.Equal(3, world.Alive);
    }

    [Fact]
    public void ADeathInACampThatIsNotThereIsRefusedRatherThanIgnored() {
        var world = new WorldSpawns(Camps(1), 7);

        world.Tick(0f);

        Assert.False(world.Died(9, 0, 1f));
        Assert.False(world.Died(0, 9, 1f));
    }

    [Fact]
    public void TwoWorldsOnOneSeedIssueTheSameOrdersInTheSameSlots() {
        // ⚠ What makes a spawn bug reportable rather than an anecdote, and what a shard restarted on
        // the same spec relies on. The orders are compared, not just the count: a different creature
        // in the same slot is the interesting failure.
        var one = new WorldSpawns(Camps(3), 0x50AC);
        var two = new WorldSpawns(Camps(3), 0x50AC);

        one.Tick(0f);
        two.Tick(0f);

        Assert.Equal<IEnumerable<SpawnOrder>>(one.Last, two.Last);
    }

    [Fact]
    public void ARealmComposesAWorldAndTicksItFromOnRealmUpdate() {
        // The point of the whole exercise: Libraries non-null and a library doing work, driven
        // through the realm rather than by the test poking WorldSpawns directly.
        var realm = new MmoRealm();
        var libraries = MmoLibraries.Load(Definitions());

        Assert.Empty(libraries.Problems);

        realm.Compose(libraries, ImmutableArray<string>.Empty, 0x50AC);

        Assert.NotNull(realm.Libraries);
        Assert.NotNull(realm.Spawns);
        Assert.Equal(1, realm.Spawns.Camps);
        Assert.Equal(0, realm.Spawns.Issued);

        // OnRealmUpdate is protected; Update is what the host calls, and driving it is what makes
        // this a test of the realm rather than of the field it holds.
        realm.Tick(0);

        Assert.Equal(3, realm.Spawns.Issued);
    }

    [Fact]
    public void AContentProblemIsRefusedOnTheSameTermsAsALibrarysOwn() {
        // ⚠ A .vxgroup broad enough to sweep a scene in with the definitions arrives as a load
        // problem rather than as a library problem, and a shard must not start on either. Running the
        // real sample against a real content build is what produced this case: thirteen .vxscene
        // files, labelled `definitions`, none of which read as one.
        var realm = new MmoRealm();

        var refusal = Assert.Throws<InvalidOperationException>(() => realm.Compose(
                MmoLibraries.Load([]),
                ["'maps/greenmarch' is labelled a definition and did not read as one."],
                0
            )
        );

        Assert.Contains("Content:", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("1 problems", refusal.Message, StringComparison.Ordinal);
    }

    static IEnumerable<(string Address, ReadOnlyMemory<byte> Bytes)> Definitions() => [
        ("spawns/camp-0", DefinitionSerialization.ToBytes(
            new SpawnTableDefinition {
                DisplayName = "Camp",
                Cap = 3,
                RespawnSeconds = 10f,
                RespawnJitter = 0f,
                Entries = [new() { Creature = "creatures/wolf", Weight = 1f, Minimum = 1, Maximum = 1 }]
            }
        ))
    ];
}
