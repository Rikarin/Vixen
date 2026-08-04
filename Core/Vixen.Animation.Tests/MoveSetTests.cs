// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Motions;
using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>A movement vocabulary as a set, and picking from it as a query.</summary>
/// <remarks>
///     The claim under test is the one the design rests on: <b>nobody authors the cross-product</b>.
///     A set with three injured clips overlaid on a full locomotion set produces correct injured
///     locomotion, and the query degrades on its own where the overlay has nothing to say.
/// </remarks>
public sealed class MoveSetTests {
    static readonly Skeleton Rig = TestRigs.Chain();

    [Fact]
    public void ASymbolIsTheSameNumberForTheSameWord() {
        Assert.Equal(Symbol.Intern("walk"), Symbol.Intern("walk"));
        Assert.NotEqual(Symbol.Intern("walk"), Symbol.Intern("run"));
        Assert.False(Symbol.Intern("").IsSome);
        Assert.False(Symbol.Intern(null).IsSome);
    }

    /// <summary>⚠ The number is the hash, so it does not depend on what a process saw first.</summary>
    /// <remarks>
    ///     The property that makes a tie-break safe to replicate. A table assigning ids in first-seen
    ///     order would give two machines different numbers for the same word, and a selection settled
    ///     on those numbers would then differ between them.
    /// </remarks>
    [Fact]
    public void ASymbolDoesNotDependOnInterningOrder() {
        var late = Symbol.Intern("zzz-never-seen-before");
        var early = Symbol.Intern("aaa-also-never-seen");

        Assert.Equal(2686853648u, Symbol.Intern("walk").Id);
        Assert.NotEqual(late, early);
        Assert.Equal(late, Symbol.Intern("zzz-never-seen-before"));
    }

    [Fact]
    public void AFacetSetSortsDeduplicatesAndMatchesBySubset() {
        var facets = FacetSet.Of(("gait", "walk"), ("condition", "injured"), ("gait", "walk"));

        Assert.Equal(2, facets.Count);
        Assert.True(facets.Contains(Facet.Of("gait", "walk")));
        Assert.True(facets.ContainsAll(FacetSet.Of(("gait", "walk"))));
        Assert.True(facets.ContainsAll(FacetSet.Empty));
        Assert.False(facets.ContainsAll(FacetSet.Of(("gait", "run"))));
        Assert.False(facets.ContainsAll(FacetSet.Of(("gait", "walk"), ("surface", "ice"))));
    }

    /// <summary>⚠ Two values on one key are both kept: a move that suits either.</summary>
    [Fact]
    public void TwoValuesOnOneKeyBothSurvive() {
        var facets = FacetSet.Of(("surface", "ice"), ("surface", "snow"));

        Assert.Equal(2, facets.Count);
        Assert.True(facets.Contains(Facet.Of("surface", "ice")));
        Assert.True(facets.Contains(Facet.Of("surface", "snow")));
    }

    [Fact]
    public void AnOverlayReplacesByKeyAndKeepsTheRest() {
        var baseSet = MoveSet.Of("human", Move("walk", 1.4f), Move("run", 4.2f), Move("stop", 0f));
        var overlaid = MoveSet.Compose("guard", [baseSet], Move("walk", 0.9f));

        Assert.Equal(3, overlaid.Count);
        Assert.True(overlaid.TryGet(MoveKey.Of("walk"), out var walk));
        Assert.Equal(0.9f, walk!.Traits.Speed);
        Assert.True(overlaid.TryGet(MoveKey.Of("run"), out var run));
        Assert.Equal(4.2f, run!.Traits.Speed);
    }

    /// <summary>⚠ A list of bases, so the diamond has a defined answer: later wins.</summary>
    [Fact]
    public void TheLastBaseToSupplyAKeyWins() {
        var body = MoveSet.Of("body", Move("walk", 1.4f));
        var personality = MoveSet.Of("personality", Move("walk", 1.1f));

        Assert.True(MoveSet.Compose("both", [body, personality]).TryGet(MoveKey.Of("walk"), out var walk));
        Assert.Equal(1.1f, walk!.Traits.Speed);
    }

    /// <summary>Composition is order-independent, so two builds of one set are the same set.</summary>
    [Fact]
    public void AComposedSetIsInKeyOrderWhateverOrderItWasBuiltIn() {
        var forwards = MoveSet.Of("s", Move("a", 1f), Move("b", 1f), Move("c", 1f));
        var backwards = MoveSet.Of("s", Move("c", 1f), Move("b", 1f), Move("a", 1f));

        Assert.Equal(
            forwards.Entries.ToArray().Select(entry => entry.Name),
            backwards.Entries.ToArray().Select(entry => entry.Name)
        );
    }

    /// <summary>The claim: three injured clips, no second graph, correct injured locomotion.</summary>
    /// <remarks>
    ///     ⚠ <b>And the degradation is the other half of it.</b> The overlay has an injured walk and
    ///     no injured sprint, so asking for a sprint while injured finds the ordinary sprint rather
    ///     than nothing — which is what every hand-written fallback rule is trying to approximate.
    /// </remarks>
    [Fact]
    public void AnInjuredOverlayOfThreeClipsGivesInjuredLocomotion() {
        var human = MoveSet.Of(
            "human",
            Move("walk", 1.4f, ("gait", "walk")),
            Move("jog", 2.8f, ("gait", "jog")),
            Move("sprint", 6.5f, ("gait", "sprint")),
            Move("idle", 0f, ("gait", "idle"))
        );

        var injured = MoveSet.Compose(
            "human-injured",
            [human],
            Move("walk-injured", 0.8f, ("gait", "walk"), ("condition", "injured")),
            Move("jog-injured", 1.6f, ("gait", "jog"), ("condition", "injured")),
            Move("idle-injured", 0f, ("gait", "idle"), ("condition", "injured"))
        );

        Assert.Equal(7, injured.Count);

        // Injured and walking: the injured walk, not the ordinary one.
        Assert.Equal("walk-injured", Pick(injured, 0.8f, "injured"));

        // Injured and sprinting: nothing injured is that fast, so the plain sprint — degraded, not
        // absent.
        Assert.Equal("sprint", Pick(injured, 6.5f, "injured"));

        // Not injured: the ordinary set, untouched by the overlay's existence.
        Assert.Equal("walk", Pick(injured, 1.4f, condition: null));
        Assert.Equal("jog", Pick(injured, 2.8f, condition: null));
    }

    /// <summary>A move is retimed within the range it admits, and no further.</summary>
    [Fact]
    public void AMoveIsStretchedOnlyAsFarAsItSaysItCanBe() {
        var stretchy = new MoveEntry(
            "walk",
            Clip(),
            FacetSet.Of(("gait", "walk")),
            new() { Speed = 1.4f, MinRate = 0.85f, MaxRate = 1.15f }
        );

        var rigid = new MoveEntry("stop", Clip(), FacetSet.Of(("gait", "stop")), new() { Speed = 1f });

        Assert.Equal(1.15f, stretchy.Traits.RateFor(10f), 1e-4f);
        Assert.Equal(0.85f, stretchy.Traits.RateFor(0.1f), 1e-4f);
        Assert.Equal(1f, stretchy.Traits.RateFor(1.4f), 1e-4f);
        Assert.Equal(1f, rigid.Traits.RateFor(5f), 1e-4f);
    }

    /// <summary>A required facet nothing has selects nothing, rather than something wrong.</summary>
    [Fact]
    public void ARequirementNothingMeetsSelectsNothing() {
        var set = MoveSet.Of("s", Move("walk", 1.4f, ("gait", "walk")));

        var chosen = QueryMoveSelector.Shared.Choose(
            set,
            new MoveQuery { Required = FacetSet.Of(("gait", "swim")) },
            DefaultMoveScorer.Shared
        );

        Assert.False(chosen.HasMove);
    }

    /// <summary>⚠ A tie is settled by key, which is the same everywhere.</summary>
    [Fact]
    public void ATieIsBrokenTheSameWayEveryTime() {
        var set = MoveSet.Of("s", Move("a", 1f, ("gait", "walk")), Move("b", 1f, ("gait", "walk")));
        var query = new MoveQuery { Numeric = new() { Speed = 1f } };

        var first = QueryMoveSelector.Shared.Choose(set, query, DefaultMoveScorer.Shared);

        for (var run = 0; run < 8; run++) {
            Assert.Equal(first.Index, QueryMoveSelector.Shared.Choose(set, query, DefaultMoveScorer.Shared).Index);
        }

        // And it is the lower key rather than the lower index, which is what makes it survive the
        // set being composed from a different order.
        var lower = set[0].Key < set[1].Key ? 0 : 1;
        Assert.Equal(lower, first.Index);
    }

    [Fact]
    public void TheRepeatPenaltyPushesOffAMoveAlreadyPlaying() {
        var set = MoveSet.Of("s", Move("a", 1f, ("gait", "walk")), Move("b", 1f, ("gait", "walk")));
        var plain = QueryMoveSelector.Shared.Choose(set, new(), DefaultMoveScorer.Shared);

        var penalised = QueryMoveSelector.Shared.Choose(
            set,
            new MoveQuery { Previous = set[plain.Index].Key, RepeatPenalty = 1f },
            DefaultMoveScorer.Shared
        );

        Assert.NotEqual(plain.Index, penalised.Index);
    }

    /// <summary>⚠ Two words colliding is refused at composition, not discovered in a frame.</summary>
    [Fact]
    public void AVocabularyWhoseWordsCollideIsRefused() {
        // Found by search: these two distinct words share a 32-bit FNV-1a hash.
        const string One = "costarring";
        const string Two = "liquid";

        Assert.Equal(Symbol.Intern(One).Id, Symbol.Intern(Two).Id);

        var failure = Assert.Throws<InvalidOperationException>(
            () => MoveSet.Of(
                "colliding",
                Move("a", 1f, ("gait", One)),
                Move("b", 1f, ("gait", Two))
            )
        );

        Assert.Contains(One, failure.Message, StringComparison.Ordinal);
        Assert.Contains(Two, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGaitModelReportsSpeedAndTreatsACrawlAsStanding() {
        var model = new BipedGaitModel();
        var targets = default(MoveTargets);

        model.Describe(new(new(3f, 4f), 0f, 0.5f, true), ref targets);
        Assert.Equal(5f, targets.Speed, 1e-4f);
        Assert.Equal(0.5f, targets.TurnRate, 1e-4f);

        model.Describe(new(new(0.05f, 0f), 0f, 0f, true), ref targets);
        Assert.Equal(0f, targets.Speed);
    }

    /// <summary>⚠ Shorter legs ask the set for a faster gait, because stride follows leg length.</summary>
    [Fact]
    public void AShorterCharacterAsksForAFasterGaitAtTheSameSpeed() {
        var reference = default(MoveTargets);
        var shorter = default(MoveTargets);

        new BipedGaitModel().Describe(new(new(0f, 3f), 0f, 0f, true), ref reference);
        new BipedGaitModel { LegLength = 0.45f }.Describe(new(new(0f, 3f), 0f, 0f, true), ref shorter);

        Assert.Equal(3f, reference.Speed, 1e-4f);
        Assert.Equal(6f, shorter.Speed, 1e-4f);
    }

    /// <summary>A move set is a motion, so everything above it needs no change.</summary>
    [Fact]
    public void AMoveSetIsAMotionAndPosesThroughWhateverItSelected() {
        var set = MoveSet.Of("s", Move("walk", 1.4f, ("gait", "walk")));
        var motion = new MoveSetMotion(set);

        Assert.IsAssignableFrom<Motion>(motion);
        Assert.Null(motion.Current);

        Assert.True(motion.Ask(new MoveQuery { Numeric = new() { Speed = 1.4f } }));
        Assert.Equal("walk", motion.Current!.Name);

        // Asked again with the same question, it does not re-decide.
        Assert.False(motion.Ask(new MoveQuery { Numeric = new() { Speed = 1.4f } }));

        var pose = new BoneTransform[Rig.JointCount];
        var parameters = new AnimationParameters();
        var scratch = new PoseScratch(Rig.JointCount);

        motion.Evaluate(
            new MotionContext(parameters, scratch, 0.5f, 0.4f, 0, false, null, 0, "s", 1f),
            pose
        );

        Assert.Equal(Rig.JointCount, pose.Length);
    }

    /// <summary>A parameter that holds a word is the facet a query is built from.</summary>
    [Fact]
    public void ASymbolParameterIsAFacet() {
        var parameters = new AnimationParameters();
        parameters.SetSymbol("surface", "ice");
        parameters.SetFloat("Speed", 3f);

        Assert.Equal(Symbol.Intern("ice"), parameters.GetSymbol("surface"));
        Assert.True(parameters.TryGetFacet(parameters.IndexOf("surface"), out var facet));
        Assert.Equal(Facet.Of("surface", "ice"), facet);

        // ⚠ A number is not a word, however convenient that would be.
        Assert.False(parameters.TryGetFacet(parameters.IndexOf("Speed"), out _));
        Assert.False(parameters.GetSymbol("Speed").IsSome);
    }

    static string Pick(MoveSet set, float speed, string? condition) {
        // ⚠ Two, not five, and the number matters. A preference and a numeric error share one
        // scale, so the weight is literally "how many metres a second of speed error this condition
        // is worth". At five, an injured character sprinting picks the injured jog — a 4.9 m/s error
        // — over the plain sprint, which is the wrong trade and exactly the kind of thing the
        // editor's score breakdown is for.
        WeightedFacet[] preferred = condition is null
            ? []
            : [new(Facet.Of("condition", condition), 2f)];

        var chosen = QueryMoveSelector.Shared.Choose(
            set,
            new MoveQuery { Preferred = preferred, Numeric = new() { Speed = speed } },
            DefaultMoveScorer.Shared
        );

        return set[chosen.Index].Name;
    }

    static MoveEntry Move(string name, float speed, params (string Key, string Value)[] facets) =>
        new(name, Clip(), FacetSet.Of(facets), new() { Speed = speed });

    static ClipMotion Clip() =>
        new(AnimationClip.Create(TestRigs.Hold("held", "Mid", Vector3.UnitY), Rig));
}
