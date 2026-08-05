// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Xunit;

namespace Vixen.Gameplay.Tests;

public class GameplayTagTableTests {
    static readonly string[] Vocabulary = [
        "Damage.Fire.Burn",
        "Damage.Fire.Scorch",
        "Damage.Frost",
        "Damage.Physical.Slash",
        "Creature.Undead.Skeleton",
        "Creature.Undead.Zombie",
        "Creature.Beast",
        "Effect.Control.Stun",
        "Effect.Control.Root",
        "Effect.Buff",
        "State.InCombat",
        "State.Mounted"
    ];

    static GameplayTagTable Table() => new GameplayTagTableBuilder().AddRange(Vocabulary).Build();

    [Fact]
    public void EveryAncestorIsImplied() {
        var table = new GameplayTagTableBuilder().Add("Damage.Fire.Burn").Build();

        Assert.Equal(3, table.Count);
        Assert.True(table.TryResolve("Damage", out _));
        Assert.True(table.TryResolve("Damage.Fire", out _));
        Assert.True(table.TryResolve("Damage.Fire.Burn", out _));
    }

    [Fact]
    public void AParentMatchesItsChildrenAndNothingElse() {
        var table = Table();
        var burn = table.Require("Damage.Fire.Burn");

        Assert.True(table.Matches(burn, "Damage"));
        Assert.True(table.Matches(burn, "Damage.Fire"));
        Assert.True(table.Matches(burn, "Damage.Fire.Burn"));
        Assert.False(table.Matches(burn, "Damage.Frost"));
        Assert.False(table.Matches(burn, "Creature"));
    }

    [Fact]
    public void PrefixMatchingAgreesWithAStringOracle() {
        var table = Table();

        // Every tag in the table against every tag in the table, against the definition of "under" a
        // reader would write: the same string, or the same string followed by a dot. Anything the
        // range test and the oracle disagree about is a rule matching the wrong content.
        for (var subject = 1u; subject <= table.Count; subject++) {
            for (var prefix = 1u; prefix <= table.Count; prefix++) {
                var subjectName = table.NameOf(new(subject));
                var prefixName = table.NameOf(new(prefix));

                var oracle = string.Equals(subjectName, prefixName, StringComparison.Ordinal)
                    || subjectName.StartsWith(prefixName + ".", StringComparison.Ordinal);

                Assert.Equal(oracle, table.Matches(new(subject), new GameplayTag(prefix)));
            }
        }
    }

    [Fact]
    public void ASiblingWhoseNameSortsBelowADotDoesNotSplitASubtree() {
        // 'A-x' sorts before 'A.b' by a plain ordinal comparison of the whole name, so a table that
        // sorted qualified names rather than segments would number A-x inside A's subtree and make
        // A's range contain a tag that is not under A.
        var table = new GameplayTagTableBuilder()
            .Add("A.b")
            .Add("A.c")
            .Add("A-x")
            .Build();

        var range = table.RangeOf("A");

        Assert.False(range.Contains(table.Require("A-x")));
        Assert.True(range.Contains(table.Require("A.b")));
        Assert.True(range.Contains(table.Require("A.c")));
        Assert.Equal(3, range.Count);
    }

    [Fact]
    public void NumberingIsAPureFunctionOfTheSetAndNotOfTheOrder() {
        var forwards = new GameplayTagTableBuilder().AddRange(Vocabulary).Build();
        var backwards = new GameplayTagTableBuilder().AddRange(Vocabulary.Reverse()).Build();

        var shuffled = new List<string>(Vocabulary);
        var random = new GameplayRandom(20260804);

        for (var index = shuffled.Count - 1; index > 0; index--) {
            var swap = random.NextInt(index + 1);
            (shuffled[index], shuffled[swap]) = (shuffled[swap], shuffled[index]);
        }

        var mixed = new GameplayTagTableBuilder().AddRange(shuffled).Build();

        Assert.Equal(forwards.BuildHash, backwards.BuildHash);
        Assert.Equal(forwards.BuildHash, mixed.BuildHash);

        foreach (var name in Vocabulary) {
            Assert.Equal(forwards.Require(name), backwards.Require(name));
            Assert.Equal(forwards.Require(name), mixed.Require(name));
        }
    }

    [Fact]
    public void ADuplicateAdditionChangesNothing() {
        var once = new GameplayTagTableBuilder().AddRange(Vocabulary).Build();
        var twice = new GameplayTagTableBuilder().AddRange(Vocabulary).AddRange(Vocabulary).Build();

        Assert.Equal(once.Count, twice.Count);
        Assert.Equal(once.BuildHash, twice.BuildHash);
    }

    [Fact]
    public void TwoVocabulariesThatConcatenateAlikeStillDiffer() {
        var first = new GameplayTagTableBuilder().Add("AB").Add("C").Build();
        var second = new GameplayTagTableBuilder().Add("A").Add("BC").Build();

        Assert.NotEqual(first.BuildHash, second.BuildHash);
    }

    [Fact]
    public void AnUnknownPrefixMatchesNothingRatherThanEverything() {
        var table = Table();

        var range = table.RangeOf("Damage.Acid");

        Assert.False(range.IsSome);
        Assert.Equal(0, range.Count);

        for (var index = 1u; index <= table.Count; index++) {
            Assert.False(range.Contains(new(index)));
        }
    }

    [Fact]
    public void NoTagMatchesAnything() {
        var table = Table();

        Assert.False(table.RangeOf("Damage").Contains(GameplayTag.None));
        Assert.False(table.Matches(GameplayTag.None, "Damage"));
        Assert.Equal(string.Empty, table.NameOf(GameplayTag.None));
    }

    [Fact]
    public void TwoSpellingsThatDifferOnlyInCaseAreRefused() {
        var builder = new GameplayTagTableBuilder().Add("Damage.Fire").Add("Damage.fire");

        var error = Assert.Throws<InvalidOperationException>(builder.Build);

        Assert.Contains("Damage.fire", error.Message, StringComparison.Ordinal);
        Assert.Contains("Damage.Fire", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySegmentIsRefused() {
        var builder = new GameplayTagTableBuilder();

        Assert.Throws<ArgumentException>(() => builder.Add("Damage..Fire"));
        Assert.Throws<ArgumentException>(() => builder.Add(".Damage"));
        Assert.Throws<ArgumentException>(() => builder.Add("Damage."));
    }

    [Fact]
    public void ATagDeeperThanTheLimitIsRefusedRatherThanOverflowingTheWalk() {
        var builder = new GameplayTagTableBuilder();
        var deep = string.Join('.', Enumerable.Range(0, GameplayTagTable.MaximumDepth + 1).Select(index => $"s{index}"));

        Assert.Throws<ArgumentException>(() => builder.Add(deep));
    }

    [Fact]
    public void ASymbolSurvivesARenumberingAndAnIndexDoesNot() {
        var before = Table();
        var stun = before.Require("Effect.Control.Stun");
        var saved = before.SymbolOf(stun);

        // A content update that adds one tag alphabetically before it. This is what a save has to
        // survive and what a live reload may not do underneath a running session.
        var after = new GameplayTagTableBuilder().AddRange(Vocabulary).Add("Creature.Aberration").Build();

        Assert.NotEqual(stun, after.Require("Effect.Control.Stun"));
        Assert.True(after.TryResolve(saved, out var rehydrated));
        Assert.Equal(after.Require("Effect.Control.Stun"), rehydrated);
    }

    [Fact]
    public void ASymbolThatNoLongerNamesATagResolvesToNone() {
        var table = Table();

        Assert.False(table.TryResolve(Symbol.Intern("Damage.Acid"), out var tag));
        Assert.Equal(GameplayTag.None, tag);
        Assert.False(table.TryResolve(Symbol.None, out _));
    }

    [Fact]
    public void ParentsAndDepthsDescribeTheTreeTheNamesSpell() {
        var table = Table();

        var burn = table.Require("Damage.Fire.Burn");
        var fire = table.Require("Damage.Fire");
        var damage = table.Require("Damage");

        Assert.Equal(fire, table.ParentOf(burn));
        Assert.Equal(damage, table.ParentOf(fire));
        Assert.Equal(GameplayTag.None, table.ParentOf(damage));
        Assert.Equal(3, table.DepthOf(burn));
        Assert.Equal(1, table.DepthOf(damage));
    }

    [Fact]
    public void TheWholeTableIsOneRange() {
        var table = Table();

        Assert.Equal(table.Count, table.All.Count);

        for (var index = 1u; index <= table.Count; index++) {
            Assert.True(table.All.Contains(new(index)));
        }

        Assert.False(table.All.Contains(new((uint)table.Count + 1)));
    }

    [Fact]
    public void AnEmptyTableHoldsNothing() {
        Assert.Equal(0, GameplayTagTable.Empty.Count);
        Assert.False(GameplayTagTable.Empty.All.IsSome);
        Assert.Equal(GameplayTag.None, GameplayTagTable.Empty.Resolve("Damage"));
    }
}

public class GameplayTagSetTests {
    static GameplayTagTable Table() =>
        new GameplayTagTableBuilder()
            .Add("Effect.Control.Stun")
            .Add("Effect.Control.Root")
            .Add("Effect.Buff.Haste")
            .Add("State.Stunned")
            .Add("State.Mounted")
            .Build();

    [Fact]
    public void TwoGrantsNeedTwoRevokes() {
        var table = Table();
        var stunned = table.Require("State.Stunned");
        var set = new GameplayTagSet();

        Assert.True(set.Add(stunned));
        Assert.False(set.Add(stunned));
        Assert.Equal(2, set.CountOf(stunned));

        Assert.False(set.Remove(stunned));
        Assert.True(set.Contains(stunned));

        Assert.True(set.Remove(stunned));
        Assert.False(set.Contains(stunned));
    }

    [Fact]
    public void RevokingSomethingNobodyGrantedIsANoOp() {
        var set = new GameplayTagSet();

        Assert.False(set.Remove(Table().Require("State.Stunned")));
        Assert.Empty(set);
    }

    [Fact]
    public void ContainsIsExactAndContainsAnyIsHierarchical() {
        var table = Table();
        var set = new GameplayTagSet();
        set.Add(table.Require("Effect.Control.Stun"));

        Assert.True(set.Contains(table.Require("Effect.Control.Stun")));
        Assert.False(set.Contains(table.Require("Effect.Control")));
        Assert.True(set.ContainsAny(table.RangeOf("Effect.Control")));
        Assert.True(set.ContainsAny(table.RangeOf("Effect")));
        Assert.False(set.ContainsAny(table.RangeOf("Effect.Buff")));
        Assert.False(set.ContainsAny(table.RangeOf("State")));
    }

    [Fact]
    public void TagsComeBackInTheTreesOwnOrderWhateverOrderTheyWereAddedIn() {
        var table = Table();
        var names = new[] { "State.Mounted", "Effect.Buff.Haste", "State.Stunned", "Effect.Control.Root" };

        var set = new GameplayTagSet();

        foreach (var name in names.Reverse()) {
            set.Add(table.Require(name));
        }

        var walked = new List<string>();

        foreach (var tag in set) {
            walked.Add(table.NameOf(tag));
        }

        Assert.Equal(names.OrderBy(name => table.Require(name).Index).ToArray(), walked);
    }

    [Fact]
    public void NoTagIsNeverGranted() {
        var set = new GameplayTagSet();

        Assert.False(set.Add(GameplayTag.None));
        Assert.Empty(set);
        Assert.False(set.Contains(GameplayTag.None));
    }

    [Fact]
    public void ContainsAnyFindsATagAtEveryPositionInTheRange() {
        var table = new GameplayTagTableBuilder()
            .Add("A.a")
            .Add("A.b")
            .Add("A.c")
            .Add("B")
            .Build();

        foreach (var name in new[] { "A", "A.a", "A.b", "A.c" }) {
            var set = new GameplayTagSet();
            set.Add(table.Require(name));

            Assert.True(set.ContainsAny(table.RangeOf("A")));
            Assert.False(set.ContainsAny(table.RangeOf("B")));
        }
    }
}

public class GameplayTagQueryTests {
    static GameplayTagTable Table() =>
        new GameplayTagTableBuilder()
            .Add("Creature.Undead.Skeleton")
            .Add("Creature.Beast.Wolf")
            .Add("State.InCombat")
            .Add("State.Mounted")
            .Build();

    [Fact]
    public void AnEmptyQueryMatchesEverything() {
        Assert.True(GameplayTagQuery.Always.Matches(null));
        Assert.True(GameplayTagQuery.Always.Matches(new()));
        Assert.False(GameplayTagQuery.Always.IsSome);
    }

    [Fact]
    public void AllAnyAndNoneAreEvaluatedTogether() {
        var table = Table();

        var query = GameplayTagQuery.Resolve(
            table,
            all: ["Creature.Undead"],
            any: ["State.InCombat", "State.Mounted"],
            none: ["Creature.Beast"]
        );

        var undeadInCombat = new GameplayTagSet();
        undeadInCombat.Add(table.Require("Creature.Undead.Skeleton"));
        undeadInCombat.Add(table.Require("State.InCombat"));

        Assert.True(query.Matches(undeadInCombat));

        var undeadAtPeace = new GameplayTagSet();
        undeadAtPeace.Add(table.Require("Creature.Undead.Skeleton"));

        Assert.False(query.Matches(undeadAtPeace));

        var wolfInCombat = new GameplayTagSet();
        wolfInCombat.Add(table.Require("Creature.Beast.Wolf"));
        wolfInCombat.Add(table.Require("State.InCombat"));

        Assert.False(query.Matches(wolfInCombat));
    }

    [Fact]
    public void AQueryOverTagsTheContentDoesNotHaveFailsClosed() {
        var table = Table();

        Assert.False(GameplayTagQuery.Resolve(table, all: ["Creature.Dragon"]).Matches(new()));
        Assert.False(GameplayTagQuery.Resolve(table, any: ["Creature.Dragon"]).Matches(new()));
        Assert.True(GameplayTagQuery.Resolve(table, none: ["Creature.Dragon"]).Matches(new()));
    }
}
