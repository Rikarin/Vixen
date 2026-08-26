// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ecs;
using Vixen.Animation.Motions;
using Vixen.Animation.StateMachine;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     The scalar weight track, from the format to the component a frame draws from.
/// </summary>
/// <remarks>
///     <b>Every number here is exact.</b> The keys are at whole and half seconds and the weights are
///     halves and quarters, so a lerp between them is representable and an assertion can be an
///     equality rather than a tolerance — the discipline the blend-shape delta fixtures already keep,
///     for the same reason: a tolerance is where an off-by-one in the interpolation hides.
/// </remarks>
public class BlendShapeTrackTests {
    readonly Skeleton skeleton = TestRigs.Chain();

    /// <summary>A clip with one weight track on one shape, and a joint track beside it.</summary>
    static AnimationClipData Blinking(
        string shape = "jawOpen",
        float[]? times = null,
        float[]? weights = null
    ) =>
        new() {
            Name = "Blink",
            Duration = 1f,
            Channels = [
                new() {
                    Target = "Mid",
                    PositionTimes = [0f, 1f],
                    Positions = [Vector3.Zero, new(0f, 0f, 4f)]
                },
                new() {
                    Target = "Head",
                    Shape = shape,
                    WeightTimes = times ?? [0f, 1f],
                    Weights = weights ?? [0f, 1f]
                }
            ]
        };

    // --- The format ---------------------------------------------------------

    [Fact]
    public void AWeightTrackIsBakedAndIsFoundByName() {
        var clip = AnimationClip.Create(Blinking(), skeleton);

        Assert.Equal(1, clip.ShapeCount);
        Assert.Equal("jawOpen", clip.Shapes[0]);
        Assert.Equal(0, clip.IndexOfShape("jawOpen"));
        Assert.Equal(-1, clip.IndexOfShape("browRaise"));
    }

    /// <summary>
    ///     ⚠ A weight channel that names no joint is not an unresolved channel.
    /// </summary>
    /// <remarks>
    ///     The count is what somebody watches to notice a clip being played on the wrong rig, and a
    ///     morph channel names the <em>mesh's</em> node — which is not a joint and never will be. A
    ///     bake that counted them would report a head's worth of unresolved channels on every correct
    ///     import and make the number useless for the thing it exists for.
    /// </remarks>
    [Fact]
    public void AWeightChannelNamingNoJointIsNotCountedUnresolved() {
        var clip = AnimationClip.Create(Blinking(), skeleton);

        Assert.Equal(0, clip.UnresolvedChannels);
        Assert.Equal(1, clip.TrackCount);
    }

    /// <summary>And a channel that names no joint and carries no weight track still is.</summary>
    [Fact]
    public void ATransformChannelNamingNoJointIsStillCountedUnresolved() {
        var clip = AnimationClip.Create(
            new AnimationClipData {
                Name = "Fingers",
                Duration = 1f,
                Channels = [new() { Target = "Thumb", PositionTimes = [0f], Positions = [Vector3.Zero] }]
            },
            skeleton
        );

        Assert.Equal(1, clip.UnresolvedChannels);
    }

    /// <summary>
    ///     ⚠ A curve that is flat at zero is a curve, and it is not the same as no curve at all.
    /// </summary>
    /// <remarks>
    ///     The zero-value trap this format was designed around. A blend-shape weight of zero is a face
    ///     at rest — an authored value that every expression returns to — so the presence of a track
    ///     has to be a fact about the key array and never about the values in it. A format that said
    ///     "no keys" and "keys that are all zero" with the same bytes would silently drop the second
    ///     half of every expression.
    /// </remarks>
    [Fact]
    public void AFlatZeroTrackIsATrackAndAnEmptyOneIsNot() {
        var driven = AnimationClip.Create(Blinking(weights: [0f, 0f]), skeleton);

        Assert.Equal(1, driven.ShapeCount);
        Assert.True(driven.TrySampleWeight(0.5f, "jawOpen", out var held));
        Assert.Equal(0f, held);

        var silent = AnimationClip.Create(Blinking(times: [], weights: []), skeleton);

        Assert.Equal(0, silent.ShapeCount);
        Assert.False(silent.TrySampleWeight(0.5f, "jawOpen", out var absent));
        Assert.Equal(0f, absent);
    }

    // --- The sampler --------------------------------------------------------

    [Fact]
    public void AWeightIsInterpolatedLinearlyBetweenItsKeys() {
        var clip = AnimationClip.Create(Blinking(times: [0f, 1f], weights: [0f, 1f]), skeleton);

        Assert.True(clip.TrySampleWeight(0.25f, "jawOpen", out var quarter));
        Assert.Equal(0.25f, quarter);

        Assert.True(clip.TrySampleWeight(0.5f, "jawOpen", out var half));
        Assert.Equal(0.5f, half);
    }

    [Fact]
    public void AWeightIsHeldAtBothEndsOfTheClip() {
        var clip = AnimationClip.Create(Blinking(times: [0.25f, 0.75f], weights: [0.5f, 1f]), skeleton);

        Assert.True(clip.TrySampleWeight(-5f, "jawOpen", out var before));
        Assert.Equal(0.5f, before);

        Assert.True(clip.TrySampleWeight(50f, "jawOpen", out var after));
        Assert.Equal(1f, after);
    }

    /// <summary>⚠ Nothing clamps a weight, at either end.</summary>
    /// <remarks>
    ///     A corrective authored past one and a shape authored as the negative of its neighbour are
    ///     both things an exporter writes on purpose, and <c>BlendShapeWeights</c> and
    ///     <c>MorphKernel</c> both say the same. Saturating in the sampler would make the three
    ///     disagree, and the symptom would be an expression that stops moving at the top of its range.
    /// </remarks>
    [Fact]
    public void AWeightIsNeitherClampedNorSaturated() {
        var clip = AnimationClip.Create(Blinking(weights: [-1f, 2f]), skeleton);

        Assert.True(clip.TrySampleWeight(0f, "jawOpen", out var under));
        Assert.Equal(-1f, under);

        Assert.True(clip.TrySampleWeight(1f, "jawOpen", out var over));
        Assert.Equal(2f, over);
    }

    /// <summary>
    ///     A long track takes the bucket index the vector tracks take, and answers the same.
    /// </summary>
    /// <remarks>
    ///     Above <c>IndexThreshold</c> keys the search is replaced by a table lookup and an advance.
    ///     A ramp with a key every twentieth of a second is the case that exercises it: every sample
    ///     is a whole multiple of the step, so the two paths have to agree exactly rather than nearly.
    /// </remarks>
    [Fact]
    public void ALongWeightTrackIsIndexedAndStillExact() {
        var times = new float[21];
        var weights = new float[21];

        for (var key = 0; key < times.Length; key++) {
            times[key] = key / 20f;
            weights[key] = key / 20f;
        }

        var clip = AnimationClip.Create(Blinking(times: times, weights: weights), skeleton);

        for (var key = 0; key < times.Length; key++) {
            Assert.True(clip.TrySampleWeight(times[key], "jawOpen", out var sampled));
            Assert.Equal(weights[key], sampled);
        }
    }

    /// <summary>Sampling every shape at once agrees with sampling them one at a time.</summary>
    [Fact]
    public void SampleWeightsFillsOneSlotPerShapeInOrder() {
        var clip = AnimationClip.Create(
            new AnimationClipData {
                Name = "Face",
                Duration = 1f,
                Channels = [
                    new() { Target = "Head", Shape = "jawOpen", WeightTimes = [0f, 1f], Weights = [0f, 1f] },
                    new() { Target = "Head", Shape = "browRaise", WeightTimes = [0f, 1f], Weights = [1f, 0f] }
                ]
            },
            skeleton
        );

        Assert.Equal(["jawOpen", "browRaise"], clip.Shapes.ToArray());

        var sampled = new float[2];
        clip.SampleWeights(0.25f, sampled);

        Assert.Equal([0.25f, 0.75f], sampled);
    }

    [Fact]
    public void SampleWeightsRefusesADestinationTooSmallToHoldTheAnswer() {
        var clip = AnimationClip.Create(Blinking(), skeleton);

        Assert.Throws<ArgumentException>(() => clip.SampleWeights(0f, []));
    }

    // --- The buffer ---------------------------------------------------------

    /// <summary>⚠ Contributions add, which is what makes a blend continuous.</summary>
    [Fact]
    public void TwoClipsDrivingOneShapeAddTheirContributions() {
        var smile = AnimationClip.Create(Blinking(shape: "smile", weights: [1f, 1f]), skeleton);
        var buffer = new MorphWeightBuffer();

        buffer.Collect(smile, 0.5f, 0.25f);
        buffer.Collect(smile, 0.5f, 0.5f);

        Assert.True(buffer.TryGet("smile", out var blended));
        Assert.Equal(0.75f, blended);
        Assert.Equal(1, buffer.Count);
    }

    /// <summary>A contribution of zero registers the shape; a clip contributing nothing does not.</summary>
    /// <remarks>
    ///     The same distinction the format makes, one layer up. "This clip holds the jaw shut" has to
    ///     reach the component so it can overwrite whatever was there; "this clip has faded out" must
    ///     not, or a transition would wipe the face on its way through.
    /// </remarks>
    [Fact]
    public void MembershipAndValueAreSeparateFactsInTheBuffer() {
        var buffer = new MorphWeightBuffer();

        buffer.Add("jawOpen", 0f);

        Assert.True(buffer.TryGet("jawOpen", out var shut));
        Assert.Equal(0f, shut);

        var clip = AnimationClip.Create(Blinking(shape: "browRaise", weights: [1f, 1f]), skeleton);

        buffer.Collect(clip, 0.5f, 0f);

        Assert.False(buffer.TryGet("browRaise", out _));
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void ClearingTheBufferForgetsEveryShape() {
        var buffer = new MorphWeightBuffer();

        buffer.Add("jawOpen", 0.5f);
        buffer.Clear();

        Assert.Equal(0, buffer.Count);
        Assert.False(buffer.TryGet("jawOpen", out _));
    }

    // --- The bridge ---------------------------------------------------------

    /// <summary>An animator playing a clip fills its own buffer as the tree is evaluated.</summary>
    /// <remarks>
    ///     ⚠ <b>The link that makes the rest of the chain more than a sampler nobody samples.</b> The
    ///     weights are collected inside <c>ClipMotion.Evaluate</c>, which is where the clip's blend
    ///     weight is known — so this asserts against a real <see cref="Animator" /> stepping a real
    ///     state machine rather than against the buffer being filled by hand.
    /// </remarks>
    [Fact]
    public void AnAnimatorPlayingAClipCollectsItsWeights() {
        var animator = Playing(Blinking(weights: [0f, 1f]));

        animator.Update(0.5f);

        Assert.True(animator.MorphWeights.TryGet("jawOpen", out var half));
        Assert.Equal(0.5f, half);
    }

    /// <summary>
    ///     ⚠ And it collects at the clip's own time, not at the fraction of it playback is through.
    /// </summary>
    /// <remarks>
    ///     <b>A one-second clip cannot tell the two apart, which is why this one is four.</b> The
    ///     constraint tags beside these are collected at the <em>normalised</em> time, because a tag's
    ///     span is authored as a fraction of the cycle; a weight key is at a second, like every other
    ///     key in the clip. Passing the fraction would read the whole curve inside the clip's first
    ///     second and every longer clip would play its face four times too fast and then hold — which
    ///     reads as an exporter problem rather than as a unit mix-up one line long.
    /// </remarks>
    [Fact]
    public void AnAnimatorCollectsAtTheClipsOwnTimeAndNotAtItsFraction() {
        var clip = Blinking(times: [0f, 4f], weights: [0f, 1f]) with { Duration = 4f };
        var animator = Playing(clip);

        // A quarter of the way through a four-second clip is one second in, and the ramp is at 0.25.
        // Sampling at the fraction would land at 0.25 s and read 0.0625.
        animator.Update(1f);

        Assert.True(animator.MorphWeights.TryGet("jawOpen", out var quarter));
        Assert.Equal(0.25f, quarter);
    }

    /// <summary>And it forgets them the moment the clip stops driving them.</summary>
    [Fact]
    public void AnAnimatorsWeightsAreClearedEveryUpdate() {
        var animator = Playing(Blinking(weights: [0f, 1f]));

        animator.Update(0.5f);
        Assert.Equal(1, animator.MorphWeights.Count);

        animator.Layers[0].Enabled = false;
        animator.Update(0.1f);

        Assert.Equal(0, animator.MorphWeights.Count);
    }

    /// <summary>The buffer lands on the component's slots by name.</summary>
    [Fact]
    public void ApplyWritesTheNamedSlotsAndGrowsTheArrayToFit() {
        var buffer = new MorphWeightBuffer();
        buffer.Add("browRaise", 0.25f);

        var component = new BlendShapeWeights();
        var written = BlendShapeAnimationSystem.Apply(buffer, ["jawOpen", "browRaise"], ref component);

        Assert.Equal(1, written);
        Assert.Equal<float>([0f, 0.25f], component.Weights!);
    }

    /// <summary>⚠ A slot no clip named keeps whatever a script put there.</summary>
    /// <remarks>
    ///     The difference between "the animation says nothing about your jaw" and "the animation wants
    ///     your jaw shut". Writing every slot would make playing a wave animation wipe an expression
    ///     an inspector or a gameplay script had set, which is a bug nobody would attribute to the
    ///     clip.
    /// </remarks>
    [Fact]
    public void ApplyLeavesASlotNoClipNamedAlone() {
        var buffer = new MorphWeightBuffer();
        buffer.Add("browRaise", 0.25f);

        var component = new BlendShapeWeights { Weights = [0.75f, 0f] };
        BlendShapeAnimationSystem.Apply(buffer, ["jawOpen", "browRaise"], ref component);

        Assert.Equal<float>([0.75f, 0.25f], component.Weights!);
    }

    /// <summary>A shape the mesh does not have is left out of the count rather than thrown over.</summary>
    /// <remarks>
    ///     The difference is what <c>Unbound</c> reports: a clip authored on a face with more shapes
    ///     than this mesh has is an ordinary thing to play, and a non-zero count is how somebody
    ///     notices they are playing a head's clip on a body.
    /// </remarks>
    [Fact]
    public void ApplyCountsOnlyTheSlotsItWrote() {
        var buffer = new MorphWeightBuffer();
        buffer.Add("browRaise", 0.25f);
        buffer.Add("tongueOut", 1f);

        var component = new BlendShapeWeights();

        var written = BlendShapeAnimationSystem.Apply(buffer, ["browRaise"], ref component);

        Assert.Equal(1, written);
        Assert.Equal(1, buffer.Count - written);
    }

    /// <summary>
    ///     The whole chain, in a world: a clip's weight track reaches the component the renderer reads.
    /// </summary>
    [Fact]
    public void TheSystemCarriesAClipsWeightOntoTheComponent() {
        using var world = new World(nameof(TheSystemCarriesAClipsWeightOntoTheComponent));

        var entity = world.Create();
        var animator = Playing(Blinking(weights: [0f, 1f]));

        world.Add(entity, new AnimatorComponent { Value = animator });
        world.Add(entity, new BlendShapeWeights { Shapes = ["jawOpen", "browRaise"] });

        animator.Update(0.5f);

        var system = new BlendShapeAnimationSystem();
        system.Run(world);

        Assert.Equal(1, system.Driven);
        Assert.Equal(0, system.Unbound);
        Assert.Equal<float>([0.5f, 0f], world.Read<BlendShapeWeights>(entity).Weights!);
    }

    /// <summary>
    ///     ⚠ A clip playing on a mesh that has none of its shapes writes nothing, and says so.
    /// </summary>
    /// <remarks>
    ///     A head's clip on a body. Nothing throws, because a rig with fewer shapes than a clip names
    ///     is an ordinary thing to play — but <c>Driven</c> must not count it, or the counter that
    ///     would tell somebody would be the one hiding it.
    /// </remarks>
    [Fact]
    public void AClipWhoseShapesTheMeshHasNoneOfIsReportedRatherThanWritten() {
        using var world = new World(nameof(AClipWhoseShapesTheMeshHasNoneOfIsReportedRatherThanWritten));

        var entity = world.Create();
        var animator = Playing(Blinking(shape: "tongueOut", weights: [0f, 1f]));

        world.Add(entity, new AnimatorComponent { Value = animator });
        world.Add(entity, new BlendShapeWeights { Shapes = ["jawOpen"], Weights = [0.75f] });

        animator.Update(0.5f);

        var system = new BlendShapeAnimationSystem();
        system.Run(world);

        Assert.Equal(0, system.Driven);
        Assert.Equal(1, system.Unbound);
        Assert.Equal<float>([0.75f], world.Read<BlendShapeWeights>(entity).Weights!);
    }

    /// <summary>An entity the renderer has not bound yet is skipped rather than guessed at.</summary>
    /// <remarks>
    ///     The binding is published out of what the render feature actually attached, so it is absent
    ///     for the frame an entity appears on. Writing slot zero on the guess that it is the first
    ///     shape the clip mentions would move whichever shape the mesh happens to list first.
    /// </remarks>
    [Fact]
    public void AnEntityWithNoBindingIsLeftAlone() {
        using var world = new World(nameof(AnEntityWithNoBindingIsLeftAlone));

        var entity = world.Create();
        var animator = Playing(Blinking(weights: [0f, 1f]));

        world.Add(entity, new AnimatorComponent { Value = animator });
        world.Add(entity, new BlendShapeWeights());

        animator.Update(0.5f);

        var system = new BlendShapeAnimationSystem();
        system.Run(world);

        Assert.Equal(0, system.Driven);
        Assert.Null(world.Read<BlendShapeWeights>(entity).Weights);
    }

    static Animator Playing(AnimationClipData data) {
        var animator = new Animator(TestRigs.Chain());
        var clip = new ClipMotion(AnimationClip.Create(data, animator.Skeleton));

        animator.AddLayer("Base", new AnimationStateMachine([new AnimationState("Play", clip)]));

        return animator;
    }
}
