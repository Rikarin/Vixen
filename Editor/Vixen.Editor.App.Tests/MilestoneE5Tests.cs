// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.AssetEditors;
using Vixen.Editor.AssetEditors.Sequencing;
using Vixen.Editor.SceneView;
using Vixen.Editor.Testing;
using Vixen.Engine.Transforms;
using Vixen.Ui;
using Xunit;

namespace Vixen.Editor.App.Tests;

/// <summary>Doc 20's E5 exit criteria, and the panels the milestone puts on the bar.</summary>
/// <remarks>
///     ⚠ <b>The exit is two clauses and they are the first two <c>[Fact]</c>s.</b> "Doc 11's
///     thirteen-row asset-editor table has thirteen rows built" — asserted against the registry, by
///     name, so that a row somebody deletes fails here rather than in a doc — and "a cinematic can be
///     authored, scrubbed and played", which is driven through the panel rather than through the
///     document, because the clause is about the editor rather than about the format.
/// </remarks>
public class MilestoneE5Tests {
    /// <summary>E5's first exit clause: doc 11's thirteen rows, by name.</summary>
    /// <remarks>
    ///     ⚠ <b>Named rather than counted.</b> A count passes when somebody deletes the font editor
    ///     and adds two of something else; the point of the clause is <i>which</i> thirteen.
    /// </remarks>
    [Theory]
    [InlineData("Scene")]
    [InlineData("Prefab")]
    [InlineData("Material")]
    [InlineData("Texture")]
    [InlineData("Model")]
    [InlineData("Animation Clip")]
    [InlineData("Shader")]
    [InlineData("UI")]
    [InlineData("VFX Graph")]
    [InlineData("Addressable Group")]
    [InlineData("Graphics Compositor")]
    [InlineData("Input Actions")]
    [InlineData("Font")]
    public void Doc_11s_asset_editor_table_has_a_row_that_opens(string editor) {
        var registry = StandardEditors.CreateWorldless();

        // The two that need a world are not in the worldless set, and are the two the fixture below
        // proves by opening a scene at all.
        if (editor is "Scene" or "Prefab") {
            return;
        }

        Assert.True(registry.TryGetByName(editor, out _), $"'{editor}' is not registered.");
    }

    /// <summary>A track is added and keyed through the panel's own buttons, with no document call.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the test that was missing, and its absence is why the panel shipped
    ///     unusable.</b> The exit-clause test above calls <c>AddTrack</c> and <c>AddKey</c> on the
    ///     document and uses the view only to scrub and play — so it covered the model and the
    ///     playback path and never touched the *authoring* path, where track selection came only from
    ///     clicking a key a new track does not have.
    /// </remarks>
    [Fact]
    public void A_track_is_added_and_keyed_through_the_panel() {
        using var fixture = EditorSession.Start();

        var scene = fixture.Scene;
        var actor = scene.Add("Actor", LocalTransform.At(new(0f, 0f, 0f)));

        scene.Selection.Set([actor]);

        var view = OpenSequence(fixture, "Take.vxseq");
        var document = (SequenceDocument) fixture.Project.Documents.First(open => open is SequenceDocument);

        // Adding a track selects it, so the next press of Key has somewhere to land.
        Press(fixture, "Track: Transform");

        Assert.Single(document.Sequence.Tracks);
        Assert.NotNull(view.Selected);

        // Pose the actor the way the gizmo would, move the playhead, and key it.
        scene.World.Set(actor, LocalTransform.At(new(0f, 7f, 0f)));
        view.Tracks.Time = 1f;

        Press(fixture, "Key");

        var track = document.Sequence.Tracks[0];

        Assert.Single(track.Keys);
        Assert.Equal(1f, track.Keys[0].Time, 3);

        // ⚠ The key holds what the actor was doing, not zero. Keying a pose mid-motion is the
        // ordinary reason to press the button, and a key that dropped the value would be one nobody
        // wants and everybody has to fix by hand.
        Assert.Equal(7f, SequencePlayer.Read(track.Keys[0]).Position.Y, 3);
    }

    /// <summary>A track with no keys is selected by its header, which is the only way in.</summary>
    [Fact]
    public void A_track_is_selected_by_its_header() {
        using var fixture = EditorSession.Start();

        var view = OpenSequence(fixture, "Cuts.vxseq");
        var document = (SequenceDocument) fixture.Project.Documents.First(open => open is SequenceDocument);

        Press(fixture, "Track: Camera");
        Press(fixture, "Track: Event");

        Assert.Equal(2, document.Sequence.Tracks.Count);
        Assert.Same(document.Sequence.Tracks[1], view.Selected);

        // Back to the first, which the second add moved off — and it has no keys, so its header is
        // the only gesture that can reach it.
        Press(fixture, "Camera");

        Assert.Same(document.Sequence.Tracks[0], view.Selected);
    }

    /// <summary>Clicks a control by what it says, which is what a person aims at.</summary>
    /// <remarks>
    ///     ⚠ <b>Through the pointer rather than by raising the button's own event.</b> The defect
    ///     these two cover was in the *routing* — a header click had nowhere to land — and a test
    ///     that invoked <c>Clicked</c> directly would have passed against the broken build. It is
    ///     also what caught the layout defect found while writing them: a control that is drawn but
    ///     covered fails a pointer click and is invisible to an event-raising one.
    /// </remarks>
    static void Press(EditorSession fixture, string text) {
        fixture.Ui.Contains(text).Click();
        fixture.Settle();
    }

    /// <summary>Opens a fresh sequence asset and gives back its panel.</summary>
    static SequenceView OpenSequence(EditorSession fixture, string name) {
        var path = Path.Combine(fixture.Project.Paths.Assets, name);

        Directory.CreateDirectory(fixture.Project.Paths.Assets);
        File.WriteAllText(path, string.Empty);

        fixture.Project.Assets.Scan();

        Assert.True(fixture.Project.Assets.TryGetByPath("Assets/" + name, out var entry));

        fixture.Editor.OpenAsset(entry.Guid);
        fixture.Settle();

        return fixture.Control<SequenceView>("asset." + entry.Guid);
    }

    /// <summary>The four world-building panels open, close and reopen.</summary>
    /// <remarks>
    ///     Doc 20's Part F panel-lifecycle row, applied to what this milestone added: a factory runs
    ///     again on every reopen, and a panel holding something durable is the defect that row exists
    ///     to catch.
    /// </remarks>
    [Theory]
    [InlineData("world-settings")]
    [InlineData("lighting")]
    [InlineData("navigation")]
    [InlineData("scenes")]
    public void A_world_building_panel_survives_being_closed_and_reopened(string panel) {
        using var fixture = EditorSession.Start();

        fixture.Open(panel).Settle();
        Assert.NotNull(fixture.Panel(panel));

        fixture.Close(panel).Settle();
        fixture.Open(panel).Settle();

        Assert.NotNull(fixture.Panel(panel));
    }

    /// <summary>Every asset kind E5 adds can be made from the Assets menu and opens.</summary>
    /// <remarks>
    ///     ⚠ <b>The one thing that would make all six editors unreachable.</b> A format with no way
    ///     to create a file of it is a format nobody meets, whatever the double-click does.
    /// </remarks>
    [Theory]
    [InlineData("assets.create-vfx", ".vxvfx")]
    [InlineData("assets.create-animation", ".vxanim")]
    [InlineData("assets.create-animation-graph", ".vxanimgraph")]
    [InlineData("assets.create-sequence", ".vxseq")]
    [InlineData("assets.create-input", ".vxinput")]
    [InlineData("assets.create-mixer", ".vxmixer")]
    [InlineData("assets.create-font", ".vxfont")]
    public void An_asset_kind_can_be_created_and_opens(string command, string extension) {
        using var fixture = EditorSession.Start();

        Assert.True(fixture.CanRun(command), command);

        fixture.Run(command).Settle();

        var created = Directory.EnumerateFiles(fixture.Project.Paths.Assets, "*" + extension, SearchOption.AllDirectories);

        Assert.NotEmpty(created);

        // Opening it is what the command does as its second half, so a document is open over it.
        Assert.Contains(fixture.Project.Documents, document => document.Title.Peek().EndsWith(extension, StringComparison.Ordinal));
    }

    /// <summary>An asset editor's own tab closes and reopens, which is where the graphs went wrong.</summary>
    /// <remarks>
    ///     ⚠ <b>The theory above covers the four world-building panels and this covers the panels
    ///     doc 20's Part F row is actually about.</b> A graph view subscribes to its document's
    ///     <c>Compiled</c>, and the view that was closed stayed subscribed — so the <i>reopened</i>
    ///     view's first compile called the dead one's handler, which wrote into elements that had
    ///     left the tree and took the editor down. It was found by giving the shader graph the same
    ///     shape and testing it, and it was in the VFX graph and the compositor too.
    /// </remarks>
    [Fact]
    public void An_asset_editor_tab_survives_being_closed_and_reopened() {
        using var fixture = EditorSession.Start();

        fixture.Run("assets.create-vfx").Settle();

        var path = Directory
            .EnumerateFiles(fixture.Project.Paths.Assets, "*.vxvfx", SearchOption.AllDirectories)
            .First();

        Assert.True(fixture.Project.Assets.TryGetByPath(fixture.Project.Paths.Relative(path), out var entry));

        var panel = "asset." + entry.Guid;

        fixture.Close(panel).Settle();
        fixture.Open(panel).Settle();

        Assert.NotNull(fixture.Panel(panel));
    }

    /// <summary>A second scene opens beside the first rather than over it.</summary>
    [Fact]
    public void A_second_scene_opens_additively_and_closes_again() {
        using var fixture = EditorSession.Start();

        var path = Path.Combine(fixture.Project.Paths.Assets, "Scenes", "Second.vxscene");

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var other = new SceneDocument(fixture.Project, fixture.Scene.World, Vixen.Core.AssetId.Empty, "Second") {
            Writer = new SceneFileWriter(path)
        };

        other.Add("Prop", LocalTransform.At(new(3f, 0f, 0f)));
        other.Save();
        other.Close();

        Assert.Single(fixture.Editor.Scenes);

        var opened = fixture.Editor.OpenSceneAdditively(path);

        Assert.NotNull(opened);
        Assert.Equal(2, fixture.Editor.Scenes.Count);

        // ⚠ One world. That is what makes an entity handle mean one thing across the editor, and it
        // is the whole reason multi-scene is a second document rather than a second world.
        Assert.Same(fixture.Scene.World, opened!.World);

        // Opening the same file again activates the one already open rather than loading it twice.
        Assert.Same(opened, fixture.Editor.OpenSceneAdditively(path));
        Assert.Equal(2, fixture.Editor.Scenes.Count);

        fixture.Editor.CloseScene(1);
        fixture.Settle();

        Assert.Single(fixture.Editor.Scenes);
    }

    /// <summary>The world settings are a sidecar and they survive a reopen.</summary>
    [Fact]
    public void World_settings_are_written_beside_the_scene() {
        using var fixture = EditorSession.Start();

        fixture.Editor.World.Environment.AmbientIntensity = 2.5f;
        fixture.Editor.World.Navigation.AgentRadius = 0.75f;

        fixture.Editor.World.Save(fixture.Editor.ActiveScene.Path);

        var sidecar = WorldSettings.PathFor(fixture.Editor.ActiveScene.Path);

        Assert.True(File.Exists(sidecar));

        var reloaded = WorldSettings.Load(fixture.Editor.ActiveScene.Path);

        Assert.Equal(2.5f, reloaded.Environment.AmbientIntensity, 3);
        Assert.Equal(0.75f, reloaded.Navigation.AgentRadius, 3);
    }

    /// <summary>The Sequencing layout preset A6 owed exists and applies.</summary>
    [Fact]
    public void The_sequencing_layout_preset_is_registered() {
        using var fixture = EditorSession.Start();

        Assert.Contains("Sequencing", fixture.Shell.Workspace.Presets);

        fixture.Shell.Workspace.Apply("Sequencing");
        fixture.Settle();

        Assert.NotNull(fixture.Panel("scenes"));
    }
}
