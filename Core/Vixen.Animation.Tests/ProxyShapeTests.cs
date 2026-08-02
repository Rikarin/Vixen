// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Animation.Tests;

/// <summary>
///     Proxy shapes, surface coordinates and adapted sockets — the half of the document that makes one
///     authored contact work on a body it was not authored on.
/// </summary>
public class ProxyShapeTests {
    const float Step = 1f / 60f;

    /// <summary>Every primitive, with dimensions that are not square so an axis mix-up shows.</summary>
    public static TheoryData<ShapeKind> Kinds => [
        ShapeKind.Box,
        ShapeKind.TaperedBox,
        ShapeKind.Sphere,
        ShapeKind.Capsule,
        ShapeKind.TaperedCapsule,
        ShapeKind.Cylinder,
        ShapeKind.Cone
    ];

    static ShapeParams Dimensions(ShapeKind kind) =>
        kind switch {
            ShapeKind.Box => ShapeParams.Box(new(0.3f, 0.5f, 0.2f)),
            ShapeKind.TaperedBox => ShapeParams.TaperedBox(new(0.3f, 0.5f, 0.2f), new(0.15f, 0f, 0.1f)),
            ShapeKind.Sphere => ShapeParams.Sphere(0.4f),
            ShapeKind.Capsule => ShapeParams.Capsule(0.2f, 0.5f),
            ShapeKind.TaperedCapsule => ShapeParams.TaperedCapsule(0.25f, 0.12f, 0.5f),
            ShapeKind.Cylinder => ShapeParams.Cylinder(0.3f, 0.45f),
            _ => ShapeParams.Cone(0.35f, 0.5f)
        };

    // ---------------------------------------------------------------- geometry

    [Theory]
    [MemberData(nameof(Kinds))]
    public void AnAuthoredPointComesBackToItselfThroughTheBake(ShapeKind kind) {
        var dimensions = Dimensions(kind);

        // Points inside, outside and right on the skin. The last is the one an author actually
        // places; the other two are what a hand-typed number and a physics answer look like.
        foreach (var point in new Vector3[] {
            new(0.5f, 0.3f, -0.2f),
            new(0.05f, -0.4f, 0.03f),
            new(-0.9f, 0.9f, 0.4f),
            new(0f, 0.6f, 0f),
            new(0.31f, 0f, 0f)
        }) {
            var coordinate = ShapeGeometry.Project(kind, dimensions, point, out var residual);
            var sample = ShapeGeometry.Evaluate(kind, dimensions, coordinate);
            var back = sample.Position + Quaternion.Transform(residual, sample.Rotation());

            // ⚠ Not "the projection is the closest point" — it is not always, and the doc comment says
            // so. What has to hold is that the pair is lossless, because a bake that quietly moved a
            // contact would be far worse than one that approximates a surface.
            TestRigs.Near(point, back, $"{kind} at {point}");
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public void ACoordinateOnTheSurfaceStaysOnTheSurfaceWhenTheShapeIsResized(ShapeKind kind) {
        var small = Dimensions(kind);
        var large = small.Scaled(new(1.8f, 1.4f, 1.8f));
        var coordinate = ShapeGeometry.Project(kind, small, new(0.5f, 0.2f, -0.15f), out _);

        var here = ShapeGeometry.Evaluate(kind, small, coordinate);
        var there = ShapeGeometry.Evaluate(kind, large, coordinate);

        // The point moved — that is the whole point — and it is still the same place on the shape.
        Assert.True((here.Position - there.Position).Length() > 1e-3f, $"{kind} did not move when resized");

        var again = ShapeGeometry.Project(kind, large, there.Position, out var residual);

        Assert.Equal(coordinate.Face, again.Face);
        Assert.Equal(coordinate.U, again.U, 1e-3f);
        Assert.Equal(coordinate.V, again.V, 1e-3f);
        Assert.True(residual.Length() < 1e-3f, $"{kind} resolved off its own surface by {residual.Length()}");
    }

    [Fact]
    public void ASurfaceNormalPointsOutOfTheShapeAndItsFrameIsRightHanded() {
        foreach (var kind in new[] { ShapeKind.Box, ShapeKind.Sphere, ShapeKind.Capsule, ShapeKind.Cylinder }) {
            var dimensions = Dimensions(kind);
            var coordinate = ShapeGeometry.Project(kind, dimensions, new(1f, 0.2f, 0.4f), out _);
            var sample = ShapeGeometry.Evaluate(kind, dimensions, coordinate);

            Assert.True(
                Vector3.Dot(sample.Normal, sample.Position) > 0f,
                $"{kind}'s normal at {coordinate} faces inwards"
            );

            // ⚠ A left-handed basis decomposes to a rotation with a negative scale, silently, and
            // shows up much later as a mirrored contact.
            var rotation = sample.Rotation();
            var x = Quaternion.Transform(Vector3.UnitX, rotation);
            var y = Quaternion.Transform(Vector3.UnitY, rotation);
            var z = Quaternion.Transform(Vector3.UnitZ, rotation);

            Assert.True(Vector3.Dot(Vector3.Cross(x, y), z) > 0.99f, $"{kind}'s surface frame is mirrored");
            TestRigs.Near(Vector3.Normalize(sample.Normal), y, $"{kind}'s frame should have +Y outward");
        }
    }

    // ---------------------------------------------------------------- the claim

    [Fact]
    public void OneAuthoredContactLandsOnTheSameSpotOnThreeBodiesOfDifferentProportions() {
        // Authored once, on the middle body, and never touched again.
        var authored = new SurfacePoint(-1, 0.15f, 0.62f);
        var coordinate = SurfaceCoordinate.On("belly", authored).Offset(new(0f, 0.01f, 0f));

        List<Vector3> landed = [];

        foreach (var scale in Proportions) {
            var body = Body(scale);
            var stack = Stack(body);

            stack.Add(
                new PositionGoal {
                    Effector = body.Wrist,
                    Chain = new(body.Shoulder, body.Wrist),
                    Goal = new SurfaceFrame(coordinate),
                    EaseIn = 0f
                }
            );

            var hand = Settle(stack, body);

            landed.Add(hand);

            // Where did the hand land, expressed on the body it landed on? The same place, every time.
            Assert.True(
                stack.Shapes!.TryPose(Symbol.Intern("belly"), body.Model, out var belly),
                "the belly should have been posed"
            );

            var where = ShapeGeometry.Project(
                belly.Shape.Kind,
                belly.Dimensions,
                belly.ToShape(hand),
                out var gap
            );

            Assert.Equal(authored.U, where.U, 2e-2f);
            Assert.Equal(authored.V, where.V, 2e-2f);

            // ⚠ And the authored one-centimetre gap is still a centimetre, not a centimetre times
            // however much bigger this body is.
            Assert.Equal(0.01f, gap.Y, 2e-3f);
        }

        // If the three had landed in the same world place, the whole exercise would prove nothing:
        // the point is that the target moved with the body and the hand followed it.
        Assert.True(
            (landed[0] - landed[2]).Length() > 0.05f,
            $"the three bodies should want the hand in different places, got {landed[0]} and {landed[2]}"
        );
    }

    [Fact]
    public void OnePropIsGrippedCorrectlyByThreeHandsWithNoPerHandOffset() {
        // One authored grip: the middle of the palm's outward face. No hand is mentioned by size
        // anywhere below.
        var contact = SurfaceCoordinate.On("right-palm", new SurfacePoint(2, 0.5f, 0.5f));

        List<float> reach = [];

        foreach (var scale in Proportions) {
            var body = Body(scale);
            var stack = Stack(body);

            var socket = stack.Sockets.Add(
                new AttachmentSocket {
                    Name = Symbol.Intern("right-hand-grip"),
                    Joint = body.Wrist,
                    Offset = new(new Vector3(0f, -0.05f, 0f), Quaternion.Identity, Vector3.One),
                    Contact = contact
                }
            );

            Settle(stack, body);

            Assert.True(socket.IsAdapted, "the socket should have found the palm");
            Assert.True(stack.Shapes!.TryPose(Symbol.Intern("right-palm"), body.Model, out var palm));

            var where = ShapeGeometry.Project(
                palm.Shape.Kind,
                palm.Dimensions,
                palm.ToShape(socket.Solved.Translation),
                out var gap
            );

            Assert.Equal(2, where.Face);
            Assert.Equal(0.5f, where.U, 2e-2f);
            Assert.Equal(0.5f, where.V, 2e-2f);
            Assert.True(gap.Length() < 1e-3f, $"the grip sat {gap.Length():0.####} m off the palm");

            reach.Add((socket.Solved.Translation - body.Model[body.Wrist].Translation).Length());
        }

        // ⚠ The offset from the bone to the socket is what moved, and it had to: the authored one is a
        // property of the hand it was authored on. Preserving it is what drives a pistol into a bigger
        // palm; preserving the grip contact is what a person would do.
        Assert.True(
            reach[2] > reach[0] * 1.2f,
            $"a bigger hand should hold the prop further out: {reach[0]:0.####} vs {reach[2]:0.####}"
        );
    }

    [Theory]
    [InlineData(8)]
    [InlineData(120)]
    public void PosingCostFollowsTheGoalsAndNotTheShapeCount(int shapes) {
        var body = Body(Vector3.One, filler: shapes);
        var stack = Stack(body);

        Assert.Equal(shapes + 2, stack.Shapes!.Set.Count);

        stack.Add(
            new PositionGoal {
                Effector = body.Wrist,
                Chain = new(body.Shoulder, body.Wrist),
                Goal = new SurfaceFrame(SurfaceCoordinate.On("belly", SurfacePoint.Side)),
                EaseIn = 0f
            }
        );

        stack.Add(
            new OrientationGoal {
                Effector = body.Wrist,
                Chain = new(body.Shoulder, body.Wrist),
                Goal = new SurfaceFrame(SurfaceCoordinate.On("right-palm", new SurfacePoint(2, 0.5f, 0.5f))),
                EaseIn = 0f
            }
        );

        Settle(stack, body, frames: 4);

        // ⚠ Two, on a body carrying a hundred and twenty. This is the second of the three reasons
        // proxy shapes are not physics colliders, and it is invisible in a screenshot — a regression
        // here shows up as a frame budget nobody can account for.
        Assert.Equal(2, stack.Shapes.PosedLastFrame);
    }

    // ---------------------------------------------------------------- the other forms

    [Fact]
    public void AnAxisCoordinateTracksTheShapesProportionsRatherThanAPatchOfIt() {
        var slim = Body(new(1f, 1f, 1f));
        var wide = Body(new(2f, 1f, 1f));

        var coordinate = SurfaceCoordinate.Along("belly", Vector3.UnitX);

        var here = Resolve(slim, coordinate);
        var there = Resolve(wide, coordinate);

        // The belly got twice as wide, so the point on its side went twice as far out — which is what
        // "track the proportions" means and what a fixed surface patch would not do.
        var slimReach = here.Origin - Centre(slim, "belly");
        var wideReach = there.Origin - Centre(wide, "belly");

        Assert.Equal(2f, wideReach.X / slimReach.X, 0.05f);
    }

    [Fact]
    public void ALimbCoordinateNeedsNoShapeAndStaysHalfwayDownAnArmOfAnyLength() {
        foreach (var scale in Proportions) {
            var body = Body(scale);
            var frame = Resolve(body, SurfaceCoordinate.OnLimb(body.Elbow, body.Wrist, 0.5f));

            var elbow = body.Model[body.Elbow].Translation;
            var wrist = body.Model[body.Wrist].Translation;

            TestRigs.Near(Vector3.Lerp(elbow, wrist, 0.5f), frame.Origin, $"at {scale}");
        }
    }

    [Fact]
    public void SeparatedSourcesTakeTheOriginFromTheSurfaceAndTheScaleFromTheWorld() {
        var body = Body(new(2f, 2f, 2f));

        var shaped = Resolve(body, SurfaceCoordinate.On("belly", SurfacePoint.Side));

        var separated = Resolve(
            body,
            SurfaceCoordinate.On("belly", SurfacePoint.Side).From(OrientationSource.Model, ScaleSource.Model)
        );

        TestRigs.Near(shaped.Origin, separated.Origin, "the origin comes from the same surface");
        TestRigs.Near(Vector3.One, separated.Scale, "and the scale from the world");

        // The shape's own answer is its half-extents, so an offset of one in that frame is one radius
        // — which is what "the offset stretches with the body" means, and what naming the world
        // instead is for.
        Assert.True(
            MathF.Abs(shaped.Scale.X - 1f) > 0.1f,
            $"the shape should have said something other than one, said {shaped.Scale}"
        );
        TestRigs.Near(Quaternion.Identity, separated.Rotation);
    }

    [Fact]
    public void AShapeMayBeNamedByWhatItAffordsRatherThanByItsName() {
        var body = Body(Vector3.One);

        var byName = Resolve(body, SurfaceCoordinate.On("belly", SurfacePoint.Side));
        var byTag = Resolve(body, SurfaceCoordinate.Affording("affords", "lean-on", SurfacePoint.Side));

        TestRigs.Near(byName.Origin, byTag.Origin);
    }

    [Fact]
    public void ASurfaceFrameOnABodyWithNoShapesFailsRatherThanThrowing() {
        var body = Body(Vector3.One);
        var stack = new ConstraintStack(body.Skeleton);

        var handle = stack.Add(
            new PositionGoal {
                Effector = body.Wrist,
                Chain = new(body.Shoulder, body.Wrist),
                Goal = new SurfaceFrame(SurfaceCoordinate.On("belly", SurfacePoint.Side))
            }
        );

        var before = TestRigs.ModelPositions(body.Pose)[body.Wrist];

        Settle(stack, body);

        TestRigs.Near(before, TestRigs.ModelPositions(body.Pose)[body.Wrist]);
        Assert.False(handle.Residual.Ran);
    }

    // ---------------------------------------------------------------- the poser seam

    [Fact]
    public void APoserMaySizeAShapeFromSomethingOtherThanItsJoint() {
        var body = Body(Vector3.One);
        var poser = new SwellingTestPoser(2.5f);
        var shapes = new ProxyShapes(body.Shapes, poser);

        Assert.True(shapes.TryPose(Symbol.Intern("belly"), body.Model, out var swollen));
        Assert.Equal(body.Shapes[body.Shapes.IndexOf("belly")].Dimensions.Radius * 2.5f, swollen.Dimensions.Radius, 1e-4f);

        var plain = new ProxyShapes(body.Shapes);

        Assert.True(plain.TryPose(Symbol.Intern("belly"), body.Model, out var normal));
        Assert.True(swollen.Dimensions.Radius > normal.Dimensions.Radius);
    }

    // ---------------------------------------------------------------- the vocabulary

    [Fact]
    public void AShapeTheVocabularyDoesNotDeclareIsReportedAndTheSetIsNamed() {
        var vocabulary = Vocabulary();

        var set = ProxyShapeSet.Of(
            "Stranger",
            "humanoid",
            new ProxyShape { Name = Symbol.Intern("belly"), Kind = ShapeKind.Sphere, Joint = 0 },
            new ProxyShape { Name = Symbol.Intern("gizzard"), Kind = ShapeKind.Sphere, Joint = 0 }
        );

        List<ShapeValidation> findings = [];

        Assert.False(vocabulary.Validate(set, findings));

        var finding = Assert.Single(findings);

        Assert.Equal(Symbol.Intern("gizzard"), finding.Shape);
        Assert.Contains("Stranger", finding.Message, StringComparison.Ordinal);
        Assert.Contains("gizzard", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AClassSaysAHumanoidHasAPalmAndASetWithoutOneIsRefused() {
        var vocabulary = Vocabulary();

        var set = ProxyShapeSet.Of(
            "Torso only",
            "humanoid",
            new ProxyShape { Name = Symbol.Intern("belly"), Kind = ShapeKind.Sphere, Joint = 0 }
        );

        List<ShapeValidation> findings = [];

        Assert.False(vocabulary.Validate(set, findings, Symbol.Intern("humanoid")));
        Assert.Contains(findings, finding => finding.Shape == Symbol.Intern("right-palm"));

        // ⚠ The distinction the class exists for. A name says "if you have a belly, call it belly";
        // a class says "a humanoid *has* a belly", which is what a clip authored on one member needs
        // in order to be portable to every other.
        Assert.True(vocabulary.Validate(set, findings = []), "the names on their own are all declared");
        Assert.Empty(findings);
    }

    [Fact]
    public void AShapeDeclaredASphereAndBuiltAsABoxIsRefused() {
        var vocabulary = Vocabulary();

        var set = ProxyShapeSet.Of(
            "Boxy",
            "humanoid",
            new ProxyShape { Name = Symbol.Intern("belly"), Kind = ShapeKind.Box, Joint = 0 },
            new ProxyShape {
                Name = Symbol.Intern("right-palm"),
                Kind = ShapeKind.Box,
                Joint = 0,
                Tags = FacetSet.Of(("affords", "grip-surface"))
            }
        );

        List<ShapeValidation> findings = [];

        Assert.False(vocabulary.Validate(set, findings, Symbol.Intern("humanoid")));

        var finding = Assert.Single(findings);

        Assert.Contains("parameterisation", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoShapesWithOneNameAreRefusedAtBuildTime() {
        var thrown = Assert.Throws<InvalidOperationException>(
            () => ProxyShapeSet.Of(
                "Doubled",
                null,
                new ProxyShape { Name = Symbol.Intern("left-palm"), Kind = ShapeKind.Box, Joint = 0 },
                new ProxyShape { Name = Symbol.Intern("left-palm"), Kind = ShapeKind.Box, Joint = 1 }
            )
        );

        Assert.Contains("Doubled", thrown.Message, StringComparison.Ordinal);
    }

    static ShapeVocabulary Vocabulary() =>
        new(
            "humanoid",
            [
                new(Symbol.Intern("belly"), "The front of the torso, between the ribs and the hips."),
                new(Symbol.Intern("right-palm"), "The gripping face of the right hand.")
            ],
            [new(Facet.Of("affords", "grip-surface"), "A hand may close on it."), new(Facet.Of("affords", "lean-on"), "A body may rest against it.")],
            [
                new ShapeClass(
                    Symbol.Intern("humanoid"),
                    [
                        new(Symbol.Intern("belly"), ShapeKind.Sphere, FacetSet.Empty, ShapeParams.Sphere(0.2f), true),
                        new(
                            Symbol.Intern("right-palm"),
                            ShapeKind.Box,
                            FacetSet.Of(("affords", "grip-surface")),
                            ShapeParams.Box(new(0.04f, 0.02f, 0.08f)),
                            true
                        )
                    ]
                )
            ]
        );

    // ---------------------------------------------------------------- the coarse set

    [Fact]
    public void TheCoarseSetIsOneBoxPerTagGroupAndAnOverrideSurvivesIt() {
        var body = Body(Vector3.One, ribs: 3);
        var coarse = ProxyShapes.Coarsen(body.Shapes, body.Skeleton, Symbol.Intern("region"));

        // Three ribs became one box; the palm declared itself coarse and did not.
        Assert.Equal(-1, coarse.IndexOf("rib-0"));
        Assert.True(coarse.IndexOf("coarse-torso") >= 0, "the tagged group should have merged");
        Assert.True(coarse.IndexOf("right-palm") >= 0, "a shape that declares itself coarse survives");

        var merged = coarse[coarse.IndexOf("coarse-torso")];

        Assert.Equal(ShapeKind.Box, merged.Kind);

        // And it actually encloses what it replaced.
        var full = new ProxyShapes(body.Shapes);
        var box = new ProxyShapes(coarse);

        for (var rib = 0; rib < 3; rib++) {
            Assert.True(full.TryPose(Symbol.Intern($"rib-{rib}"), body.Model, out var posed));
            Assert.True(box.TryPose(merged.Name, body.Model, out var enclosing));

            var local = enclosing.ToShape(posed.Transform.Translation);
            var extents = enclosing.Dimensions.Extents;

            Assert.True(
                MathF.Abs(local.X) <= extents.X + 1e-3f
                && MathF.Abs(local.Y) <= extents.Y + 1e-3f
                && MathF.Abs(local.Z) <= extents.Z + 1e-3f,
                $"rib {rib} at {local} is outside the coarse box {extents}"
            );
        }
    }

    [Fact]
    public void DroppingDetailSwapsWhichSetAnswers() {
        var body = Body(Vector3.One, ribs: 3);
        var shapes = new ProxyShapes(body.Shapes) {
            Coarse = ProxyShapes.Coarsen(body.Shapes, body.Skeleton, Symbol.Intern("region"))
        };

        Assert.True(shapes.TryPose(Symbol.Intern("rib-0"), body.Model, out _));

        shapes.Detail = 1;
        shapes.Frame();

        Assert.False(shapes.TryPose(Symbol.Intern("rib-0"), body.Model, out _), "the ribs are gone at range");
        Assert.True(shapes.TryPose(Symbol.Intern("right-palm"), body.Model, out _), "and the grip is not");
    }

    // ---------------------------------------------------------------- the rig

    static Vector3[] Proportions => [new(0.7f, 0.85f, 0.7f), Vector3.One, new(1.5f, 1.25f, 1.4f)];

    /// <summary>A torso and one arm, at whatever proportions, with the shapes to match.</summary>
    static TestBody Body(Vector3 scale, int filler = 0, int ribs = 0) => new(scale, filler, ribs);

    static ConstraintStack Stack(TestBody body) =>
        new(body.Skeleton) { Shapes = new(body.Shapes) };

    static Vector3 Settle(ConstraintStack stack, TestBody body, int frames = 60) {
        for (var frame = 0; frame < frames; frame++) {
            body.Pose.ResetToBindPose();
            stack.Solve(body.Pose.Bones, body.Model, Step);
        }

        return TestRigs.ModelPositions(body.Pose)[body.Wrist];
    }

    static Frame Resolve(TestBody body, SurfaceCoordinate coordinate) {
        var stack = Stack(body);

        stack.Add(new PositionGoal { Effector = body.Wrist, Goal = new SurfaceFrame(coordinate) });
        stack.Solve(body.Pose.Bones, body.Model, Step);

        Assert.True(
            new SurfaceFrame(coordinate).TryResolve(
                new() {
                    Skeleton = body.Skeleton,
                    Model = body.Model,
                    Bindings = stack.Bindings,
                    Shapes = stack.Shapes
                },
                out var frame
            )
        );

        return frame;
    }

    static Vector3 Centre(TestBody body, string shape) {
        var shapes = new ProxyShapes(body.Shapes);

        Assert.True(shapes.TryPose(Symbol.Intern(shape), body.Model, out var posed));

        return posed.Transform.Translation;
    }

    /// <summary>One body: a skeleton, its proxy shapes, and somewhere to pose it.</summary>
    sealed class TestBody {
        public TestBody(Vector3 scale, int filler, int ribs) {
            Skeleton = Skeleton.Create(
                TestRigs.Build(
                    "Body",
                    ("Root", -1, Vector3.Zero),
                    ("Spine", 0, new Vector3(0f, 0.9f, 0f) * scale),
                    ("Shoulder", 1, new Vector3(0.2f, 0.35f, 0f) * scale),
                    ("Elbow", 2, new Vector3(0f, -0.32f, 0f) * scale),
                    ("Wrist", 3, new Vector3(0f, -0.3f, 0f) * scale)
                )
            );

            Shoulder = Skeleton.IndexOf("Shoulder");
            Elbow = Skeleton.IndexOf("Elbow");
            Wrist = Skeleton.IndexOf("Wrist");

            List<ProxyShape> shapes = [
                new() {
                    Name = Symbol.Intern("belly"),
                    Kind = ShapeKind.Sphere,
                    Joint = Skeleton.IndexOf("Spine"),
                    Offset = new(new Vector3(0f, 0.1f, 0.05f) * scale, Quaternion.Identity, Vector3.One),
                    Dimensions = ShapeParams.Sphere(0.22f).Scaled(scale),
                    Tags = FacetSet.Of(("affords", "lean-on"), ("region", "torso"))
                },
                new() {
                    Name = Symbol.Intern("right-palm"),
                    Kind = ShapeKind.Box,
                    Joint = Wrist,
                    Offset = new(new Vector3(0f, -0.05f, 0f) * scale, Quaternion.Identity, Vector3.One),
                    Dimensions = ShapeParams.Box(new Vector3(0.04f, 0.02f, 0.08f) * scale),
                    Tags = FacetSet.Of(("affords", "grip-surface")),
                    Coarse = true
                }
            ];

            for (var rib = 0; rib < ribs; rib++) {
                shapes.Add(
                    new() {
                        Name = Symbol.Intern($"rib-{rib}"),
                        Kind = ShapeKind.Capsule,
                        Joint = Skeleton.IndexOf("Spine"),
                        Offset = new(new Vector3(0f, 0.05f * rib, 0.1f), Quaternion.Identity, Vector3.One),
                        Dimensions = ShapeParams.Capsule(0.03f, 0.12f),
                        Tags = FacetSet.Of(("region", "torso"))
                    }
                );
            }

            for (var extra = 0; extra < filler; extra++) {
                shapes.Add(
                    new() {
                        Name = Symbol.Intern($"filler-{extra}"),
                        Kind = ShapeKind.Capsule,
                        Joint = extra % Skeleton.JointCount,
                        Dimensions = ShapeParams.Capsule(0.02f, 0.05f)
                    }
                );
            }

            Shapes = ProxyShapeSet.Of("Body", "humanoid", [.. shapes]);
            Pose = new(Skeleton);
            Model = new BoneTransform[Skeleton.JointCount];

            Pose.ComputeModelSpace(Model);
        }

        public Skeleton Skeleton { get; }

        public ProxyShapeSet Shapes { get; }

        public SkeletonPose Pose { get; }

        public BoneTransform[] Model { get; }

        public int Shoulder { get; }

        public int Elbow { get; }

        public int Wrist { get; }
    }
}

/// <summary>A poser that inflates whatever it places.</summary>
/// <remarks>
///     The second implementation of <see cref="IProxyShapePoser" />, and the case the seam exists for:
///     a shape whose size comes from a morph weight, a corrective or a simulation rather than from the
///     joint hierarchy.
/// </remarks>
sealed class SwellingTestPoser(float swell) : IProxyShapePoser {
    public bool TryPose(ProxyShape shape, ReadOnlySpan<BoneTransform> model, out ProxyShapePose posed) {
        if (!JointProxyShapePoser.Shared.TryPose(shape, model, out posed)) {
            return false;
        }

        posed = posed with { Dimensions = posed.Dimensions.Scaled(new Vector3(swell)) };
        return true;
    }
}
