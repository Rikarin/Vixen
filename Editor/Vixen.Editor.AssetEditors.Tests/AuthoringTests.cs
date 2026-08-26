// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation;
using Vixen.Animation.Constraints;
using Vixen.Audio.Assets;
using Vixen.Core;
using Vixen.Core.Curves;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Audio;
using Vixen.Editor.AssetEditors.Input;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.AssetEditors.Vfx;
using Vixen.Editor.Assets.Animation;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Input;
using Vixen.Rendering;
using Xunit;

namespace Vixen.Editor.AssetEditors.Tests;

/// <summary>Doc 20's E5: the six documents this assembly adds, as documents rather than as panels.</summary>
/// <remarks>
///     <para>
///         <b>What is under test is the half a screenshot cannot check.</b> Each of these editors is
///         a format, an undo stack and a translation into something the runtime runs; the panel over
///         it is <c>Vixen.Editor.App.Tests</c>' and the golden suite's. So what is asserted here is
///         that a file round-trips, that an edit is one undo entry, and that the translation is the
///         one the runtime would have made.
///     </para>
///     <para>
///         Real files, for <see cref="EditorFixture" />'s reason: everything worth knowing about a
///         document that writes an asset is about what ends up in the file.
///     </para>
/// </remarks>
public class AuthoringTests {
    // ── The VFX graph ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANewEffectCompilesAndSimulates() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Sparks.vxvfx", string.Empty);
        var document = new VfxDocument(fixture.Project, AssetId.Empty, path);

        var artefact = document.Compile();

        Assert.NotNull(artefact);
        Assert.Empty(document.Diagnostics);
        Assert.NotNull(document.Preview);

        // The preview is the runtime's own simulation over the artefact this compile produced, so
        // stepping it is the same arithmetic a game would run.
        document.Preview!.Step(0.1f);
        document.Preview.Step(0.1f);

        Assert.True(document.Preview.Count > 0);
    }

    [Fact]
    public void AnEffectEmitsTheShaderFromTheSameCompile() {
        using var fixture = new EditorFixture();

        var document = new VfxDocument(fixture.Project, AssetId.Empty, fixture.Write("Assets/Smoke.vxvfx", string.Empty));

        Assert.NotNull(document.Compile());

        var source = document.ShaderSource();

        Assert.NotNull(source);
        Assert.Contains("Smoke", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEffectRoundTripsThroughItsFile() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Fire.vxvfx", string.Empty);
        var document = new VfxDocument(fixture.Project, AssetId.Empty, path);

        var nodes = document.Graph.Nodes.Count;

        document.Save();

        var reopened = new VfxDocument(fixture.Project, AssetId.Empty, path);

        Assert.Empty(reopened.LoadDiagnostics);
        Assert.Equal(nodes, reopened.Graph.Nodes.Count);
    }

    // ── The clip editor ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AClipKeysUndoablyAndBakesToChannels() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Door.vxanim", string.Empty);
        var document = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);

        Assert.True(document.AddTarget("Door"));

        var target = document.Target("Door")!;

        document.AddKey(target, AnimationProperty.PositionX, 0f, 0f);
        document.AddKey(target, AnimationProperty.PositionX, 1f, 2f);

        Assert.Equal(2, document.KeyCount);
        Assert.Equal(1f, AnimationClipDocument.Evaluate(target, AnimationProperty.PositionX, 0.5f), 3);

        // ⚠ Each of the three calls is one undo entry, which is what makes a drag one press of
        // Ctrl+Z rather than one per key.
        document.Stack.Undo();
        Assert.Equal(1, document.KeyCount);

        document.Stack.Redo();

        var baked = document.Clip.ToClipData();

        Assert.Single(baked.Channels);
        Assert.Equal(2, baked.Channels[0].PositionTimes.Length);
        Assert.Equal(2f, baked.Channels[0].Positions[1].X, 3);
    }

    [Fact]
    public void DeletingEveryKeyRemovesTheCurveRatherThanLeavingAnEmptyOne() {
        using var fixture = new EditorFixture();

        var document = new AnimationClipDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Empty.vxanim", string.Empty)
        );

        document.AddTarget("Thing");

        var target = document.Target("Thing")!;

        document.AddKey(target, AnimationProperty.ScaleX, 0f, 1f);
        document.SetCurve(target, AnimationProperty.ScaleX, []);

        Assert.Empty(target.Curves);

        // A group with no curves produces no keys, which is different from one holding the rest
        // pose — see AnimationClipAsset.ToClipData.
        Assert.Empty(document.Clip.ToClipData().Channels[0].ScaleTimes);
    }

    /// <summary>A weight curve is bound by the shape's name, and each shape is its own row.</summary>
    /// <remarks>
    ///     ⚠ <b>The pair identifies a curve, not the property.</b> A morphed mesh's node carries one
    ///     weight curve per shape and every one of them is <c>Weight</c>, so a lookup by property
    ///     alone finds whichever the list happens to hold first — and every edit lands on it. This is
    ///     the assertion that keeps <c>Curve</c>, <c>SetCurve</c>, <c>AddKey</c> and <c>Evaluate</c>
    ///     taking both halves.
    /// </remarks>
    [Fact]
    public void EachBlendShapeIsItsOwnWeightCurveOnTheSameTarget() {
        using var fixture = new EditorFixture();

        var document = new AnimationClipDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Face.vxanim", string.Empty)
        );

        document.AddTarget("Head");

        var target = document.Target("Head")!;

        document.AddKey(target, AnimationProperty.Weight, 0f, 0f, "jawOpen");
        document.AddKey(target, AnimationProperty.Weight, 2f, 1f, "jawOpen");
        document.AddKey(target, AnimationProperty.Weight, 0f, 0.25f, "browRaise");

        Assert.Equal(2, target.Curves.Count);
        Assert.Equal(2, AnimationClipDocument.Curve(target, AnimationProperty.Weight, "jawOpen")!.Keys.Count);
        Assert.Single(AnimationClipDocument.Curve(target, AnimationProperty.Weight, "browRaise")!.Keys);

        // Sampled with the shape too, so the two curves cannot be read as one.
        Assert.Equal(0.5f, AnimationClipDocument.Evaluate(target, AnimationProperty.Weight, 1f, "jawOpen"), 3);
        Assert.Equal(0.25f, AnimationClipDocument.Evaluate(target, AnimationProperty.Weight, 1f, "browRaise"), 3);
    }

    /// <summary>The bake: one named scalar channel per shape, exactly as an import would produce.</summary>
    [Fact]
    public void AWeightCurveBakesToTheNamedScalarChannelAnImportWouldHaveWritten() {
        using var fixture = new EditorFixture();

        var document = new AnimationClipDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Face.vxanim", string.Empty)
        );

        document.AddTarget("Head");

        var target = document.Target("Head")!;

        document.AddKey(target, AnimationProperty.Weight, 0f, 0f, "jawOpen");
        document.AddKey(target, AnimationProperty.Weight, 2f, 1f, "jawOpen");
        document.AddKey(target, AnimationProperty.Weight, 0f, 0.5f, "browRaise");

        var weighted = document.Clip.ToClipData()
            .Channels.Where(channel => channel.WeightTimes.Length > 0)
            .ToArray();

        Assert.Equal(2, weighted.Length);

        var jaw = Assert.Single(weighted, channel => channel.Shape == "jawOpen");

        Assert.Equal("Head", jaw.Target);
        Assert.Equal<float>([0f, 2f], jaw.WeightTimes);
        Assert.Equal<float>([0f, 1f], jaw.Weights);

        var brow = Assert.Single(weighted, channel => channel.Shape == "browRaise");

        Assert.Equal<float>([0.5f], brow.Weights);
    }

    /// <summary>
    ///     ⚠ A target that drives only shapes emits no transform channel, and the reason is a number
    ///     nobody would think to look at.
    /// </summary>
    /// <remarks>
    ///     A weight curve names the morphed mesh's <em>node</em>, which is not a joint and never will
    ///     be. An empty transform channel beside the weight ones would be free everywhere except in
    ///     <c>AnimationClip.UnresolvedChannels</c> — the count somebody watches to notice a clip being
    ///     played on the wrong rig — where a perfectly correct facial clip would contribute one per
    ///     face. The imported path avoids this by construction, because Assimp keeps node transforms
    ///     and morph weights in different lists; the authored path has to avoid it on purpose, which
    ///     is why this is asserted through a bake against a rig rather than by counting channels.
    /// </remarks>
    [Fact]
    public void AClipThatOnlyDrivesShapesReportsNoUnresolvedChannels() {
        using var fixture = new EditorFixture();

        var document = new AnimationClipDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Face.vxanim", string.Empty)
        );

        document.AddTarget("HeadMesh");

        var target = document.Target("HeadMesh")!;

        document.AddKey(target, AnimationProperty.Weight, 0f, 0f, "jawOpen");
        document.AddKey(target, AnimationProperty.Weight, 1f, 1f, "jawOpen");

        // A rig that has never heard of "HeadMesh", which is every rig: it is a mesh node.
        var skeleton = Skeleton.Create(
            new SkeletonData {
                Name = "Rig",
                Joints = [new() { Name = "Root", Parent = -1, InverseBindPose = Matrix4x4.Identity }]
            }
        );

        var clip = AnimationClip.Create(document.Clip.ToClipData(), skeleton);

        Assert.Equal(0, clip.UnresolvedChannels);
        Assert.Equal(1, clip.ShapeCount);
        Assert.Equal("jawOpen", clip.Shapes[0]);
    }

    /// <summary>
    ///     ⚠ The version in the file is what the clip <em>uses</em>, and version 2 is a fence rather
    ///     than a re-import trigger.
    /// </summary>
    /// <remarks>
    ///     <c>YamlSerializer</c> binds an enum with <c>Enum.Parse</c>, which throws on a name it does
    ///     not have — so a version-1 build meeting <c>property: Weight</c> fails, and the fence turns
    ///     that into a sentence. Stamping every clip 2 would lock a project's other clips out of an
    ///     older build for a member none of them uses, so a clip with no weight curve still says 1.
    /// </remarks>
    [Fact]
    public void AClipIsWrittenAtVersionTwoOnlyOnceItCarriesAWeightCurve() {
        using var fixture = new EditorFixture();

        var document = new AnimationClipDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Face.vxanim", string.Empty)
        );

        document.AddTarget("Head");

        var target = document.Target("Head")!;

        document.AddKey(target, AnimationProperty.PositionY, 0f, 1f);

        Assert.Equal(1, document.Clip.MinimumVersion);
        Assert.Contains("version: 1", document.Clip.ToYaml(), StringComparison.Ordinal);

        document.AddKey(target, AnimationProperty.Weight, 0f, 1f, "jawOpen");

        Assert.Equal(2, document.Clip.MinimumVersion);

        var yaml = document.Clip.ToYaml();

        Assert.Contains("version: 2", yaml, StringComparison.Ordinal);

        // And it reads back with the shape intact, which is the half a version number is about.
        var reopened = AnimationClipAsset.FromYaml(yaml);
        var curve = Assert.Single(reopened.Targets[0].Curves, entry => entry.Property == AnimationProperty.Weight);

        Assert.Equal("jawOpen", curve.Shape);
    }

    [Fact]
    public void AClipRoundTripsItsEventsAndItsTangents() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Walk.vxanim", string.Empty);
        var document = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);

        document.AddTarget("Root");

        var target = document.Target("Root")!;

        document.SetCurve(
            target,
            AnimationProperty.PositionY,
            [new() { Time = 0.25f, Value = 1.5f, Mode = TangentMode.Constant }]
        );

        document.AddEvent("Footstep", 0.5f);
        document.Save();

        var reopened = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(reopened.LoadError);
        Assert.Single(reopened.Clip.Events);
        Assert.Equal(0.5f, reopened.Clip.ToEvents()[0].Time, 3);
        Assert.Equal(
            TangentMode.Constant,
            AnimationClipDocument.Curve(reopened.Target("Root")!, AnimationProperty.PositionY)!.Keys[0].Mode
        );
    }

    // ── The constraint track ────────────────────────────────────────────────────────────────────

    [Fact]
    public void AConstraintIsPlacedDraggedAndEditedInOneUndoEntryEach() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Reach.vxanim", string.Empty);
        var document = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);

        var tag = document.AddConstraint(
            new() { Name = "right hand", Kind = GoalKind.Position, Effector = "hand_r", Begin = 0.2f, End = 0.6f }
        );

        document.MoveConstraint(tag, 0.3f, 0.7f);

        document.SetConstraintField(
            tag,
            "Set Priority",
            static entry => entry.Priority,
            static (entry, value) => entry.Priority = value,
            "contact"
        );

        Assert.Equal(0.3f, tag.Begin);
        Assert.Equal("contact", tag.Priority);

        document.Stack.Undo();
        Assert.Equal(string.Empty, tag.Priority);

        document.Stack.Undo();
        Assert.Equal(0.2f, tag.Begin);

        document.Stack.Undo();
        Assert.Empty(document.Clip.Constraints);
    }

    [Fact]
    public void AConstraintTrackSurvivesASaveAndReopen() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Reach.vxanim", string.Empty);
        var document = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);

        document.AddConstraint(
            new() {
                Name = "left hand on the rail",
                Kind = GoalKind.Position,
                Effector = "hand_l",
                Chain = "upperarm_l",
                Begin = 0.1f,
                End = 0.9f,
                Priority = "contact",
                Region = new(0.05f, 0.01f, 0.05f),
                Goal = new() {
                    Kind = ConstraintFrameKind.Surface,
                    Shape = "rail",
                    Origin = OriginSource.Surface,
                    Face = -1,
                    U = 0.25f,
                    V = 0.6f
                }
            }
        );

        document.Save();

        var reopened = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);
        var tag = Assert.Single(reopened.Clip.Constraints);

        Assert.Equal("left hand on the rail", tag.Name);
        Assert.Equal("upperarm_l", tag.Chain);
        Assert.Equal("contact", tag.Priority);
        Assert.Equal(ConstraintFrameKind.Surface, tag.Goal.Kind);
        Assert.Equal(0.6f, tag.Goal.V, 3);
        Assert.Equal(0.05f, tag.Region.X, 3);
    }

    /// <summary>⚠ The bar's ramp has to be the ramp the game plays, or it is worse than no ramp.</summary>
    [Fact]
    public void TheTrackDrawsTheActivationTheRuntimeWillUse() {
        var tag = new ConstraintTagRecord { Begin = 0.2f, End = 0.8f, EaseIn = 0.15f, EaseOut = 0.15f };

        Assert.Equal(0f, AnimationClipDocument.Activation(tag, 0.1f));
        Assert.Equal(0.5f, AnimationClipDocument.Activation(tag, 0.275f), 2);
        Assert.Equal(1f, AnimationClipDocument.Activation(tag, 0.5f), 3);
        Assert.Equal(0f, AnimationClipDocument.Activation(tag, 0.9f));
    }

    [Fact]
    public void ATemplateIsAppliedAsOneUndoEntryAndReapplyLeavesHandPlacedTagsAlone() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Sit.vxanim", string.Empty);
        var document = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);

        var template = new ConstraintTemplateContent {
            Name = "seated",
            Revision = 1,
            Tags = [
                new() { Name = "right hand", Effector = "hand_r", Begin = 0f, End = 1f },
                new() { Name = "hips", Effector = "pelvis", Begin = 0f, End = 1f }
            ]
        };

        document.ApplyTemplate(template, 0.25f, 0.75f);
        Assert.Equal(2, document.Clip.Constraints.Count);

        // ⚠ Twenty tags as twenty undo steps is twenty presses to take back one decision.
        document.Stack.Undo();
        Assert.Empty(document.Clip.Constraints);
        document.Stack.Redo();

        document.AddConstraint(new() { Name = "chin", Effector = "head" });

        template.Revision = 2;
        template.Tags[0].MaxWeight = 0.4f;

        var diff = document.CompareTemplate(template, 0.25f, 0.75f);

        Assert.Equal(1, diff.Changed);
        Assert.Equal(0, diff.Removed);

        document.ReapplyTemplate(template, 0.25f, 0.75f);

        Assert.Equal(3, document.Clip.Constraints.Count);
        Assert.Contains(document.Clip.Constraints, entry => entry.Name == "chin" && entry.Template.Length == 0);
        Assert.Contains(document.Clip.Constraints, entry => entry.Name == "right hand" && entry.MaxWeight == 0.4f);

        document.Stack.Undo();
        Assert.Contains(document.Clip.Constraints, entry => entry.Name == "right hand" && entry.MaxWeight == 1f);
    }

    /// <summary>
    ///     ⚠ Every field the schema names has an accessor, or the panel shows it and cannot write it.
    /// </summary>
    /// <remarks>
    ///     The table is explicit rather than reflected, for the reason <c>BuiltInImporters</c> gives
    ///     about scanning — so this is the check that stops the two drifting, and it is not optional.
    /// </remarks>
    [Fact]
    public void EveryGeneratedFieldCanBeReadAndWritten() {
        List<string> orphans = [];

        foreach (var field in GoalKindSchema.Common) {
            if (!ConstraintFieldAccess.TryGet(field.Property, out _)) {
                orphans.Add(field.Property);
            }
        }

        foreach (var kind in Enum.GetValues<GoalKind>()) {
            foreach (var field in GoalKindSchema.For(kind)) {
                if (!ConstraintFieldAccess.TryGet(field.Property, out _)) {
                    orphans.Add(field.Property);
                }
            }
        }

        Assert.True(orphans.Count == 0, $"the panel would show and could not write: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void AGeneratedFieldWritesThroughTheUndoStackAndRejectsNonsense() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Reach.vxanim", string.Empty);
        var document = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);
        var tag = document.AddConstraint(new() { Kind = GoalKind.Position, Effector = "hand_r", MaxWeight = 1f });

        Assert.True(ConstraintFieldAccess.TryGet("MaxWeight", out var weight));
        Assert.True(weight.Write(document, tag, Field("MaxWeight"), "0.4"));
        Assert.Equal(0.4f, tag.MaxWeight, 3);

        document.Stack.Undo();
        Assert.Equal(1f, tag.MaxWeight, 3);

        // ⚠ A number that does not parse leaves the tag alone and says so, rather than writing zero.
        Assert.False(weight.Write(document, tag, Field("MaxWeight"), "quite a lot"));
        Assert.Equal(1f, tag.MaxWeight, 3);
    }

    [Fact]
    public void AFrameRoundTripsThroughTheOneLineItIsShownAs() {
        foreach (var text in new[] {
            "World 1 2 3",
            "Joint hand_r",
            "Entity held-item",
            "Socket held-item grip",
            "Provided ledge",
            "Attachment right-hand-grip",
            "Surface belly 0.25 0.6"
        }) {
            var frame = ConstraintFieldAccess.Parse(text);

            Assert.NotNull(frame);
            Assert.Equal(text, ConstraintFieldAccess.Describe(frame!));
        }

        Assert.Null(ConstraintFieldAccess.Parse("Nonsense here"));
    }

    static GoalField Field(string property) {
        foreach (var field in GoalKindSchema.Common) {
            if (field.Property == property) {
                return field;
            }
        }

        throw new InvalidOperationException(property);
    }

    // ── The animation graph document ────────────────────────────────────────────────────────────

    [Fact]
    public void RenamingAStateMovesTheTransitionsIntoIt() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Hero.vxanimgraph", string.Empty);
        var document = new AnimationGraphDocument(fixture.Project, AssetId.Empty, path);

        var layer = document.Layer(0)!;
        var idle = layer.States[0];
        var walk = document.AddState(layer, "Walk", 300f, 40f);

        document.AddTransition(idle, walk.Name);

        document.Edit("Rename State", () => {
            walk.Name = "Run";

            foreach (var leaving in idle.Transitions) {
                if (string.Equals(leaving.To, "Walk", StringComparison.Ordinal)) {
                    leaving.To = "Run";
                }
            }
        });

        Assert.Equal("Run", document.Layer(0)!.States[1].Name);
        Assert.Equal("Run", document.Layer(0)!.States[0].Transitions[0].To);
        Assert.DoesNotContain(document.Compile()!.Diagnostics, diagnostic => diagnostic.Id == "AG0011");
    }

    [Fact]
    public void RemovingAStateTakesTheTransitionsIntoItAndUndoPutsThemBack() {
        using var fixture = new EditorFixture();

        var document = new AnimationGraphDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Enemy.vxanimgraph", string.Empty)
        );

        var layer = document.Layer(0)!;
        var idle = layer.States[0];
        var attack = document.AddState(layer, "Attack", 300f, 40f);

        document.AddTransition(idle, attack.Name);
        document.RemoveState(layer, attack);

        Assert.Single(layer.States);
        Assert.Empty(idle.Transitions);

        document.Stack.Undo();

        Assert.Equal(2, layer.States.Count);
        Assert.Single(idle.Transitions);
    }

    // ── The input editor ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AnInputAssetRoundTripsThroughTheRuntimeReaderAndWriter() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Controls.vxinput", string.Empty);
        var document = new InputActionsDocument(fixture.Project, AssetId.Empty, path);

        document.AddBinding("Player", "Move", new(string.Empty, Composite: InputCompositeKind.Vector2, Parts: [
            new("up", "<Keyboard>/w"),
            new("down", "<Keyboard>/s"),
            new("left", "<Keyboard>/a"),
            new("right", "<Keyboard>/d")
        ]));

        document.Save();

        var result = InputActionAssetReader.Read(File.ReadAllText(path), "Controls");

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));

        var action = result.Asset!.Maps[0].Actions[0];

        Assert.Equal("Move", action.Name);
        Assert.Single(action.Bindings);
        Assert.Equal(InputCompositeKind.Vector2, action.Bindings[0].Composite);
        Assert.Equal(4, action.Bindings[0].Parts.Count);
    }

    [Fact]
    public void EveryInputEditIsOneUndoEntry() {
        using var fixture = new EditorFixture();

        var document = new InputActionsDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Menu.vxinput", string.Empty)
        );

        document.AddMap("UI");
        document.AddAction("UI", "Submit");

        Assert.Equal(2, document.Actions.Maps.Count);

        document.Stack.Undo();
        Assert.Empty(document.Actions.Maps[1].Actions);

        document.Stack.Undo();
        Assert.Single(document.Actions.Maps);
    }

    // ── The mixer ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ANewMixerBuildsAgainstTheRealBuilder() {
        using var fixture = new EditorFixture();

        var document = new AudioMixerDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Main.vxmixer", string.Empty)
        );

        Assert.Empty(document.Validate());
        Assert.Equal(3, document.Mixer.Buses.Length);
    }

    [Fact]
    public void RemovingABusTakesTheSendsIntoItWithIt() {
        using var fixture = new EditorFixture();

        var document = new AudioMixerDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Game.vxmixer", string.Empty)
        );

        document.AddBus("Reverb");
        document.AddSend("SFX", "Reverb");

        Assert.Single(document.Mixer.Buses.First(bus => bus.Name == "SFX").Sends);

        document.RemoveBus("Reverb");

        Assert.Empty(document.Mixer.Buses.First(bus => bus.Name == "SFX").Sends);
        Assert.Empty(document.Validate());
    }

    [Fact]
    public void ASnapshotNamesOnlyTheBusesItChanges() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Under.vxmixer", string.Empty);
        var document = new AudioMixerDocument(fixture.Project, AssetId.Empty, path);

        document.AddSnapshot("Underwater");
        document.SetSnapshotBus("Underwater", "Music", -12f, muted: false);

        Assert.Single(document.Mixer.Snapshots[0].Buses);

        document.Save();

        var reopened = new AudioMixerDocument(fixture.Project, AssetId.Empty, path);

        Assert.Single(reopened.Mixer.Snapshots);
        Assert.Equal(-12f, reopened.Mixer.Snapshots[0].Buses[0].GainDb, 3);
        Assert.Empty(reopened.Validate());
    }

    // ── The sequencer ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ASequenceScrubsAnActorAndPutsItBack() {
        using var fixture = new EditorFixture();
        using var world = new World("Sequencer");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Shot");
        var actor = scene.Add("Actor", LocalTransform.At(new(0f, 0f, 0f)));

        var document = new SequenceDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Intro.vxseq", string.Empty)
        );

        document.Attach(scene);

        var track = new SequenceTrackData { Kind = SequenceTrackKind.Transform, Target = scene.IdOf(actor) };

        document.AddTrack(track);

        document.AddKey(track, new() { Time = 0f, Value = SequencePlayer.Write(LocalTransform.At(new(0f, 0f, 0f))) });
        document.AddKey(track, new() { Time = 2f, Value = SequencePlayer.Write(LocalTransform.At(new(10f, 0f, 0f))) });

        var player = document.Player!;

        player.Begin();

        Assert.Equal(1, player.Apply(1f));
        Assert.Equal(5f, world.Get<LocalTransform>(actor).Position.X, 3);

        // ⚠ Scrubbing back gives the same answer as scrubbing forward, which is the property the
        // whole player is arranged around.
        player.Apply(0.5f);
        Assert.Equal(2.5f, world.Get<LocalTransform>(actor).Position.X, 3);

        player.Restore();
        Assert.Equal(0f, world.Get<LocalTransform>(actor).Position.X, 3);
    }

    [Fact]
    public void AnEventFiresOnTheIntervalCrossedAndNotOnASeekBack() {
        using var fixture = new EditorFixture();
        using var world = new World("Events");

        var scene = new SceneDocument(fixture.Project, world, AssetId.Empty, "Shot");

        var document = new SequenceDocument(
            fixture.Project,
            AssetId.Empty,
            fixture.Write("Assets/Cue.vxseq", string.Empty)
        );

        document.Attach(scene);

        var track = new SequenceTrackData { Kind = SequenceTrackKind.Event, Name = "Cues" };

        document.AddTrack(track);
        document.AddKey(track, new() { Time = 1f, Text = "Explosion" });

        var player = document.Player!;

        player.Begin();

        player.Apply(0.5f, 0f);
        Assert.Empty(player.Signals);

        player.Apply(1.5f, 0.5f);
        Assert.Single(player.Signals);
        Assert.Equal("Explosion", player.Signals[0].Name);

        // Backwards fires nothing, deliberately: an editor that raised a cue because somebody
        // dragged the playhead over it would make a scrub audibly different from a play.
        player.Apply(0.5f, 1.5f);
        Assert.Empty(player.Signals);
    }

    [Fact]
    public void ASequenceRoundTripsItsTracks() {
        using var fixture = new EditorFixture();

        var path = fixture.Write("Assets/Cinematic.vxseq", string.Empty);
        var document = new SequenceDocument(fixture.Project, AssetId.Empty, path);
        var id = new EntityId(Guid.NewGuid());

        document.AddTrack(new() { Kind = SequenceTrackKind.Transform, Target = id, Name = "Hero" });
        document.SetDuration(12.5f);
        document.Save();

        var reopened = new SequenceDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(reopened.LoadError);
        Assert.Single(reopened.Sequence.Tracks);
        Assert.Equal(id, reopened.Sequence.Tracks[0].Target);
        Assert.Equal(12.5f, reopened.Sequence.Duration, 3);
    }

    // ── The registry ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void EveryNewExtensionIsClaimedByExactlyOneEditor() {
        var registry = StandardEditors.CreateWorldless();

        foreach (var extension in (string[]) [".vxvfx", ".vxanim", ".vxanimgraph", ".vxseq", ".vxinput", ".vxmixer", ".vxfont"]) {
            Assert.True(registry.TryGetForFile("Thing" + extension, out var editor), extension);
            Assert.NotNull(editor);
        }
    }
}
