// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Audio.Assets;
using Vixen.Core;
using Vixen.Ecs;
using Vixen.Editor.AssetEditors.Animation;
using Vixen.Editor.AssetEditors.Audio;
using Vixen.Editor.AssetEditors.Input;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.AssetEditors.Vfx;
using Vixen.Editor.Core.Scenes;
using Vixen.Editor.SceneView;
using Vixen.Engine.Transforms;
using Vixen.Input;
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
            [new() { Time = 0.25f, Value = 1.5f, Mode = Ui.Controls.Advanced.TangentMode.Constant }]
        );

        document.AddEvent("Footstep", 0.5f);
        document.Save();

        var reopened = new AnimationClipDocument(fixture.Project, AssetId.Empty, path);

        Assert.Null(reopened.LoadError);
        Assert.Single(reopened.Clip.Events);
        Assert.Equal(0.5f, reopened.Clip.ToEvents()[0].Time, 3);
        Assert.Equal(
            Ui.Controls.Advanced.TangentMode.Constant,
            AnimationClipDocument.Curve(reopened.Target("Root")!, AnimationProperty.PositionY)!.Keys[0].Mode
        );
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
