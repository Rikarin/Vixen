// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.Assets.Animation;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>The sequencer's second job: saying what a clip was marked up against.</summary>
public class AuthoringContextTests {
    /// <summary>
    ///     ⚠ <b>Authoring-time only, and the one-way door is where it stops.</b> A constraint that
    ///     could not be resolved from the live game alone is a bug, not a feature — so the reference
    ///     survives a save and never reaches the artefact.
    /// </summary>
    [Fact]
    public void TheContextRoundTripsInTheClipAndNeverReachesTheArtefact() {
        var clip = new AnimationClipAsset { Name = "Reach", AuthoringContext = "Assets/Scenes/reach.vxseq" };

        var reopened = AnimationClipAsset.FromYaml(clip.ToYaml());

        Assert.Equal("Assets/Scenes/reach.vxseq", reopened.AuthoringContext);
        Assert.DoesNotContain("vxseq", System.Text.Json.JsonSerializer.Serialize(clip.ToContent().Extensions), StringComparison.Ordinal);

        // The runtime record has no field for it at all, which is the strongest form the rule can
        // take: there is nowhere for a build to read it from even by mistake.
        Assert.Null(typeof(AnimationClipContent).GetProperty("AuthoringContext"));
    }

    /// <summary>
    ///     ⚠ <b>Without a named subject, every hand is in contact with its own arm.</b> Saying the
    ///     scene is unusable beats guessing which actor the clip is about.
    /// </summary>
    [Fact]
    public void ASceneWithNoNamedSubjectIsNotUsable() {
        var sequence = Scene();
        sequence.Subject = string.Empty;

        var context = AuthoringContext.From(sequence);

        Assert.Null(context.Subject);
        Assert.False(context.IsUsable);
    }

    [Fact]
    public void TheContextSaysWhoWasWhereAndWhatWasHeld() {
        var context = AuthoringContext.From(Scene());

        Assert.True(context.IsUsable);
        Assert.Equal("hero", context.Subject?.Name);

        // Before the pick-up: the mug is on the table and nothing is held.
        var before = Assert.Single(context.At(0f));

        Assert.Equal("mug", before.Name);
        Assert.Equal(string.Empty, before.Held);
        Assert.Equal(1.0f, before.Where.Translation.X, 3);

        // ⚠ The last key at or before the moment, not the nearest: reading the nearest would put the
        // mug in the hand for the half second before it was picked up.
        Assert.Equal(string.Empty, Assert.Single(context.At(1.5f)).Held);
        Assert.Equal("Hand_r", Assert.Single(context.At(2.5f)).Held);
    }

    /// <summary>
    ///     ⚠ <b>Everything is brought into the subject's model space.</b> A prop recorded in world
    ///     space has to have the subject's own placement taken back off it, or every proposal is
    ///     measured against a prop as far away as the character is from the origin.
    /// </summary>
    [Fact]
    public void APropBecomesAShapeInTheSubjectsOwnSpace() {
        var context = AuthoringContext.From(Scene());
        var rig = Rig();

        var augmented = context.Augment(Body(rig), rig, 0f);

        Assert.Equal(2, augmented.Count);

        var mug = augmented[augmented.IndexOf("mug")];

        // The hero stands at x = 4 and the mug at x = 1, so in the hero's own space it is at −3.
        Assert.Equal(0, mug.Joint);
        Assert.Equal(-3f, mug.Offset.Translation.X, 3);
    }

    /// <summary>Once it is held, the prop hangs off the socket's joint rather than the root.</summary>
    [Fact]
    public void AHeldPropHangsOffTheJointItIsHeldBy() {
        var context = AuthoringContext.From(Scene());
        var rig = Rig();

        var augmented = context.Augment(Body(rig), rig, 2.5f);
        var mug = augmented[augmented.IndexOf("mug")];

        Assert.Equal(rig.IndexOf("Hand_r"), mug.Joint);
        Assert.Equal(Vector3.Zero, mug.Offset.Translation);
    }

    /// <summary>
    ///     A socket the rig does not have puts the prop on the root rather than dropping it: a dropped
    ///     prop is a proposal that silently never happens.
    /// </summary>
    [Fact]
    public void APropHeldByAJointThisRigLacksIsStillPlaced() {
        var sequence = Scene();

        sequence.Tracks.Find(track => track.Kind == SequenceTrackKind.Attachment)!.Keys[0].Text = "Tentacle";

        var context = AuthoringContext.From(sequence);
        var rig = Rig();

        var augmented = context.Augment(Body(rig), rig, 2.5f);

        Assert.Equal(2, augmented.Count);
        Assert.Equal(0, augmented[augmented.IndexOf("mug")].Joint);
    }

    /// <summary>The whole point: the augmented set is what the proposal pass measures against.</summary>
    [Fact]
    public void TheAugmentedSetIsWhatTheProposalPassReads() {
        var context = AuthoringContext.From(Scene());
        var rig = Rig();

        // ⚠ A mug the size of a mug. Proximity is measured to the *surface*, so a prop modelled as a
        // large sphere with the hand at its centre reads as far away — which is right, and is why the
        // size the scene shapes props at is a parameter rather than a constant.
        var augmented = context.Augment(Body(rig), rig, 1.5f, size: 0.02f);

        var found = ConstraintProposals.Find(
            rig,
            AnimationClip.Create(new() { Name = "Reach", Duration = 1f }, rig),
            augmented,
            [new(rig.IndexOf("Hand_r"), rig.IndexOf("Spine"), Vector3.Zero)],
            ProposalSettings.Default,
            samples: 8
        );

        // The mug is on the hand's own joint, so the hand is on it for the whole clip — which is
        // exactly the contact an author would want proposed.
        Assert.Contains(found, proposal => proposal.Shape == Symbol.Intern("mug"));
    }

    /// <summary>
    ///     The whole path, from a button to a tag: the clip editor asks, the context supplies the
    ///     scene's shapes, the proposal pass measures, and nothing is applied until somebody accepts.
    /// </summary>
    [Fact]
    public void TheClipEditorProposesAndNothingIsAppliedUntilItIsAccepted() {
        using var project = new EditorFixture();
        var path = project.WriteAsset("Assets/reach.vxanim", "name: Reach\nduration: 1.0\n");

        var document = new AnimationClipDocument(project.Project, AssetId.New(), path);

        // ⚠ With nothing bound it says why rather than answering an empty list. "Found nothing" and
        // "could not look" are different facts and an author acts on them differently.
        Assert.Empty(document.Propose());
        Assert.Contains("No scene is bound", document.ProposalError, StringComparison.Ordinal);

        var rig = Rig();
        var context = AuthoringContext.From(Scene());

        document.Scene = _ => new(
            rig,
            context.Augment(Body(rig), rig, 1.5f, size: 0.02f),
            [new(rig.IndexOf("Hand_r"), rig.IndexOf("Spine"), Vector3.Zero)],
            ProposalSettings.Default
        );

        var found = document.Propose();

        Assert.NotEmpty(found);
        Assert.Empty(document.Clip.Constraints);

        // Accepting one is an ordinary undoable edit, and the proposal stays in the list.
        document.Accept(found[0]);

        Assert.Single(document.Clip.Constraints);
        Assert.Equal(found.Count, document.Proposals.Count);

        document.Stack.Undo();
        Assert.Empty(document.Clip.Constraints);
    }

    /// <summary>A clip that names no context is told so, rather than told nothing was found.</summary>
    [Fact]
    public void AClipWithNoContextIsToldWhatIsMissing() {
        using var project = new EditorFixture();
        var path = project.WriteAsset("Assets/reach.vxanim", "name: Reach\nduration: 1.0\n");

        var document = new AnimationClipDocument(project.Project, AssetId.New(), path) { Scene = _ => null };

        Assert.Empty(document.Propose());
        Assert.Contains("names no authoring context", document.ProposalError, StringComparison.Ordinal);
    }

    static SequenceAsset Scene() =>
        new() {
            Name = "reach",
            Duration = 3f,
            Subject = "hero",
            Tracks = [
                new() {
                    Name = "hero",
                    Kind = SequenceTrackKind.Transform,
                    Clip = "Assets/Anim/Reach.vxanim",
                    Keys = [new() { Time = 0f, Value = [4f, 0f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f] }]
                },
                new() {
                    Name = "mug",
                    Kind = SequenceTrackKind.Transform,
                    Keys = [
                        // On the table, then in the air where the hand is — and picked up a second
                        // after that, so "near it" and "holding it" are two distinct moments to ask
                        // about.
                        new() { Time = 0f, Value = [1f, 0.9f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f] },
                        new() { Time = 1f, Value = [4.52f, 1.2f, 0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f] }
                    ]
                },
                new() {
                    Name = "mug",
                    Kind = SequenceTrackKind.Attachment,
                    Keys = [new() { Time = 2f, Text = "Hand_r" }]
                }
            ]
        };

    static ProxyShapeSet Body(Skeleton rig) =>
        ProxyShapeSet.Of(
            "hero",
            null,
            new ProxyShape {
                Name = Symbol.Intern("belly"),
                Kind = ShapeKind.Sphere,
                Joint = rig.IndexOf("Spine"),
                Dimensions = ShapeParams.Sphere(0.2f)
            }
        );

    static Skeleton Rig() {
        (string Name, int Parent, Vector3 Offset)[] joints = [
            ("Root", -1, Vector3.Zero),
            ("Spine", 0, new(0f, 0.9f, 0f)),
            ("Hand_r", 1, new(0.5f, 0.3f, 0f))
        ];

        var model = new Matrix4x4[joints.Length];
        var built = new SkeletonJoint[joints.Length];

        for (var index = 0; index < joints.Length; index++) {
            var local = Matrix4x4.FromTranslation(joints[index].Offset);

            model[index] = joints[index].Parent >= 0 ? local * model[joints[index].Parent] : local;

            Matrix4x4.Invert(model[index], out var inverse);

            built[index] = new() { Name = joints[index].Name, Parent = joints[index].Parent, InverseBindPose = inverse };
        }

        return Skeleton.Create(new() { Name = "hero", Joints = built });
    }
}
