// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     What an author works with: the track a clip carries, the schema the panel is generated from,
///     the ladder priorities are named on, templates, proposals, and the two reports that say when a
///     set is finished.
/// </summary>
public class AuthoringTests {
    // ---------------------------------------------------------------- the track

    [Fact]
    public void AMarkedUpClipBakesItsConstraintsAgainstTheRigItIsPlayedOn() {
        var rig = Rig();

        var content = new AnimationClipContent {
            Name = "Reach",
            Data = TestRigs.Hold("Reach", "Root", Vector3.Zero),
            Constraints = [
                new() {
                    Name = "right hand on the ledge",
                    Kind = GoalKind.Position,
                    Effector = "Wrist",
                    Chain = "Shoulder",
                    Begin = 0.2f,
                    End = 0.8f,
                    Priority = "contact",
                    Goal = new() { Kind = ConstraintFrameKind.World, Position = new(0.4f, 1.2f, 0f) }
                }
            ]
        };

        var clip = content.Bake(rig, PriorityLadder.Default);
        var track = clip.Constraints;

        Assert.NotNull(track);

        var tag = Assert.Single(track!.Tags.ToArray());

        Assert.Equal(0.2f, tag.Begin);
        Assert.Equal(rig.IndexOf("Wrist"), tag.Goal.Effector);
        Assert.Equal(rig.IndexOf("Shoulder"), tag.Goal.Solved.First);
        Assert.Equal(500, tag.Goal.Priority);
        Assert.IsType<PositionGoal>(tag.Goal);
    }

    /// <summary>⚠ A tag naming a joint the rig does not have resolves to nothing, not to joint zero.</summary>
    [Fact]
    public void AConstraintOnAJointTheRigLacksIsSkippedAndNamed() {
        var content = new AnimationClipContent {
            Data = TestRigs.Hold("Reach", "Root", Vector3.Zero),
            Constraints = [
                new() { Name = "tail", Effector = "Tail", Goal = new() { Kind = ConstraintFrameKind.World } },
                new() { Name = "hand", Effector = "Wrist", Goal = new() { Kind = ConstraintFrameKind.World } }
            ]
        };

        List<string> unresolved = [];
        var clip = content.Bake(Rig(), PriorityLadder.Default, null, unresolved);

        Assert.Equal(1, clip.Constraints!.Count);
        Assert.Equal("tail", Assert.Single(unresolved));
    }

    [Fact]
    public void EveryFrameKindResolvesToTheFrameItNames() {
        var rig = Rig();

        Assert.IsType<WorldFrame>(Frame(ConstraintFrameKind.World).Bake(rig));
        Assert.IsType<EntityFrame>(Frame(ConstraintFrameKind.Entity).Bake(rig));
        Assert.IsType<SocketFrame>(Frame(ConstraintFrameKind.Socket).Bake(rig));
        Assert.IsType<ProvidedFrame>(Frame(ConstraintFrameKind.Provided).Bake(rig));
        Assert.IsType<SurfaceFrame>(Frame(ConstraintFrameKind.Surface).Bake(rig));
        Assert.IsType<AttachmentFrame>(Frame(ConstraintFrameKind.Attachment).Bake(rig));

        var joint = Frame(ConstraintFrameKind.Joint);

        joint.Joint = "Wrist";
        Assert.IsType<JointFrame>(joint.Bake(rig));

        joint.Joint = "Tail";
        Assert.Null(joint.Bake(rig));
    }

    static ConstraintFrameRecord Frame(ConstraintFrameKind kind) => new() { Kind = kind, Slot = "ledge", Socket = "grip" };

    // ---------------------------------------------------------------- the schema

    /// <summary>
    ///     ⚠ Every field of a tag is either on a panel or deliberately hidden, and nothing is neither.
    /// </summary>
    /// <remarks>
    ///     This is the mechanism that keeps the generated inspector from drifting: adding a property to
    ///     the record and forgetting the schema fails here rather than shipping a field nobody can
    ///     edit.
    /// </remarks>
    [Fact]
    public void TheSchemaAccountsForEveryFieldOfATag() {
        List<string> orphans = [];

        foreach (var property in typeof(ConstraintTagRecord).GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
            if (GoalKindSchema.Hidden.ContainsKey(property.Name)) {
                continue;
            }

            var shown = false;

            foreach (var kind in Enum.GetValues<GoalKind>()) {
                shown |= GoalKindSchema.Shows(kind, property.Name);
            }

            if (!shown) {
                orphans.Add(property.Name);
            }
        }

        Assert.True(orphans.Count == 0, $"no panel shows, and nothing hides: {string.Join(", ", orphans)}");
    }

    /// <summary>A position goal has no aim axis, and the panel does not show one.</summary>
    [Fact]
    public void APanelShowsOnlyTheFieldsItsKindHas() {
        Assert.True(GoalKindSchema.Shows(GoalKind.Position, "Region"));
        Assert.False(GoalKindSchema.Shows(GoalKind.Position, "Axis"));
        Assert.False(GoalKindSchema.Shows(GoalKind.Position, "Other"));

        Assert.True(GoalKindSchema.Shows(GoalKind.Aim, "AuthoredDistance"));
        Assert.False(GoalKindSchema.Shows(GoalKind.Aim, "Region"));

        Assert.True(GoalKindSchema.Shows(GoalKind.Distance, "Other"));

        // And the fields every kind has are on every panel.
        foreach (var kind in Enum.GetValues<GoalKind>()) {
            Assert.True(GoalKindSchema.Shows(kind, "Begin"));
            Assert.True(GoalKindSchema.Shows(kind, "Priority"));
        }
    }

    [Fact]
    public void EverySchemaFieldNamesARealProperty() {
        foreach (var kind in Enum.GetValues<GoalKind>()) {
            foreach (var field in GoalKindSchema.For(kind)) {
                Assert.NotNull(typeof(ConstraintTagRecord).GetProperty(field.Property));
                Assert.False(string.IsNullOrWhiteSpace(field.Help), $"{field.Property} has no help");
            }
        }

        foreach (var field in GoalKindSchema.Common) {
            Assert.NotNull(typeof(ConstraintTagRecord).GetProperty(field.Property));
        }
    }

    // ---------------------------------------------------------------- the ladder

    [Fact]
    public void APriorityIsANameAndASubStepStaysInsideItsRung() {
        var ladder = PriorityLadder.Default;

        Assert.Equal(500, ladder.Value("contact"));
        Assert.Equal(501, ladder.Value("contact+1"));
        Assert.Equal(498, ladder.Value("contact-2"));

        // ⚠ A sub-step may not climb a whole rung. `look+200` outranking `aim` would make the
        // ladder's order a lie, and the order is the only thing it is for.
        Assert.True(ladder.Value("look+99") < ladder.Value("aim"));
        Assert.Equal(199, ladder.Value("look+200"));
    }

    [Fact]
    public void AnUnknownPriorityIsTheLowestRungRatherThanAnException() {
        Assert.Equal(0, PriorityLadder.Default.Value("whatever-the-other-project-called-it"));
        Assert.False(PriorityLadder.Default.Declares("whatever-the-other-project-called-it"));
        Assert.True(PriorityLadder.Default.Declares("contact+3"));
        Assert.True(PriorityLadder.Default.Declares(""));
    }

    // ---------------------------------------------------------------- templates

    [Fact]
    public void ATemplatesTimingsAreRelativeAndAreRemappedOntoTheSpanItIsAppliedTo() {
        var template = Seated();
        var placed = template.Instantiate(0.4f, 0.8f);

        Assert.Equal(2, placed.Count);

        // The first tag runs 0 → 0.5 of the template, so 0.4 → 0.6 of the clip.
        Assert.Equal(0.4f, placed[0].Begin, 1e-4f);
        Assert.Equal(0.6f, placed[0].End, 1e-4f);
        Assert.Equal(0.04f, placed[0].EaseIn, 1e-4f);

        foreach (var tag in placed) {
            Assert.Equal("seated", tag.Template);
            Assert.Equal(3, tag.TemplateVersion);
        }
    }

    /// <summary>⚠ Two clips from one template must not share the tag objects.</summary>
    [Fact]
    public void InstantiatingTwiceGivesTwoSetsOfTags() {
        var template = Seated();
        var one = template.Instantiate();
        var two = template.Instantiate();

        one[0].MaxWeight = 0.25f;

        Assert.Equal(1f, two[0].MaxWeight);
        Assert.Equal(1f, template.Tags[0].MaxWeight);
    }

    [Fact]
    public void AReapplyReportsWhatItWouldChangeAndLeavesHandPlacedTagsAlone() {
        var template = Seated();
        List<ConstraintTagRecord> onTheClip = [.. template.Instantiate()];

        // Somebody added one of their own, and the template moved on.
        onTheClip.Add(new() { Name = "chin clear of the shoulder", Effector = "Head" });
        template.Revision = 4;
        template.Tags[1].MaxWeight = 0.5f;
        template.Tags.Add(new() { Name = "left foot", Effector = "AnkleL", Begin = 0f, End = 1f });

        var diff = template.Compare(onTheClip);

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Changed);
        Assert.Equal(0, diff.Removed);
        Assert.Equal(0, diff.Edited);

        Assert.Contains(diff.Changes, change => change.Tag == "left foot" && change.Kind == TemplateChangeKind.Added);
        Assert.DoesNotContain(diff.Changes, change => change.Tag == "chin clear of the shoulder");
        Assert.Contains(diff.Changes, change => change.Kind == TemplateChangeKind.Changed && change.Detail.Contains("weight", StringComparison.Ordinal));
    }

    /// <summary>
    ///     ⚠ A tag from the current revision that differs is somebody's hand edit, not a template
    ///     change, and it is reported separately because a re-apply destroys it.
    /// </summary>
    [Fact]
    public void AHandEditToATemplatesOwnTagIsCalledOutSeparately() {
        var template = Seated();
        List<ConstraintTagRecord> onTheClip = [.. template.Instantiate()];

        onTheClip[0].MaxWeight = 0.3f;

        var diff = template.Compare(onTheClip);

        Assert.Equal(1, diff.Edited);
        Assert.Equal(0, diff.Changed);
        Assert.Contains("hand edits", diff.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ATemplateThatHasNotChangedWouldDoNothing() {
        var template = Seated();

        Assert.True(template.Compare(template.Instantiate()).IsEmpty);
    }

    static ConstraintTemplateContent Seated() =>
        new() {
            Name = "seated",
            Revision = 3,
            Meaning = "A character sitting: both hands on the arms of the chair, hips on the seat.",
            Tags = [
                new() { Name = "right hand", Effector = "Wrist", Begin = 0f, End = 0.5f, EaseIn = 0.1f },
                new() { Name = "hips", Effector = "Root", Begin = 0f, End = 1f }
            ]
        };

    // ---------------------------------------------------------------- proposals

    /// <summary>A hand that rests on the belly for half the clip is proposed; one that passes is not.</summary>
    [Fact]
    public void AContactHeldLongEnoughIsProposedAndOneThatPassesByIsNot() {
        var rig = Rig();

        // The shape is placed where the hand actually ends up rather than where arithmetic says it
        // should. Deriving the contact from the posed rig is setup, not circularity: what is being
        // asserted is that the pass notices a contact that exists, and hand-computing the swing would
        // be asserting the rotation convention instead.
        var shapes = Shapes(rig, Resting(rig));

        var resting = ConstraintProposals.Find(
            rig,
            Reaching(rig, hold: true),
            shapes,
            [new(rig.IndexOf("Wrist"), rig.IndexOf("Shoulder"), Vector3.Zero)],
            ProposalSettings.Default
        );

        var proposal = Assert.Single(resting);

        Assert.Equal(Symbol.Intern("belly"), proposal.Shape);
        Assert.Equal(ConstraintFrameKind.Surface, proposal.Tag.Goal.Kind);
        Assert.Equal("belly", proposal.Tag.Goal.Shape);
        Assert.True(proposal.Tag.End - proposal.Tag.Begin > 0.3f, "the span should cover the hold");
        Assert.True(proposal.Confidence > 0.5f, $"a still, near, long contact should be confident, was {proposal.Confidence}");

        // ⚠ The ease is not zero. A contact that snaps on is the first thing an author fixes by hand,
        // and a default that creates work is a default nobody uses.
        Assert.True(proposal.Tag.EaseIn > 0f);

        var passing = ConstraintProposals.Find(
            rig,
            Reaching(rig, hold: false),
            shapes,
            [new(rig.IndexOf("Wrist"), rig.IndexOf("Shoulder"), Vector3.Zero)],
            ProposalSettings.Default
        );

        Assert.Empty(passing);
    }

    /// <summary>⚠ A shape hanging off the effector travels with it and is never a contact.</summary>
    [Fact]
    public void AShapeOnTheEffectorItselfIsNeverProposed() {
        var rig = Rig();

        var found = ConstraintProposals.Find(
            rig,
            Reaching(rig, hold: true),
            Shapes(rig, Resting(rig)),
            [new(rig.IndexOf("Wrist"), rig.IndexOf("Shoulder"), Vector3.Zero)],
            ProposalSettings.Default
        );

        Assert.DoesNotContain(found, proposal => proposal.Shape == Symbol.Intern("right-palm"));
    }

    // ---------------------------------------------------------------- the shape audit

    [Fact]
    public void AShapeThatNeverMovesIsReportedAndOneThatDoesIsNot() {
        var rig = Rig();
        var found = ProxyShapeAudit.Audit(Shapes(rig), rig, [Reaching(rig, hold: true)]);

        // The belly hangs off the spine, which the clip does not touch.
        Assert.Contains(found, entry => entry.Shape == Symbol.Intern("belly"));
        Assert.DoesNotContain(found, entry => entry.Shape == Symbol.Intern("right-palm"));
    }

    /// <summary>The failure an author cannot notice by reading either set on its own.</summary>
    [Fact]
    public void ANamePresentInOneSetAndMissingFromAnotherIsReportedBothWays() {
        var rig = Rig();

        var theirs = ProxyShapeSet.Of(
            "Other body",
            null,
            new ProxyShape {
                Name = Symbol.Intern("palm-r"),
                Kind = ShapeKind.Box,
                Joint = rig.IndexOf("Wrist"),
                Dimensions = ShapeParams.Box(new(0.04f, 0.02f, 0.08f))
            }
        );

        var found = ProxyShapeAudit.Compare(Shapes(rig), theirs);

        Assert.Contains(found, entry => entry.Shape == Symbol.Intern("right-palm"));
        Assert.Contains(found, entry => entry.Shape == Symbol.Intern("palm-r"));
        Assert.Contains(found, entry => entry.Message.Contains("silently does nothing", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoShapesDeepInsideEachOtherOnNonAdjacentJointsAreReported() {
        var rig = Rig();

        var overlapping = ProxyShapeSet.Of(
            "Overlapping",
            null,
            new ProxyShape {
                Name = Symbol.Intern("belly"),
                Kind = ShapeKind.Sphere,
                Joint = rig.IndexOf("Spine"),
                Dimensions = ShapeParams.Sphere(0.6f)
            },
            new ProxyShape {
                Name = Symbol.Intern("right-palm"),
                Kind = ShapeKind.Sphere,
                Joint = rig.IndexOf("Wrist"),
                Dimensions = ShapeParams.Sphere(0.6f)
            }
        );

        Assert.Contains(
            ProxyShapeAudit.Audit(overlapping, rig),
            entry => entry.Message.Contains("inside one another", StringComparison.Ordinal)
        );

        // And the ordinary case is silent: a shoulder overlapping an upper arm always will.
        Assert.DoesNotContain(
            ProxyShapeAudit.Audit(Shapes(rig), rig),
            entry => entry.Message.Contains("inside one another", StringComparison.Ordinal)
        );
    }

    // ---------------------------------------------------------------- the move set editor

    [Fact]
    public void AnExplanationAgreesWithTheScoreTheSelectorUsed() {
        var moves = Locomotion();

        var query = new MoveQuery {
            Numeric = new() { Speed = 3.2f },
            Preferred = [new(Facet.Of("style", "injured"), 2f)],
            Previous = MoveKey.Of("walk"),
            RepeatPenalty = 1f
        };

        var explained = MoveExplanations.Explain(moves, query);
        var chosen = QueryMoveSelector.Shared.Choose(moves, query, DefaultMoveScorer.Shared);

        // The order is the selector's, so the first eligible row is what would actually play.
        Assert.Equal(moves[chosen.Index].Name, explained[0].Name);

        // And every score is the sum of the terms shown for it, or the breakdown is decoration.
        foreach (var row in explained) {
            if (!row.Eligible) {
                continue;
            }

            var total = 0f;

            foreach (var term in row.Terms) {
                total += term.Amount;
            }

            Assert.Equal(row.Score, total, 1e-4f);
        }
    }

    [Fact]
    public void AnIneligibleMoveSaysWhichFacetItDoesNotHave() {
        var moves = Locomotion();

        var explained = MoveExplanations.Explain(
            moves,
            new MoveQuery { Required = FacetSet.Of(("style", "injured")), Numeric = new() { Speed = 1f } }
        );

        var refused = explained.First(row => !row.Eligible);

        Assert.Equal(Facet.Of("style", "injured"), refused.Missing);
        Assert.Contains("does not say", refused.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Where the set has nothing to offer — not an error, and the thing to see before shipping.</summary>
    [Fact]
    public void CoverageNamesTheSpeedsAndCombinationsWithNothingBehindThem() {
        var coverage = MoveCoverage.Sweep(
            Locomotion(),
            [FacetSet.Empty, FacetSet.Of(("style", "injured"))],
            fastest: 9f,
            steps: 10
        );

        Assert.True(coverage.FallsBack > 0, "nine metres a second is past anything in the set");
        Assert.True(coverage.Worst.FallsBack);
        Assert.Contains("falling back", coverage.ToString(), StringComparison.Ordinal);

        // Nothing in the set says injured, so every injured question is unanswered rather than
        // answered badly — which is a different thing and reads differently in the matrix.
        Assert.True(coverage.Unanswered >= 10);
    }

    static MoveSet Locomotion() =>
        MoveSet.Of(
            "Locomotion",
            new MoveEntry(
                "walk",
                new Motions.ClipMotion(AnimationClip.Create(TestRigs.Hold("walk", "Root", Vector3.Zero), Rig())),
                FacetSet.Of(("role", "loop")),
                new MoveTraits { Speed = 1.4f, MinRate = 0.8f, MaxRate = 1.2f }
            ),
            new MoveEntry(
                "run",
                new Motions.ClipMotion(AnimationClip.Create(TestRigs.Hold("run", "Root", Vector3.Zero), Rig())),
                FacetSet.Of(("role", "loop")),
                new MoveTraits { Speed = 4f, MinRate = 0.85f, MaxRate = 1.15f }
            )
        );

    // ---------------------------------------------------------------- the rig

    static Skeleton Rig() =>
        Skeleton.Create(
            TestRigs.Build(
                "Body",
                ("Root", -1, Vector3.Zero),
                ("Spine", 0, new Vector3(0f, 1f, 0f)),
                ("Head", 1, new Vector3(0f, 0.55f, 0f)),
                ("Shoulder", 1, new Vector3(0.25f, 0.35f, 0f)),
                ("Elbow", 3, new Vector3(0f, -0.3f, 0f)),
                ("Wrist", 4, new Vector3(0f, -0.3f, 0f))
            )
        );

    /// <summary>Where the belly has to sit for the resting hand to be exactly on its skin.</summary>
    static Vector3 Resting(Skeleton rig) {
        var pose = new SkeletonPose(rig);
        var model = new BoneTransform[rig.JointCount];

        Reaching(rig, hold: true).Sample(1f, pose.Bones);
        pose.ComputeModelSpace(model);

        var spine = model[rig.IndexOf("Spine")].Translation;
        var wrist = model[rig.IndexOf("Wrist")].Translation;
        var towards = wrist - spine;

        // A radius back along the line from the spine, so the wrist lands on the surface rather than
        // inside it.
        return (wrist - (Vector3.Normalize(towards) * 0.18f)) - spine;
    }

    static ProxyShapeSet Shapes(Skeleton rig, Vector3? belly = null) =>
        ProxyShapeSet.Of(
            "Body",
            null,
            new ProxyShape {
                Name = Symbol.Intern("belly"),
                Kind = ShapeKind.Sphere,
                Joint = rig.IndexOf("Spine"),
                Offset = new(belly ?? new Vector3(0f, 0.1f, 0.12f), Quaternion.Identity, Vector3.One),
                Dimensions = ShapeParams.Sphere(0.18f)
            },
            new ProxyShape {
                Name = Symbol.Intern("right-palm"),
                Kind = ShapeKind.Box,
                Joint = rig.IndexOf("Wrist"),
                Dimensions = ShapeParams.Box(new(0.04f, 0.02f, 0.08f))
            }
        );

    /// <summary>An arm that swings in, and either rests on the belly or carries on past it.</summary>
    static AnimationClip Reaching(Skeleton rig, bool hold) {
        // The wrist starts at (0.25, 1.05, 0) and the belly's surface is around (0, 1.1, 0.3). A
        // rotation of the shoulder brings the hand across the front of the body.
        var times = new List<float>();
        var turns = new List<Quaternion>();

        for (var index = 0; index <= 20; index++) {
            var phase = index / 20f;

            times.Add(phase);

            // ⚠ The pass-through sweeps straight past the contact rather than easing up to it. A
            // sine that *peaks* at the contact is flat there, so the hand dwells for a sixth of the
            // clip and is correctly proposed — which makes it a bad negative case, not a bug.
            var amount = hold ? MathF.Min(phase / 0.25f, 1f) : phase * 2f;

            turns.Add(Quaternion.FromAxisAngle(Vector3.UnitZ, amount * -1.2f));
        }

        return AnimationClip.Create(
            new() {
                Name = hold ? "rest" : "pass",
                Duration = 1f,
                Channels = [new() { Target = "Shoulder", RotationTimes = [.. times], Rotations = [.. turns] }]
            },
            rig
        );
    }
}
